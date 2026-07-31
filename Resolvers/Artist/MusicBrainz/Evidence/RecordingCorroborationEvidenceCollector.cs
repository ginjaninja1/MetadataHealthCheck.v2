using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// The one evidence collector confirming candidates against recording-level
    /// data (the CorroborationTier.* family). Serves both the AlbumArtist/Artist
    /// pathway and the Composer pathway -- they are not split into separate
    /// collector classes, since the only real difference between them is which
    /// round-type(s) RecordingLookup is allowed to run, not anything about how
    /// evidence is built or reported. ConfirmationModeForBucket decides
    /// PerformerOnly vs RelationshipOnly per observation from its bucket, and
    /// that mode is passed straight into RecordingLookup.LookupRounds.
    ///
    /// Round-based (all live candidates checked jointly, one round at a time)
    /// rather than per-candidate: RecordingLookup.LookupRounds shares one
    /// recording search and one relationship fetch across every live candidate
    /// at once, and yields incrementally so the caller's decision-gate check
    /// happens between every recording's relationship fetch, not just between
    /// whole observations -- the true candidate can confirm at Tier1 (its own
    /// performer credit, zero extra API calls) and stop the sampler before any
    /// decoy candidate's relationship-scan walk ever fires.
    /// </summary>
    public class RecordingCorroborationEvidenceCollector : IRoundBasedObservationEvidenceCollector<EmbyArtist>
    {
        private readonly RecordingLookup _recordingLookup;

        public RecordingCorroborationEvidenceCollector(RecordingLookup recordingLookup)
        {
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

            // Search text is the track's own recorded performer credit(s), never
            // the candidate's identity. All of track.Artists tried as an
            // OR-group; falls back to track.AlbumArtists only if Artists is
            // empty entirely.
            var recordedPerformerNames = track.Artists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (recordedPerformerNames.Count == 0)
                recordedPerformerNames = track.AlbumArtists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            var candidateMbids = candidates.Select(c => c.TargetId).ToList();
            var relationshipMbidsByCandidate = candidates.ToDictionary(
                c => c.TargetId,
                c => (IReadOnlyList<string>)(c.RelationshipMbids?.ToList() ?? new List<string>()));
            var candidateByMbid = candidates.ToDictionary(c => c.TargetId, c => c);

            var mode = ConfirmationModeForBucket(trackUnit.BucketKey);

            foreach (var round in _recordingLookup.LookupRounds(candidateMbids, relationshipMbidsByCandidate, track, recordedPerformerNames, mode))
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

        // A deliberately hardcoded exhaustive switch, not a config-driven lookup
        // table: which round-type(s) apply to a bucket is a statement about
        // what that bucket's evidence IS (a performer-role bucket structurally
        // can't confirm via relationship, a composer-role bucket can't confirm
        // via performer-credit), not a tunable weight or threshold. A new
        // bucket is exactly as much a code change as a new evidence collector
        // would be -- adding one without deciding its confirmation mode here
        // fails loudly (the default arm throws) rather than silently
        // inheriting a mode that was never reasoned about for it.
        private static ConfirmationMode ConfirmationModeForBucket(string bucketKey)
        {
            switch (bucketKey)
            {
                case "AlbumArtist":
                case "Artist":
                    return ConfirmationMode.PerformerOnly;
                case "Composer":
                    return ConfirmationMode.RelationshipOnly;
                default:
                    throw new InvalidOperationException(
                        $"RecordingCorroborationEvidenceCollector has no ConfirmationMode decision for bucket \"{bucketKey}\" -- " +
                        "a new bucket needs a deliberate choice here (PerformerOnly/RelationshipOnly), not a silent default.");
            }
        }

        // Tier classification derives from lookup.RungReached, not from a
        // separate exact-string title/album recheck.
        private static EvidenceRecord BuildEvidenceRecord(Candidate candidate, EmbyTrackCredit track, RecordingLookupResult lookup)
        {
            var rec = lookup.Recording!;

            string tier = lookup.RungReached switch
            {
                RecordingLookupRung.TrackArtistAlbum => "CorroborationTier.Tier1",
                RecordingLookupRung.TrackAlbum => "CorroborationTier.Tier2",
                RecordingLookupRung.TrackArtist => "CorroborationTier.Tier2",
                _ => "CorroborationTier.Tier3", // TrackDuration/TrackOnly: frequency-inferred or unnarrowed, not a direct search-field confirmation
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
                Rung = lookup.RungReached.ToString(),
                Rationale = $"MusicBrainz {tierDescription} corroboration for \"{track.TrackName}\"{aliasNote}{confirmationNote} (rung={lookup.RungReached}, mbScore={rec.Score}).",
            };
        }
    }
}
