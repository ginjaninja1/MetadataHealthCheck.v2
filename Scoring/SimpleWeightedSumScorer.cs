using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Scoring
{
    /// <summary>
    /// Sums evidence LLR values looked up from ScoringConfig, applying a per-bucket
    /// role-weight multiplier (§6.4) to observation evidence tagged with a Role.
    /// Still no joint-evidence rules (correlated-pair handling, §5.4) -- that, plus
    /// the fuller Bayesian treatment, is BayesianBeliefScorer.cs's job, not yet built.
    /// This scorer is deliberately kept as the Phase 1 stand-in a little longer since
    /// it already satisfies what the Sequential Sampler needs (§21 phase 2).
    /// </summary>
    public class SimpleWeightedSumScorer : IBeliefScorer
    {
        public ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar, ScoringConfig config)
        {
            var evidence = evidenceSoFar.ToList();
            double runningLlr = 0;
            foreach (var e in evidence)
            {
                if (!config.EvidenceWeights.TryGetValue(e.EvidenceType, out var llr))
                    continue; // Unrecognized/neutral evidence types (e.g. "NameSimilarity.Neutral")
                              // intentionally contribute 0 rather than throwing — matches §6.1's
                              // "Neutral" bucket in the user-facing translation layer, §5.6.

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