using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// The single evidence collector confirming candidates against recording-level
    /// data (§6.1/§6.3's CorroborationTier.* family). Superseded CorroborationTier/
    /// WorkRelationship/RecordingRelationship EvidenceCollectors 2026-07-17.
    ///
    /// REWRITTEN 2026-07-23 from IObservationEvidenceCollector (per-candidate) to
    /// IRoundBasedObservationEvidenceCollector (all-candidates-jointly, round by
    /// round). Motivating problem: a per-candidate Lookup() loop meant a single
    /// high-collision-name observation (e.g. 25 candidates named "Queen") triggered a
    /// full relationship-scan walk for EVERY candidate before SequentialSampler's
    /// decision gate was checked even once -- the true candidate could already be
    /// confirmed at Tier1 (its own performer credit, zero extra API calls) while 24
    /// decoys still each paid for their own GetRelationships walk regardless. Round-
    /// based collection fixes this at its actual source: RecordingLookup.LookupRounds
    /// shares one recording search and one relationship fetch across every live
    /// candidate at once, and yields incrementally so the caller's decision-gate
    /// check happens between every recording's relationship fetch, not just between
    /// whole observations. See IRoundBasedObservationEvidenceCollector's own doc
    /// comment for why this is a second collector category rather than a change to
    /// the first.
    /// </summary>
    public class RecordingCorroborationEvidenceCollector : IRoundBasedObservationEvidenceCollector<EmbyArtist>
    {
        private readonly RecordingLookup _recordingLookup;

        public RecordingCorroborationEvidenceCollector(IMusicBrainzApiClient client, RecordingLookup recordingLookup, MetadataHealthCheck.v2.Diagnostics.StructuredLogger? logger = null)
        {
            // client/logger params retained for constructor-compatibility with existing
            // plugin wiring (MusicBrainzArtistResolverPlugin.cs) -- neither is used
            // directly here; RecordingLookup owns all client calls and their logging.
            _recordingLookup = recordingLookup;
        }

        public string EvidenceType => "RecordingCorroboration";

        public IReadOnlyList<string> PossibleWeightedEvidenceTypes => new[]
        {
            "CorroborationTier.Tier1",
            "CorroborationTier.Tier2",
            "CorroborationTier.Tier3",
        };

        public IEnumerable<IReadOnlyDictionary<string, IReadOnlyList<EvidenceRecord>>> CollectRounds(EmbyArtist source, IReadOnlyList<Candidate> candidates, IObservationUnit unit, ResolutionContext context)
        {
            if (unit is not EmbyTrackObservationUnit trackUnit) yield break;
            var track = trackUnit.Track;

            // Rung-1/2 search text is the track's own recorded performer credit(s), never
            // the candidate's identity -- see RecordingLookup's own doc comment. All of
            // track.Artists tried as an OR-group; falls back to track.AlbumArtists only if
            // Artists is empty entirely.
            var recordedPerformerNames = track.Artists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (recordedPerformerNames.Count == 0)
                recordedPerformerNames = track.AlbumArtists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            var candidateMbids = candidates.Select(c => c.TargetId).ToList();
            var relationshipMbidsByCandidate = candidates.ToDictionary(
                c => c.TargetId,
                c => (IReadOnlyList<string>)(c.RelationshipMbids?.ToList() ?? new List<string>()));
            var candidateByMbid = candidates.ToDictionary(c => c.TargetId, c => c);

            foreach (var round in _recordingLookup.LookupRounds(candidateMbids, relationshipMbidsByCandidate, track, recordedPerformerNames))
            {
                var output = new Dictionary<string, IReadOnlyList<EvidenceRecord>>();
                foreach (var kvp in round.NewlyConfirmed)
                {
                    var candidateMbid = kvp.Key;
                    var lookup = kvp.Value;
                    if (!candidateByMbid.TryGetValue(candidateMbid, out var candidate)) continue; // shouldn't happen; defensive only
                    output[candidate.Id] = new[] { BuildEvidenceRecord(candidate, track, lookup) };
                }
                if (output.Count > 0) yield return output;
            }
        }

        // Extracted unchanged from the previous per-candidate Collect() -- tier
        // classification derives from lookup.RungReached (§6.1/§6.3), not from a
        // separate exact-string title/album recheck (that recheck was a real bug,
        // fixed 2026-07-18: see git history for the full rationale if needed).
        // TrackArtist rung mapped to Tier2 2026-07-23 (was previously mis-mapped into
        // the Tier3 catch-all) -- TrackDuration remains a deliberate Tier3, being a
        // frequency-inferred match rather than a direct search-field confirmation.
        private static EvidenceRecord BuildEvidenceRecord(Candidate candidate, EmbyTrackCredit track, RecordingLookupResult lookup)
        {
            var rec = lookup.Recording!;

            string tier = lookup.RungReached switch
            {
                RecordingLookupRung.TrackArtistAlbum => "CorroborationTier.Tier1",
                RecordingLookupRung.TrackAlbum => "CorroborationTier.Tier2",
                RecordingLookupRung.TrackArtist => "CorroborationTier.Tier2",
                _ => "CorroborationTier.Tier3",
            };
            string tierDescription = lookup.RungReached switch
            {
                RecordingLookupRung.TrackArtistAlbum => "full-triple (track+artist+album search)",
                RecordingLookupRung.TrackAlbum => "track+album search, no artist filter",
                RecordingLookupRung.TrackArtist => "track+artist search, no album",
                RecordingLookupRung.TrackDuration => "title+duration frequency search",
                _ => "track-only search",
            };
            string aliasNote = lookup.MatchedViaAlias ? " (matched via a registered alias)" : "";

            string confirmationNote;
            bool matchedViaRelationship = lookup.ConfirmedViaRelationship;
            string? relationshipTypeForRecord = null;
            if (lookup.ConfirmedViaRelationship && lookup.ConfirmingRelationship != null)
            {
                var rel = lookup.ConfirmingRelationship;
                relationshipTypeForRecord = rel.RelationshipType;
                bool viaRelationshipMbid = rel.ArtistMbid != candidate.TargetId;
                confirmationNote = viaRelationshipMbid
                    ? $" -- confirmed via a related artist identity's {rel.RelationshipType} relationship ({rel.Level})"
                    : $" -- confirmed via this artist's own {rel.RelationshipType} relationship ({rel.Level}), not performer-credit";
            }
            else
            {
                confirmationNote = " -- confirmed via performer-credit";
            }

            return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = tier,
                RawValue = $"rung={lookup.RungReached} mbScore={rec.Score} exactTitleMatch={rec.TrackTitleMatches} exactAlbumMatch={rec.ReleaseTitleMatches} viaRelationship={matchedViaRelationship}",
                Role = track.Role,
                SourceTrackId = track.TrackId,
                AlbumId = track.AlbumId,
                MatchedViaAlias = lookup.MatchedViaAlias,
                MatchedViaRelationship = matchedViaRelationship,
                RelationshipType = relationshipTypeForRecord,
                Rationale = $"MusicBrainz {tierDescription} corroboration for \"{track.TrackName}\"{aliasNote}{confirmationNote} (rung={lookup.RungReached}, mbScore={rec.Score}).",
            };
        }
    }
}