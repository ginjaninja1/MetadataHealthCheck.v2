namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    /// <summary>
    /// Pure name-comparison functions shared across candidate generation
    /// (match-tier classification), evidence collection (name-distance logging),
    /// and recording confirmation (trusting a recording's artist-credit text).
    /// </summary>
    internal static class NameSimilarity
    {
        // Below this normalized similarity, a comparison is too poor to trust at
        // any rung -- the floor used both for evidence bucketing and for
        // deciding whether a recording's artist-credit text is trustworthy.
        public const double PoorMatchFloor = 0.7;

        // Simple normalized Levenshtein similarity, case-insensitive.
        public static double NormalizedSimilarity(string a, string b)
        {
            a = a.Trim().ToLowerInvariant();
            b = b.Trim().ToLowerInvariant();
            if (a == b) return 1.0;
            int dist = Levenshtein(a, b);
            int maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 1.0;
            return 1.0 - (double)dist / maxLen;
        }

        public static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }

        // Given a recording lookup hit's raw artist-credit text, decides whether
        // it's a genuine match against the candidate's primary name, a genuine
        // match against one of its registered aliases only, or too poor a match
        // to trust at all.
        public static NameMatchOutcome EvaluateRecordingMatch(string candidateName, IReadOnlyList<string> candidateAliases, string artistCreditText)
        {
            if (NormalizedSimilarity(candidateName, artistCreditText) >= PoorMatchFloor)
                return NameMatchOutcome.MatchedViaName;

            foreach (var alias in candidateAliases)
            {
                if (NormalizedSimilarity(alias, artistCreditText) >= PoorMatchFloor)
                    return NameMatchOutcome.MatchedViaAlias;
            }

            return NameMatchOutcome.TooPoorToTrust;
        }
    }
}
