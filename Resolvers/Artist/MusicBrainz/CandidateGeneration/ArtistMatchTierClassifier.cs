using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    /// <summary>
    /// Classifies a MusicBrainz artist search hit's sort tier relative to the
    /// (already-normalized) source name -- used to order admitted candidates so
    /// the sequential sampler's early-stopping sees the most likely candidate
    /// first, and to decide which side of an identity-fold pair survives.
    /// </summary>
    internal static class ArtistMatchTierClassifier
    {
        public static ArtistMatchTier Classify(string normalizedSource, MbArtistResult result, CandidateGenerationConfig config)
        {
            var normalizedName = ArtistNameNormalizer.Normalize(result.Name, config.NameNormalizationRules);
            if (NameSimilarity.Levenshtein(normalizedSource, normalizedName) == 0)
                return ArtistMatchTier.Name;

            foreach (var alias in result.Aliases)
            {
                var normalizedAlias = ArtistNameNormalizer.Normalize(alias, config.NameNormalizationRules);
                if (NameSimilarity.Levenshtein(normalizedSource, normalizedAlias) == 0)
                    return ArtistMatchTier.Alias;
            }

            return ArtistMatchTier.Neither;
        }

        // Not currently called: admission is score-only (or exact name/alias
        // match), not edit-distance-based. Preserved as a reusable building
        // block in case closeness-based admission or tiering is wanted again --
        // flag with Nick before deleting.
        public static bool IsNameOrAliasWithinEditDistance(string normalizedSource, MbArtistResult result, CandidateGenerationConfig config)
        {
            var normalizedName = ArtistNameNormalizer.Normalize(result.Name, config.NameNormalizationRules);
            if (NameSimilarity.Levenshtein(normalizedSource, normalizedName) <= config.ArtistCandidateMaxEditDistance)
                return true;

            foreach (var alias in result.Aliases)
            {
                var normalizedAlias = ArtistNameNormalizer.Normalize(alias, config.NameNormalizationRules);
                if (NameSimilarity.Levenshtein(normalizedSource, normalizedAlias) <= config.ArtistCandidateMaxEditDistance)
                    return true;
            }

            return false;
        }
    }
}
