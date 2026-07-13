using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies
{
    /// <summary>
    /// Normalizes an artist display name before Stage 1's edit-distance admission
    /// gate compares it against a candidate's name/aliases (§5.3). Two kinds of step,
    /// deliberately kept separate:
    ///
    ///   1. Diacritics folding (fixed, not user-editable) — Unicode NFD decomposition,
    ///      strip combining marks, recompose. Not expressed as a regex table entry
    ///      since "remove accents" isn't a small edited replacement rule the way
    ///      "strip leading The" is.
    ///   2. The regex replacement/allowance table itself
    ///      (ScoringConfig.CandidateGeneration.NameNormalizationRules) — strip leading
    ///      "The", fold &/and/+, strip apostrophes, strip feat/vs/with credit
    ///      suffixes, strip remaining punctuation (§5.3's own enumeration).
    ///
    /// Case-folding and whitespace collapse always run last, unconditionally.
    /// </summary>
    internal static class ArtistNameNormalizer
    {
        public static string Normalize(string input, IReadOnlyList<NameNormalizationRule> rules)
        {
            string s = FoldDiacritics(input.Trim());

            foreach (var rule in rules)
                s = Regex.Replace(s, rule.Pattern, rule.Replacement, RegexOptions.IgnoreCase);

            s = s.Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"\s+", " ");
            return s;
        }

        private static string FoldDiacritics(string s)
        {
            var decomposed = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}