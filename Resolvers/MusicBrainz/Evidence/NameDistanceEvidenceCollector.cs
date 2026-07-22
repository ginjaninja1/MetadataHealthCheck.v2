using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Static (candidate-pair-level) evidence — computed once, not per observation
    /// (§5.4). Normalized name distance between source display name and the
    /// candidate's MB display name, bucketed per §6.1: near-exact / close / poor.
    /// </summary>
    public class NameDistanceEvidenceCollector : IEvidenceCollector<EmbyArtist>
    {
        // Existing Poor/Neutral boundary, extracted as a named constant 2026-07-13
        // rather than changed — same value (0.7), now reused by EvaluateRecordingMatch
        // below as the "too poor a match to trust at any rung" floor (§5.4), which is a
        // distinct use from this collector's own (currently still-wired, still open
        // per Project Log) role as scored evidence.
        private const double PoorMatchFloor = 0.7;

        private readonly IMusicBrainzApiClient _client;

        public NameDistanceEvidenceCollector(IMusicBrainzApiClient client) => _client = client;

        public string EvidenceType => "NameSimilarity";

        // Empty, deliberately: this collector's NameSimilarity.* records are always
        // Contributing=false (§ settled directive, 2026-07-17) -- opportunistic/logged
        // only, never scored. An empty list here is itself the documentation of that
        // fact, checkable by EvidenceConfigValidator rather than only stated in a
        // comment a future reader might not see.
        public IReadOnlyList<string> PossibleWeightedEvidenceTypes => Array.Empty<string>();

        public EvidenceRecord? Collect(EmbyArtist source, Candidate candidate, ResolutionContext context)
        {
            var candidateName = _client.GetArtistDisplayName(candidate.TargetId);
            double distance = NormalizedSimilarity(source.DisplayName, candidateName);

            string bucket = distance >= 0.95 ? "NameSimilarity.NearExact"
                           : distance >= 0.85 ? "NameSimilarity.Close"
                           : distance < PoorMatchFloor ? "NameSimilarity.Poor"
                           : "NameSimilarity.Neutral"; // between 0.7 and 0.85 — no catalog entry, contributes 0

            return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = bucket,
                RawValue = $"source=\"{source.DisplayName}\" candidate=\"{candidateName}\" similarity={distance:F2}",
                Role = null, // static evidence, not tied to a specific track/role
                // Contributing=false (2026-07-17 settled directive): only CorroborationTier
                // evidence, produced solely inside RecordingCorroborationEvidenceCollector
                // from a confirmed recording, is allowed to affect auto_accept/needs_review/
                // reject. NameSimilarity is still computed and logged (name match is what
                // SoftBucketStrategy's admission gate already used to decide whether this
                // candidate exists at all) but must not also silently add to the score on
                // top of that. Revisit only as a deliberate decision, not a default.
                Contributing = false,
                Rationale = $"MusicBrainz artist name \"{candidateName}\" compared to Emby's \"{source.DisplayName}\" ({DescribeBucket(bucket)}).",
            };
        }

        private static string DescribeBucket(string bucket) => bucket switch
        {
            "NameSimilarity.NearExact" => "near-exact match",
            "NameSimilarity.Close" => "close match",
            "NameSimilarity.Poor" => "poor match",
            _ => "no strong signal either way",
        };

        // Simple normalized Levenshtein similarity, case-insensitive. Sufficient for
        // Phase 1; swap for a more MusicBrainz-tuned distance metric later if
        // calibration (§16) shows the default under/over-discriminates.
        internal static double NormalizedSimilarity(string a, string b)
        {
            a = a.Trim().ToLowerInvariant();
            b = b.Trim().ToLowerInvariant();
            if (a == b) return 1.0;
            int dist = Levenshtein(a, b);
            int maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 1.0;
            return 1.0 - (double)dist / maxLen;
        }

        // Added 2026-07-13: raw edit distance, exposed (was private) for
        // SoftBucketStrategy's Stage 1 admission gate (§5.3), which needs the actual
        // character-count distance against ArtistCandidateMaxEditDistance, not a
        // normalized 0-1 ratio. Same algorithm NormalizedSimilarity already used
        // internally — visibility change only, no behavior change.
        internal static int Levenshtein(string a, string b)
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

        // Added 2026-07-13 for RecordingLookup (§5.4): given a RecordingLookup hit's raw
        // artist-credit text, decide whether it's (a) a genuine match against the
        // candidate's primary name, (b) a genuine match against one of its registered
        // aliases only (MatchedViaAlias — discounted via AliasMatchWeight at scoring
        // time, §6.3), or (c) too poor a match to trust at all — the real safety net
        // against a wrong-album/wrong-artist-text fallback rung (§5.4), reusing this
        // collector's existing Poor-match floor rather than a second calibration.
        internal static NameMatchOutcome EvaluateRecordingMatch(string candidateName, IReadOnlyList<string> candidateAliases, string artistCreditText)
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

    // Outcome of NameDistanceEvidenceCollector.EvaluateRecordingMatch — a small,
    // dedicated enum rather than reusing the NameSimilarity.* bucket strings, since
    // this is a reject/accept decision (§5.4), not a scored evidence type (§6.1).
    internal enum NameMatchOutcome
    {
        MatchedViaName,
        MatchedViaAlias,
        TooPoorToTrust,
    }
}