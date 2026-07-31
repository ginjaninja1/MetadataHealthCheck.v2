using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    /// <summary>
    /// The sole candidate-generation strategy for the Artist/MusicBrainz
    /// resolver. Searches MusicBrainz for the source artist's name and aliases,
    /// admits results by MB relevance score OR an exact normalized name/alias
    /// match (an exact name/alias hit is never dropped for a middling score),
    /// fetches each admitted candidate's artist-relationships eagerly, then
    /// runs the identity-fold and relationship-mirror-prune passes before
    /// yielding candidates in tier-first, MB-score-descending order -- so the
    /// sequential sampler's early-stopping sees the most likely candidate
    /// first.
    /// </summary>
    public class ArtistCandidateStrategy : ICandidateGenerationStrategy<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly ArtistMusicBrainzConfig _config;
        private readonly StructuredLogger? _logger;
        private readonly ArtistRelationshipFetcher _relationshipFetcher;
        private readonly CandidateIdentityFoldPass _foldPass;
        private readonly CandidateRelationshipMirrorPrune _mirrorPrune;

        public ArtistCandidateStrategy(IMusicBrainzApiClient client, ArtistMusicBrainzConfig config, StructuredLogger? logger = null)
        {
            _client = client;
            _config = config;
            _logger = logger;
            _relationshipFetcher = new ArtistRelationshipFetcher(client, logger);
            _foldPass = new CandidateIdentityFoldPass(logger);
            _mirrorPrune = new CandidateRelationshipMirrorPrune(logger);
        }

        public string StrategyName => "ArtistCandidateStrategy";
        public int Priority => 30;

        public IEnumerable<Candidate> GenerateCandidates(EmbyArtist source, ResolutionContext context)
        {
            var cgConfig = _config.CandidateGeneration;
            var admitted = AdmitSearchResults(source, cgConfig);

            var finalCandidates = new List<Candidate>();
            var relationshipsByCandidate = new List<IReadOnlyList<AdmittedArtistRelationship>>();
            var identityRelationshipMbidsByCandidate = new List<IReadOnlyList<string>>();
            var tiers = new List<ArtistMatchTier>();

            foreach (var (result, tier) in admitted)
            {
                var relationships = _relationshipFetcher.FetchValid(result.Mbid, result.Name, cgConfig);
                relationshipsByCandidate.Add(relationships);
                identityRelationshipMbidsByCandidate.Add(
                    relationships.Where(r => r.Classification == ArtistRelationshipClassification.Identity)
                                 .Select(r => r.Mbid)
                                 .ToList());
                tiers.Add(tier);

                finalCandidates.Add(new Candidate
                {
                    SourceEntityId = source.SourceId,
                    TargetSystem = "MusicBrainz",
                    TargetEntityType = "Artist",
                    TargetId = result.Mbid,
                    Name = result.Name,
                    Type = result.Type,
                    GenerationStrategy = StrategyName,
                    GenerationQuery = _client.LastSearchArtistQueryUsed ?? $"(artist:\"{source.DisplayName}\" OR alias:\"{source.DisplayName}\")",
                    CreatedAt = DateTime.UtcNow,
                    RelationshipMbids = relationships.Select(r => r.Mbid).ToList(),
                    GroupMembershipMbids = relationships
                        .Where(r => r.Classification == ArtistRelationshipClassification.GroupMembership)
                        .Select(r => r.Mbid)
                        .ToList(),
                });
            }

            var folded = _foldPass.Apply(finalCandidates, tiers, identityRelationshipMbidsByCandidate, context);
            var prunedNamesByCandidate = _mirrorPrune.Apply(finalCandidates, folded, relationshipsByCandidate);

            LogSummary(finalCandidates, admitted, relationshipsByCandidate, folded, prunedNamesByCandidate);

            for (int i = 0; i < finalCandidates.Count; i++)
            {
                if (!folded[i])
                    yield return finalCandidates[i];
            }
        }

        private List<(MbArtistResult Result, ArtistMatchTier Tier)> AdmitSearchResults(EmbyArtist source, CandidateGenerationConfig cgConfig)
        {
            var artistResults = _client.SearchArtist(source.DisplayName);
            var normalizedSource = ArtistNameNormalizer.Normalize(source.DisplayName, cgConfig.NameNormalizationRules);

            _logger?.Info("ArtistCandidateGen", "[{0}] Filtering {1} artist search result(s) by MB score (>= {2}) OR exact normalized name/alias match...", source.DisplayName, artistResults.Count, cgConfig.ArtistCandidateMinScore);

            var admitted = new List<(MbArtistResult Result, ArtistMatchTier Tier)>();
            var seen = new HashSet<string>();
            foreach (var result in artistResults)
            {
                if (!seen.Add(result.Mbid))
                {
                    _logger?.Debug("ArtistCandidateGen", "  [{0}] \"{1}\" -- duplicate of an already-seen result, skipped.", result.Mbid, result.Name);
                    continue;
                }

                var tier = ArtistMatchTierClassifier.Classify(normalizedSource, result, cgConfig);

                // An exact normalized name or alias match is admitted regardless of
                // MB score -- a genuine exact match must never be dropped for a
                // middling relevance score.
                if (result.Score < cgConfig.ArtistCandidateMinScore && tier != ArtistMatchTier.Name && tier != ArtistMatchTier.Alias)
                {
                    _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} tier={3} -- dropped: below ArtistCandidateMinScore ({4}) and not an exact name/alias match.", result.Mbid, result.Name, result.Score, tier, cgConfig.ArtistCandidateMinScore);
                    continue;
                }

                _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} tier={3} -- admitted as a candidate.", result.Mbid, result.Name, result.Score, tier);
                admitted.Add((result, tier));
            }

            // Tier-first, MB score descending within a tier -- so the sampler's
            // early-stopping sees the most likely candidate first.
            var ordered = admitted.OrderBy(a => a.Tier).ThenByDescending(a => a.Result.Score).ToList();
            _logger?.Info("ArtistCandidateGen", "[{0}] Complete: {1} of {2} search result(s) admitted as candidates.", source.DisplayName, ordered.Count, artistResults.Count);
            return ordered;
        }

        private void LogSummary(
            List<Candidate> finalCandidates,
            List<(MbArtistResult Result, ArtistMatchTier Tier)> admitted,
            List<IReadOnlyList<AdmittedArtistRelationship>> relationshipsByCandidate,
            bool[] folded,
            List<string>[] prunedNamesByCandidate)
        {
            _logger?.Info("ArtistCandidateGen", "================================================================");
            _logger?.Info("ArtistCandidateGen", "Artist Candidate Summary");
            _logger?.Info("ArtistCandidateGen", "================================================================");
            for (int i = 0; i < finalCandidates.Count; i++)
            {
                var (result, _) = admitted[i];
                var aliasText = result.Aliases.Count == 0 ? "(none)" : string.Join(", ", result.Aliases);
                var relText = relationshipsByCandidate[i].Count == 0
                    ? "(none)"
                    : string.Join(", ", relationshipsByCandidate[i].Select(r => r.Name));
                var foldedSuffix = folded[i] ? "  [FOLDED -- excluded from sampling, see fold pass log above]" : "";
                var prunedSuffix = prunedNamesByCandidate[i].Count == 0 ? "" : $"  [MIRROR-PRUNED from scoring: {string.Join(", ", prunedNamesByCandidate[i])}]";
                _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} aliases=[{3}] relationships=[{4}]{5}{6}",
                    result.Mbid, result.Name, result.Score, aliasText, relText, foldedSuffix, prunedSuffix);
            }
            _logger?.Info("ArtistCandidateGen", "================================================================");
        }
    }
}
