using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Work-level relationship evidence (writer/composer/lyricist), sourced from
    /// work-rels+work-level-rels (§5.4/§7.2 C5). Decomposed by actual relationship
    /// type, not treated as one undifferentiated "Composer matched" fact.
    /// Recording-level relations (producer/arranger) are a separate collector,
    /// deferred to Phase 2's full evidence set per §21.
    ///
    /// Converted from IEvidenceCollector to IObservationEvidenceCollector alongside
    /// the Sequential Sampler (§5.5, §21 phase 2): this used to loop over every one
    /// of source.Tracks itself and stop at the first hit, which is exactly the
    /// "collect everything, no early stop" behavior the sampler replaces. Now it
    /// checks the single track handed to it by SequentialSampler's own draw order
    /// and lets the sampler decide when enough observations have been taken.
    /// </summary>
    public class WorkRelationshipEvidenceCollector : IObservationEvidenceCollector<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly RecordingLookup _recordingLookup;
        private static readonly HashSet<string> WorkLevelTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "writer", "composer", "lyricist", "librettist"
        };

        public WorkRelationshipEvidenceCollector(IMusicBrainzApiClient client, RecordingLookup recordingLookup)
        {
            _client = client;
            _recordingLookup = recordingLookup;
        }

        public string EvidenceType => "WorkRelationship";

        public EvidenceRecord? Collect(EmbyArtist source, Candidate candidate, IObservationUnit unit, ResolutionContext context)
        {
            // This collector needs a recording id, which the AnchoredRecordingStrategy/
            // SoftBucketStrategy don't currently thread through onto Candidate (the
            // spec's Candidate model, §4, doesn't carry one either). Re-derive the
            // recording via the shared RecordingLookup (2026-07-12), matched to this
            // candidate's artist mbid, instead of an inline SearchRecording call — this
            // also gives the lookup a chance to be memoized once other collectors
            // (Corroboration/AlbumMatch/RecordingRelationship) share the same instance.
            if (unit is not EmbyTrackObservationUnit trackUnit) return null;
            var track = trackUnit.Track;

            var lookup = _recordingLookup.Lookup(candidate.TargetId, track, artistName: null);
            var rec = lookup.Recording;
            if (rec == null) return null;

            var rels = _client.GetWorkRelationships(rec.RecordingId)
                .Where(r => WorkLevelTypes.Contains(r.RelationshipType) && r.ArtistMbid == candidate.TargetId)
                .ToList();

            if (rels.Count == 0) return null;

            var rel = rels[0];
            string evidenceType = rel.RelationshipType.ToLowerInvariant() switch
            {
                "writer" => "WorkRelationship.Writer",
                "composer" => "WorkRelationship.Composer",
                "lyricist" => "WorkRelationship.Lyricist",
                _ => "WorkRelationship.Other",
            };

            return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = evidenceType,
                RawValue = rel.RelationshipType,
                Role = track.Role,
                SourceTrackId = track.TrackId,
                AlbumId = track.AlbumId,
                RelationshipType = rel.RelationshipType,
                Rationale = $"MusicBrainz credits this artist as {rel.RelationshipType} on \"{track.TrackName}\".",
            };
        }
    }
}