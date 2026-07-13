using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Scoring
{
    /// <summary>
    /// Sums evidence LLR values looked up from ScoringConfig, applying a per-bucket
    /// role-weight multiplier (§6.4) to observation evidence tagged with a Role.
    ///
    /// Two additional rules implemented here (2026-07-13, correcting a Project Log
    /// entry that claimed the first of these already existed when it didn't):
    ///
    ///   §5.2 supersession: AlbumMatchEvidenceCollector's precursor evidence for a
    ///   given album is dropped if a Tier 1 (full-triple) corroboration evidence
    ///   record exists for that SAME AlbumId — this has to happen here, at scoring
    ///   time, because the precursor always runs before any per-track observation
    ///   exists and can't know in advance whether a Tier 1 hit will arrive.
    ///
    ///   §6.3 match-quality multiplier: Corroboration Tier evidence's LLR is
    ///   multiplied by NameMatchWeight or AliasMatchWeight (per
    ///   EvidenceRecord.MatchedViaAlias) BEFORE role-weighting is applied.
    ///
    /// Still no general joint-evidence rules (correlated-pair handling beyond this
    /// one named case, §5.4) -- that, plus the fuller Bayesian treatment, is
    /// BayesianBeliefScorer.cs's job, not yet built.
    /// </summary>
    public class SimpleWeightedSumScorer : IBeliefScorer
    {
        public ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar, ScoringConfig config)
        {
            var evidence = evidenceSoFar.ToList();

            // .ToHashSet() isn't available on netstandard2.0 (needs netstandard2.1+) —
            // this project's actual build target, confirmed by the real build error
            // this replaced (CS1061). Construct the HashSet directly instead.
            var tier1AlbumIds = new HashSet<string>(
                evidence
                    .Where(e => e.EvidenceType == "CorroborationTier.Tier1" && e.AlbumId != null)
                    .Select(e => e.AlbumId!));

            double runningLlr = 0;
            foreach (var e in evidence)
            {
                if (e.EvidenceType.StartsWith("AlbumMatch.") && e.AlbumId != null && tier1AlbumIds.Contains(e.AlbumId))
                    continue; // §5.2: superseded by a Tier 1 hit for the same album

                if (!config.EvidenceWeights.TryGetValue(e.EvidenceType, out var llr))
                    continue; // Unrecognized/neutral evidence types (e.g. "NameSimilarity.Neutral")
                              // intentionally contribute 0 rather than throwing — matches §6.1's
                              // "Neutral" bucket in the user-facing translation layer, §5.6.

                if (e.EvidenceType.StartsWith("CorroborationTier."))
                    llr *= e.MatchedViaAlias ? config.AliasMatchWeight : config.NameMatchWeight; // §6.3

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