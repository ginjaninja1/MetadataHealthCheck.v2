using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies
{
    /// <summary>
    /// Strategy A (§5.3): if the source artist already has a confirmed MBID
    /// anchor (from the Identity Cache), query recording:"{track}" AND
    /// arid:{anchor_mbid} (§7.2, C3). Near-certain single hit, still passed
    /// through full evidence scoring — an anchor does not bypass verification.
    /// </summary>
    public class AnchoredRecordingStrategy : ICandidateGenerationStrategy<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly IIdentityCache _identityCache;

        public AnchoredRecordingStrategy(IMusicBrainzApiClient client, IIdentityCache identityCache)
        {
            _client = client;
            _identityCache = identityCache;
        }

        public string StrategyName => "A";
        public int Priority => 10; // tried first — §5.3

        public IEnumerable<Candidate> GenerateCandidates(EmbyArtist source, ResolutionContext context)
        {
            var existing = _identityCache.Get(source.SourceSystem, source.SourceId, "MusicBrainz");
            if (existing == null)
                yield break; // no own anchor — fall through to Strategy B (or C in Phase 3)

            foreach (var track in source.Tracks)
            {
                var recordings = _client.SearchRecording(track.TrackName, track.AlbumName);
                foreach (var rec in recordings.Where(r => r.ArtistMbid == existing.TargetId))
                {
                    yield return new Candidate
                    {
                        SourceEntityId = source.SourceId,
                        TargetSystem = "MusicBrainz",
                        TargetEntityType = "Artist",
                        TargetId = rec.ArtistMbid,
                        GenerationStrategy = StrategyName,
                        GenerationQuery = $"recording:\"{track.TrackName}\" AND arid:{existing.TargetId}",
                        CreatedAt = DateTime.UtcNow,
                    };
                }
            }
        }
    }
}
