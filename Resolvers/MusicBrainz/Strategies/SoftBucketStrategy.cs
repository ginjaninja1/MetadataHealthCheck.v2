using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies
{
    /// <summary>
    /// Strategy B (§5.3): used when no own anchor (Strategy A) and, in later
    /// phases, no borrowed anchor (Strategy C) is available. Queries
    /// recording:"{track}" AND release:"{album}", falling back to dropping
    /// the release clause if empty (§7.2, call C4).
    /// </summary>
    public class SoftBucketStrategy : ICandidateGenerationStrategy<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;

        public SoftBucketStrategy(IMusicBrainzApiClient client) => _client = client;

        public string StrategyName => "B";
        public int Priority => 30; // tried after A (and, later, C) — §5.3

        public IEnumerable<Candidate> GenerateCandidates(EmbyArtist source, ResolutionContext context)
        {
            var seen = new HashSet<string>();
            foreach (var track in source.Tracks)
            {
                var recordings = _client.SearchRecording(track.TrackName, track.AlbumName);
                if (recordings.Count == 0)
                    recordings = _client.SearchRecording(track.TrackName, null); // fallback: drop release: clause

                foreach (var rec in recordings)
                {
                    if (!seen.Add(rec.ArtistMbid)) continue;
                    yield return new Candidate
                    {
                        SourceEntityId = source.SourceId,
                        TargetSystem = "MusicBrainz",
                        TargetEntityType = "Artist",
                        TargetId = rec.ArtistMbid,
                        GenerationStrategy = StrategyName,
                        GenerationQuery = $"recording:\"{track.TrackName}\" AND release:\"{track.AlbumName}\"",
                        CreatedAt = DateTime.UtcNow,
                    };
                }
            }
        }
    }
}
