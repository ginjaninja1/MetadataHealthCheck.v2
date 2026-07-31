using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Scoring
{
    /// <summary>
    /// Sums evidence LLR values looked up from ArtistMusicBrainzConfig, applying
    /// a per-bucket role-weight multiplier to observation evidence tagged with
    /// a Role, and a match-quality multiplier (NameMatchWeight/AliasMatchWeight)
    /// to CorroborationTier evidence before role-weighting is applied.
    /// </summary>
    public class SimpleWeightedSumScorer : IBeliefScorer<ArtistMusicBrainzConfig>
    {
        public ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar, ArtistMusicBrainzConfig config)
        {
            var evidence = evidenceSoFar.ToList();

            double runningLlr = 0;
            foreach (var e in evidence)
            {
                if (!config.EvidenceWeights.TryGetValue(e.EvidenceType, out var llr))
                    continue; // Unrecognized/neutral evidence types (e.g. "NameSimilarity.Neutral") intentionally contribute 0 rather than throwing.

                if (e.EvidenceType.StartsWith("CorroborationTier."))
                    llr *= e.MatchedViaAlias ? config.AliasMatchWeight : config.NameMatchWeight;

                double weight = (e.Role != null && config.RoleWeights.TryGetValue(e.Role, out var w)) ? w : 1.0;
                runningLlr += llr * weight;
            }

            return new ScoredCandidate
            {
                Candidate = candidate,
                RunningLlr = runningLlr,
                EvidenceSoFar = evidence,
            };
        }
    }
}
