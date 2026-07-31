using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// Static (candidate-pair-level) evidence, computed once, not per
    /// observation. Normalized name distance between source display name and
    /// the candidate's MusicBrainz display name, bucketed into near-exact /
    /// close / poor / neutral.
    ///
    /// Always opportunistic/logged-only (Contributing=false): the candidate
    /// admission gate already used name/alias matching to decide whether this
    /// candidate exists at all, so this must not also silently add to the score
    /// on top of that.
    /// </summary>
    public class NameDistanceEvidenceCollector : IEvidenceCollector<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;

        public NameDistanceEvidenceCollector(IMusicBrainzApiClient client) => _client = client;

        public string EvidenceType => "NameSimilarity";

        // Empty, deliberately: this collector's NameSimilarity.* records are
        // always Contributing=false -- opportunistic/logged only, never scored.
        // An empty list here is itself the documentation of that fact, checkable
        // by EvidenceConfigValidator.
        public IReadOnlyList<string> PossibleWeightedEvidenceTypes => Array.Empty<string>();

        public EvidenceRecord? Collect(EmbyArtist source, Candidate candidate, ResolutionContext context)
        {
            var candidateName = _client.GetArtistDisplayName(candidate.TargetId);
            double distance = NameSimilarity.NormalizedSimilarity(source.DisplayName, candidateName);

            string bucket = distance >= 0.95 ? "NameSimilarity.NearExact"
                           : distance >= 0.85 ? "NameSimilarity.Close"
                           : distance < NameSimilarity.PoorMatchFloor ? "NameSimilarity.Poor"
                           : "NameSimilarity.Neutral"; // between 0.7 and 0.85 -- no catalog entry, contributes 0

            return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = bucket,
                RawValue = $"source=\"{source.DisplayName}\" candidate=\"{candidateName}\" similarity={distance:F2}",
                Role = null, // static evidence, not tied to a specific track/role
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
    }
}
