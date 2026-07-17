using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// SUPERSEDES CorroborationTierEvidenceCollector, WorkRelationshipEvidenceCollector,
    /// and RecordingRelationshipEvidenceCollector (built 2026-07-17, per direct
    /// instruction after a design conversation). Those three files are left in the
    /// repo unmodified but are no longer wired into MusicBrainzArtistResolverPlugin —
    /// not deleted, in case anchoring/composer-tier work later needs to reference
    /// their logic, but they should be treated as dead code until a decision is made
    /// to revive one of them.
    ///
    /// WHY THIS COLLAPSE HAPPENED: the three collectors above each independently
    /// decided how to call the shared RecordingLookup for the same (candidate, track)
    /// pair -- two passed artistName: source.DisplayName, one passed artistName: null,
    /// and all three branched into a separate LookupComposerTier() ladder for
    /// Composer-role tracks. Because RecordingLookup's cache was keyed only on
    /// (candidateMbid, TrackId) -- with no distinction for which method or which
    /// arguments produced the cached answer -- whichever collector ran first for a
    /// given (candidate, track) silently decided the answer for the other two, and
    /// Lookup() / LookupComposerTier() could stomp on each other's cache entries for
    /// the very same key. This was a real correctness bug, not just a style
    /// inconsistency (see "AlbumArtist observation starts without artist" in the
    /// Project Log).
    ///
    /// SETTLED DIRECTIVE (2026-07-17): "is this recording confirmed for this
    /// candidate" is ONE factual question per (candidate, observation), not one per
    /// collector. So there is now exactly one RecordingLookup.Lookup() call per
    /// Collect() invocation, always with artistName: source.DisplayName, for EVERY
    /// observation Role (AlbumArtist/Artist/Composer alike) -- no branching into
    /// LookupComposerTier(). If Composer-role observations come back NotFound because
    /// the recording's artist-credit is the performer and not the composer, that's
    /// expected and informative: it's the evidence we need before deciding whether
    /// Composer needs different lookup logic (LookupComposerTier stays dormant in
    /// RecordingLookup.cs, unused, until that decision is made). Do not silently wire
    /// it back in.
    ///
    /// Once one recording is confirmed, this collector pulls out every evidence type
    /// it can find on THAT SAME recording in one pass, rather than three separate
    /// lookups: Corroboration Tier (§6.1/§6.3), Work-level relationships
    /// (writer/composer/lyricist/librettist, §5.4/§7.2 C5), and Recording-level
    /// relationships (producer/arranger, §5.4/§7.2 C5). This is possible because
    /// IObservationEvidenceCollector.Collect was widened 2026-07-17 to return
    /// IEnumerable&lt;EvidenceRecord&gt; instead of at most one record.
    /// </summary>
    public class RecordingCorroborationEvidenceCollector : IObservationEvidenceCollector<EmbyArtist>
    {
        private static readonly HashSet<string> WorkLevelTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "writer", "composer", "lyricist", "librettist"
        };

        private readonly IMusicBrainzApiClient _client;
        private readonly RecordingLookup _recordingLookup;

        public RecordingCorroborationEvidenceCollector(IMusicBrainzApiClient client, RecordingLookup recordingLookup)
        {
            _client = client;
            _recordingLookup = recordingLookup;
        }

        // Reports several EvidenceType values (CorroborationTier.*, WorkRelationship.*,
        // RecordingRelationship.*); this property is descriptive only, matching the
        // pattern already used elsewhere in this codebase for multi-outcome collectors.
        public string EvidenceType => "RecordingCorroboration";

        public IEnumerable<EvidenceRecord> Collect(EmbyArtist source, Candidate candidate, IObservationUnit unit, ResolutionContext context)
        {
            if (unit is not EmbyTrackObservationUnit trackUnit) yield break;
            var track = trackUnit.Track;

            // ONE lookup, same call, every Role (AlbumArtist/Artist/Composer alike).
            // See class doc comment: no LookupComposerTier branching here by settled
            // directive -- a Composer-role NotFound is real, useful evidence, not a
            // bug to route around.
            var lookup = _recordingLookup.Lookup(candidate.TargetId, track, artistName: source.DisplayName);
            var rec = lookup.Recording;
            if (rec == null) yield break;

            // --- Corroboration Tier (§6.1/§6.3) ---
            string tier = (rec.TrackTitleMatches, rec.ReleaseTitleMatches) switch
            {
                (true, true) => "CorroborationTier.Tier1",
                (true, false) => "CorroborationTier.Tier2",
                _ => "CorroborationTier.Tier3",
            };
            string tierDescription = tier switch
            {
                "CorroborationTier.Tier1" => "full-triple (artist+track+album)",
                "CorroborationTier.Tier2" => "artist+track, no album",
                _ => "single-field (artist only)",
            };
            string aliasNote = lookup.MatchedViaAlias ? " (matched via a registered alias)" : "";

            yield return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = tier,
                RawValue = $"track={rec.TrackTitleMatches} album={rec.ReleaseTitleMatches} rung={lookup.RungReached}",
                Role = track.Role,
                SourceTrackId = track.TrackId,
                AlbumId = track.AlbumId,
                MatchedViaAlias = lookup.MatchedViaAlias,
                Rationale = $"MusicBrainz {tierDescription} corroboration for \"{track.TrackName}\"{aliasNote} (rung={lookup.RungReached}).",
            };

            // --- Relationship evidence (§5.4/§7.2 C5), same recording, one call ---
            var rels = _client.GetRelationships(rec.RecordingId)
                .Where(r => r.ArtistMbid == candidate.TargetId)
                .ToList();

            var workRel = rels.FirstOrDefault(r => r.Level == RelationshipLevel.Work && WorkLevelTypes.Contains(r.RelationshipType));
            if (workRel != null)
            {
                string workEvidenceType = workRel.RelationshipType.ToLowerInvariant() switch
                {
                    "writer" => "WorkRelationship.Writer",
                    "composer" => "WorkRelationship.Composer",
                    "lyricist" => "WorkRelationship.Lyricist",
                    _ => "WorkRelationship.Other",
                };

                yield return new EvidenceRecord
                {
                    CandidateId = candidate.Id,
                    EvidenceType = workEvidenceType,
                    RawValue = workRel.RelationshipType,
                    Role = track.Role,
                    SourceTrackId = track.TrackId,
                    AlbumId = track.AlbumId,
                    RelationshipType = workRel.RelationshipType,
                    Rationale = $"MusicBrainz credits this artist as {workRel.RelationshipType} on \"{track.TrackName}\".",
                };
            }

            var recordingRel = rels.FirstOrDefault(r => r.Level == RelationshipLevel.Recording);
            if (recordingRel != null)
            {
                string recordingEvidenceType = recordingRel.RelationshipType.ToLowerInvariant() switch
                {
                    "producer" => "RecordingRelationship.Producer",
                    "arranger" => "RecordingRelationship.Arranger",
                    _ => "RecordingRelationship.Other",
                };

                yield return new EvidenceRecord
                {
                    CandidateId = candidate.Id,
                    EvidenceType = recordingEvidenceType,
                    RawValue = recordingRel.RelationshipType,
                    Role = track.Role,
                    SourceTrackId = track.TrackId,
                    AlbumId = track.AlbumId,
                    RelationshipType = recordingRel.RelationshipType,
                    MatchedViaAlias = lookup.MatchedViaAlias,
                    Rationale = $"MusicBrainz credits this artist as {recordingRel.RelationshipType} on \"{track.TrackName}\".",
                };
            }
        }
    }
}