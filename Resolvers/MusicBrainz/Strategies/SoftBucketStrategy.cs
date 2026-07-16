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
    ///   2. Name/alias filtering (§5.3): a result becomes a real Candidate if (a)
    ///      MB's own text-relevance Score clears
    ///      ScoringConfig.CandidateGeneration.ArtistCandidateMinScore, and (b) the
    ///      normalized source name is within
    ///      ScoringConfig.CandidateGeneration.ArtistCandidateMaxEditDistance
    ///      (raw Levenshtein, not a normalized ratio) of either the result's own
    ///      normalized Name or one of its normalized Aliases. Normalization is
    ///      ArtistNameNormalizer.cs, driven by
    ///      ScoringConfig.CandidateGeneration.NameNormalizationRules. Pure string
    ///      comparison, no API calls, no track/observation data of any kind.
    ///
    /// REMOVED 2026-07-16 (Nick's explicit direction, confirmed correct against
    /// spec): this class used to ALSO do a "Phase 2" recording-lookup confirmation
    /// pass here, before returning a candidate at all -- looping raw over
    /// source.Tracks with no ordering discipline, calling RecordingLookup directly.
    /// That was flat wrong: recording lookups must never happen before the Track
    /// Observation Feeder (EmbyArtistObservationUnitProvider, §5.3.1) has selected
    /// and ordered which track to observe -- that's the entire point of the
    /// Feeder/Engine split settled in Decisions.md, 2026-07-12. Phase 2 bypassed
    /// the Feeder completely and duplicated, badly, a job the Sequential
    /// Resolution Engine's own per-observation loop already does correctly:
    /// CorroborationTierEvidenceCollector (Resolvers/MusicBrainz/Evidence/) calls
    /// RecordingLookup once per Feeder-ordered observation and produces graded
    /// (Tier 1/2/3) evidence -- consistent with §5.4's "never pre-baked LLR,
    /// accumulate and decide via threshold" philosophy, which Phase 2's binary
    /// admit/reject gate actually violated outright.
    ///
    /// Net effect: a name/alias match is now a real Candidate immediately. If
    /// MusicBrainz happens to have two real, distinct entities with near-identical
    /// names, both become candidates here -- the one with no genuine matching
    /// recordings simply accumulates no CorroborationTier evidence once the Engine
    /// runs and is rejected by the normal decision gate, the same way any other
    /// unconfirmed candidate is. No separate upfront recording check is needed or
    /// wanted; this is the evidence-accumulation system working as designed, not a
    /// gap left by removing Phase 2.
    /// </summary>
    public class SoftBucketStrategy : ICandidateGenerationStrategy<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly ScoringConfig _config;
        private readonly MetadataHealthCheck.v2.Diagnostics.StructuredLogger? _logger;

        // logger is optional (nullable), 2026-07-16 -- added after this class was
        // first built; existing/future callers that don't have a logger handy
        // shouldn't be forced to supply one just to keep compiling.
        public SoftBucketStrategy(IMusicBrainzApiClient client, ScoringConfig config, MetadataHealthCheck.v2.Diagnostics.StructuredLogger? logger = null)
        {
            _client = client;
            _config = config;
            _logger = logger;
        }

        public string StrategyName => "B";
        public int Priority => 30; // tried after A (and, later, C) — §5.3

        public IEnumerable<Candidate> GenerateCandidates(EmbyArtist source, ResolutionContext context)
        {
            var artistResults = _client.SearchArtist(source.DisplayName);
            var cgConfig = _config.CandidateGeneration;
            var normalizedSource = ArtistNameNormalizer.Normalize(source.DisplayName, cgConfig.NameNormalizationRules);

            // Name/alias filtering ONLY. No API calls beyond the SearchArtist call
            // already made above, no recording lookups, no track/observation data
            // touched at all -- this is the ENTIRETY of candidate generation now.
            // Anything recording-shaped happens later, inside the Engine, once the
            // Feeder has selected and ordered an observation.
            _logger?.Info("ArtistCandidateGen", "[{0}] Filtering {1} artist search result(s) by name/alias closeness...", source.DisplayName, artistResults.Count);

            var seen = new HashSet<string>();
            int admitted = 0;
            foreach (var result in artistResults)
            {
                if (!seen.Add(result.Mbid))
                {
                    _logger?.Debug("ArtistCandidateGen", "  [{0}] \"{1}\" -- DUPLICATE of an already-seen result, skipped.", result.Mbid, result.Name);
                    continue;
                }

                if (result.Score < cgConfig.ArtistCandidateMinScore)
                {
                    _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} -- DROPPED: below ArtistCandidateMinScore ({3}).", result.Mbid, result.Name, result.Score, cgConfig.ArtistCandidateMinScore);
                    continue; // MB's own text-relevance score too low to consider, §5.4
                }

                if (!IsNameOrAliasWithinEditDistance(normalizedSource, result, cgConfig))
                {
                    _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} -- DROPPED: name/alias not within ArtistCandidateMaxEditDistance ({3}) of \"{4}\".", result.Mbid, result.Name, result.Score, cgConfig.ArtistCandidateMaxEditDistance, source.DisplayName);
                    continue; // admission gate, §5.3
                }

                _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} -- ADMITTED as a candidate.", result.Mbid, result.Name, result.Score);
                admitted++;

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

            _logger?.Info("ArtistCandidateGen", "[{0}] Complete: {1} of {2} search result(s) admitted as candidates.", source.DisplayName, admitted, artistResults.Count);
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