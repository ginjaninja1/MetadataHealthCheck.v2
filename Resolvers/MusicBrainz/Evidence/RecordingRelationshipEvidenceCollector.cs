using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Recording-level relationship evidence (producer/arranger), sourced from the
    /// same GetRelationships call as WorkRelationshipEvidenceCollector but filtered to
    /// RelationshipLevel.Recording instead of .Work (§5.4/§7.2 C5). This is the
    /// collector originally motivated by the Queen/Bohemian Rhapsody real-world case:
    /// a performer-only (Artist-tier) band credit has no writer/composer relation to
    /// find, but a real recording-level producer credit exists and is externally
    /// corroborated.
    ///
    /// Only producer (+0.8) and arranger (+0.5) have LLR entries in §6.1's evidence
    /// catalog — "engineer" is a real MB relationship type this call can return, but
    /// has no catalog weight, so it's treated the same as any other unrecognized
    /// evidence type (contributes 0 via SimpleWeightedSumScorer's existing fallback,
    /// not an error).
    /// </summary>
    public class RecordingRelationshipEvidenceCollector : IObservationEvidenceCollector<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly RecordingLookup _recordingLookup;

        public RecordingRelationshipEvidenceCollector(IMusicBrainzApiClient client, RecordingLookup recordingLookup)
        {
            _client = client;
            _recordingLookup = recordingLookup;
        }

        public string EvidenceType => "RecordingRelationship";

        // DORMANT (2026-07-17): unwired from MusicBrainzArtistResolverPlugin --
        // superseded by RecordingCorroborationEvidenceCollector. Signature updated to
        // match the widened IObservationEvidenceCollector interface purely so this
        // file still compiles while sitting unused; logic otherwise untouched.
        public IEnumerable<EvidenceRecord> Collect(EmbyArtist source, Candidate candidate, IObservationUnit unit, ResolutionContext context)
        {
            if (unit is not EmbyTrackObservationUnit trackUnit) yield break;
            var track = trackUnit.Track;

            // Composer-tier: relationship-scan path (§5.1's Composer-tier variant) --
            // see WorkRelationshipEvidenceCollector for the same routing and rationale.
            // MatchedViaAlias has no meaning on this path (no name-match evaluation
            // runs), so it stays false for composer-tier hits.
            RecordingLookupResult lookup;
            if (string.Equals(track.Role, "Composer", StringComparison.OrdinalIgnoreCase))
            {
                var coCredits = track.AlbumArtists.Concat(track.Artists).Select(c => c.Name);
                lookup = _recordingLookup.LookupComposerTier(candidate.TargetId, track, coCredits);
            }
            else
            {
                lookup = _recordingLookup.Lookup(candidate.TargetId, track, artistName: source.DisplayName);
            }
            var rec = lookup.Recording;
            if (rec == null) yield break;

            var rels = _client.GetRelationships(rec.RecordingId)
                .Where(r => r.Level == RelationshipLevel.Recording && r.ArtistMbid == candidate.TargetId)
                .ToList();

            if (rels.Count == 0) yield break;

            var rel = rels[0];
            string evidenceType = rel.RelationshipType.ToLowerInvariant() switch
            {
                "producer" => "RecordingRelationship.Producer",
                "arranger" => "RecordingRelationship.Arranger",
                _ => "RecordingRelationship.Other",
            };

            yield return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = evidenceType,
                RawValue = rel.RelationshipType,
                Role = track.Role,
                SourceTrackId = track.TrackId,
                AlbumId = track.AlbumId,
                RelationshipType = rel.RelationshipType,
                MatchedViaAlias = lookup.MatchedViaAlias,
                Rationale = $"MusicBrainz credits this artist as {rel.RelationshipType} on \"{track.TrackName}\".",
            };
        }
    }
}