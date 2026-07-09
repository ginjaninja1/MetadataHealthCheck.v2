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
        private readonly IMusicBrainzApiClient _client;

        public NameDistanceEvidenceCollector(IMusicBrainzApiClient client) => _client = client;

        public string EvidenceType => "NameSimilarity";

        public EvidenceRecord? Collect(EmbyArtist source, Candidate candidate, ResolutionContext context)
        {
            var candidateName = _client.GetArtistDisplayName(candidate.TargetId);
            double distance = NormalizedSimilarity(source.DisplayName, candidateName);

            string bucket = distance >= 0.95 ? "NameSimilarity.NearExact"
                           : distance >= 0.85 ? "NameSimilarity.Close"
                           : distance < 0.7 ? "NameSimilarity.Poor"
                           : "NameSimilarity.Neutral"; // between 0.7 and 0.85 — no catalog entry, contributes 0

            return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = bucket,
                RawValue = $"source=\"{source.DisplayName}\" candidate=\"{candidateName}\" similarity={distance:F2}",
                Role = null, // static evidence, not tied to a specific track/role
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

        private static int Levenshtein(string a, string b)
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
    }
}
