using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Scoring
{
    /// <summary>
    /// Phase 1 scorer: sums evidence LLR values looked up from ScoringConfig.
    /// No role-weight multipliers, no joint-evidence rules, no sequential
    /// sampler — those arrive in Phase 2 (BayesianBeliefScorer.cs) once the
    /// full evidence catalog and sampler are built (§21).
    /// </summary>
    public class SimpleWeightedSumScorer : IBeliefScorer
    {
        public ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar, ScoringConfig config)
        {
            var evidence = evidenceSoFar.ToList();
            double runningLlr = 0;
            foreach (var e in evidence)
            {
                if (config.EvidenceWeights.TryGetValue(e.EvidenceType, out var llr))
                    runningLlr += llr;
                // Unrecognized/neutral evidence types (e.g. "NameSimilarity.Neutral")
                // intentionally contribute 0 rather than throwing — matches §6.1's
                // "Neutral" bucket in the user-facing translation layer, §5.6.
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
