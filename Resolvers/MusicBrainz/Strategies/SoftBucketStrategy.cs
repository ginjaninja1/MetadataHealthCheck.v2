using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies
{
    /// <summary>
    /// Strategy B (§5.3): used when no own anchor (Strategy A) and, in later
    /// phases, no borrowed anchor (Strategy C) is available.
    ///
    /// Artist-search-first, per the settled architectural decision (supersedes the
    /// old recording-search-first approach, now in the spec's Appendix A):
    ///
    ///   1. C1: SearchArtist(source.DisplayName) -- returns candidates plus their
    ///      inline registered aliases in one call.
    ///   2. Stage 1 admission gate (§5.3): a result is only considered further if
    ///      (a) MB's own text-relevance Score clears
    ///      ScoringConfig.CandidateGeneration.ArtistCandidateMinScore, and (b) the
    ///      normalized source name is within
    ///      ScoringConfig.CandidateGeneration.ArtistCandidateMaxEditDistance
    ///      (raw Levenshtein, not a normalized ratio) of either the result's own
    ///      normalized Name or one of its normalized Aliases. Normalization is
    ///      ArtistNameNormalizer.cs, driven by
    ///      ScoringConfig.CandidateGeneration.NameNormalizationRules.
    ///   3. Stage 2 per-candidate confirmation (§5.3): RecordingLookup is reused
    ///      here, not to generate candidates, but to confirm that this admitted
    ///      candidate actually appears — trustworthily, per its own name-match
    ///      rejection logic (§5.4) — on at least one of this artist's real tracks.
    /// </summary>
    public class SoftBucketStrategy : ICandidateGenerationStrategy<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly RecordingLookup _recordingLookup;
        private readonly ScoringConfig _config;

        public SoftBucketStrategy(IMusicBrainzApiClient client, RecordingLookup recordingLookup, ScoringConfig config)
        {
            _client = client;
            _recordingLookup = recordingLookup;
            _config = config;
        }

        public string StrategyName => "B";
        public int Priority => 30; // tried after A (and, later, C) — §5.3

        public IEnumerable<Candidate> GenerateCandidates(EmbyArtist source, ResolutionContext context)
        {
            var artistResults = _client.SearchArtist(source.DisplayName);
            var seen = new HashSet<string>();
            var cgConfig = _config.CandidateGeneration;

            var normalizedSource = ArtistNameNormalizer.Normalize(source.DisplayName, cgConfig.NameNormalizationRules);

            foreach (var result in artistResults)
            {
                if (!seen.Add(result.Mbid))
                    continue;

                if (result.Score < cgConfig.ArtistCandidateMinScore)
                    continue; // MB's own text-relevance score too low to consider, §5.4

                if (!IsNameOrAliasWithinEditDistance(normalizedSource, result, cgConfig))
                    continue; // Stage 1 admission gate, §5.3

                if (!IsConfirmedByAnyTrack(result.Mbid, source))
                    continue; // Stage 2 per-candidate confirmation via RecordingLookup, §5.3

                yield return new Candidate
                {
                    SourceEntityId = source.SourceId,
                    TargetSystem = "MusicBrainz",
                    TargetEntityType = "Artist",
                    TargetId = result.Mbid,
                    GenerationStrategy = StrategyName,
                    GenerationQuery = $"artist:\"{source.DisplayName}\"",
                    CreatedAt = DateTime.UtcNow,
                };
            }
        }

        private bool IsConfirmedByAnyTrack(string candidateMbid, EmbyArtist source)
        {
            foreach (var track in source.Tracks)
            {
                // Composer-tier: the candidate is never the recording's artist-credit,
                // so the name-bearing ladder below can never confirm it (Outstanding
                // item A / §5.1's Composer-tier variant). Route through the
                // relationship-scan path instead.
                if (string.Equals(track.Role, "Composer", StringComparison.OrdinalIgnoreCase))
                {
                    var coCredits = track.AlbumArtists.Concat(track.Artists).Select(c => c.Name);
                    var composerLookup = _recordingLookup.LookupComposerTier(candidateMbid, track, coCredits);
                    if (composerLookup.Recording != null)
                        return true;
                    continue;
                }

                var lookup = _recordingLookup.Lookup(candidateMbid, track, source.DisplayName);
                if (lookup.Recording != null)
                    return true;
            }
            return false;
        }

        private static bool IsNameOrAliasWithinEditDistance(string normalizedSource, MbArtistResult result, CandidateGenerationConfig config)
        {
            var normalizedName = ArtistNameNormalizer.Normalize(result.Name, config.NameNormalizationRules);
            if (NameDistanceEvidenceCollector.Levenshtein(normalizedSource, normalizedName) <= config.ArtistCandidateMaxEditDistance)
                return true;

            foreach (var alias in result.Aliases)
            {
                var normalizedAlias = ArtistNameNormalizer.Normalize(alias, config.NameNormalizationRules);
                if (NameDistanceEvidenceCollector.Levenshtein(normalizedSource, normalizedAlias) <= config.ArtistCandidateMaxEditDistance)
                    return true;
            }

            return false;
        }
    }
}