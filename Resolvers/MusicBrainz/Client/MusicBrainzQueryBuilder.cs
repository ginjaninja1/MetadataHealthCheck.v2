using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client
{
    /// <summary>
    /// Pure Lucene query-string construction for the MusicBrainz search API -- no
    /// HTTP, no parsing, no response caching. Extracted 2026-07-29 from
    /// HttpMusicBrainzApiClient per one-file-one-purpose: that class's job is
    /// transport (HTTP calls, DTOs, response caching, call-count instrumentation);
    /// everything here is "what MB search syntax to send", which is search-strategy/
    /// domain logic, not transport, and deserves to be independently testable
    /// without a live HttpClient in the loop.
    ///
    /// AssumedMbQdurBucketSeconds and EscapeLucene moved here too, for the same
    /// reason -- both are properties of query construction, not of the HTTP/
    /// serialization layer.
    /// </summary>
    public static class MusicBrainzQueryBuilder
    {
        // MusicBrainz's own duration-bucketing width for the "qdur" search field --
        // INFERRED, not confirmed against MB documentation: reverse-engineered from
        // one working query (351000ms observed -> qdur:[173 TO 177] worked; 351/2 =
        // 175.5, which centers in that window). Not a config value, deliberately --
        // this isn't a lever we control, it's a guess at a fixed property of MB's
        // own index. If a future duration range shows this assumption breaking,
        // fix it here, in one place, with a name that says exactly what's being
        // assumed (2026-07-19, per direct correction from Nick on the earlier draft
        // that wrongly treated this as configurable).
        public const int AssumedMbQdurBucketSeconds = 2;

        // Regex added 2026-07-29: matches a quoted nickname/alias segment inside an
        // artist name using any common quote character (straight single/double or
        // curly), capturing only the inner text. Deliberately quote-symmetric-agnostic
        // (open and close don't have to match character-for-character) since sources
        // are inconsistent about which quote glyph they use.
        private static readonly Regex QuotedTermRegex =
            new Regex(
                "[\"'\u2018\u2019\u201C\u201D]([^\"'\u2018\u2019\u201C\u201D]+)[\"'\u2018\u2019\u201C\u201D]",
                RegexOptions.Compiled);

        public static string EscapeLucene(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // ---- C1: artist search ------------------------------------------------

        public static string BuildArtistPrimaryQuery(string name)
        {
            var escaped = EscapeLucene(name);
            return $"(artist:\"{escaped}\" OR alias:\"{escaped}\")";
        }

        // Fallback rung, added 2026-07-29: the primary query above quotes both
        // fields, so MB's Lucene parser treats each as an exact phrase and
        // effectively requires every word to match in order -- a source name that
        // differs from MB's stored form only in word order, a missing/extra word,
        // etc. gets zero results even though a matching artist exists. This
        // fallback drops the quotes (unquoted terms are OR'd/ranked by relevance
        // instead of exact-phrase matched) and drops the alias clause (unquoted
        // alias search alone was too noisy/broad to be worth the extra call).
        // Only tried when the primary query above found nothing, so it costs no
        // extra API call on the common path.
        //
        // Extended 2026-07-29 (Nick's direction): if the source name contains a
        // quoted nickname/alias segment (e.g. Jamie 'Cookie' Cook) AND has 3+
        // words, the quoted segment is kept quoted but down-weighted to ^0.5 -- so
        // the more distinctive surrounding words carry full relevance weight in
        // MB's Lucene ranking instead of the nickname term dominating/skewing the
        // match. Only triggers when BOTH conditions hold (quoted term present AND
        // word count >= 3); a 1-2 word name or a name with no quoted segment falls
        // through to the plain unweighted fallback, unchanged from before.
        public static string BuildArtistFallbackQuery(string name)
        {
            var wordCount = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            var matches = QuotedTermRegex.Matches(name);

            if (wordCount < 3 || matches.Count == 0)
                return $"artist:{EscapeLucene(name)}";

            var sb = new StringBuilder();
            int pos = 0;
            foreach (Match m in matches)
            {
                if (m.Index > pos)
                    sb.Append(EscapeLucene(name.Substring(pos, m.Index - pos)));
                sb.Append('\'').Append(EscapeLucene(m.Groups[1].Value)).Append("'^0.5");
                pos = m.Index + m.Length;
            }
            if (pos < name.Length)
                sb.Append(EscapeLucene(name.Substring(pos)));

            return $"artist:{sb}";
        }

        // ---- C3/C4: recording search ------------------------------------------

        public static string BuildRecordingSearchQuery(string trackTitle, string? albumTitle, IEnumerable<string>? artistNames)
        {
            var parts = new List<string> { $"recording:\"{EscapeLucene(trackTitle)}\"" };
            if (!string.IsNullOrWhiteSpace(albumTitle))
                parts.Add($"release:\"{EscapeLucene(albumTitle)}\"");
            var artistNameList = (artistNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();
            if (artistNameList.Count > 0)
                parts.Add($"artist:({string.Join(" OR ", artistNameList.Select(n => $"\"{EscapeLucene(n)}\""))})");
            return string.Join(" AND ", parts);
        }

        public static (string Query, int Low, int High) BuildRecordingByTitleAndDurationQuery(string trackTitle, int observedDurationMs, int qdurToleranceBuckets)
        {
            int centerBucket = (int)Math.Round(observedDurationMs / 1000.0 / AssumedMbQdurBucketSeconds);
            int low = Math.Max(0, centerBucket - qdurToleranceBuckets);
            int high = centerBucket + qdurToleranceBuckets;

            var query = $"recording:\"{EscapeLucene(trackTitle)}\" AND qdur:[{low} TO {high}]";
            return (query, low, high);
        }
    }
}