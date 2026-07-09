using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Scoring
{
    /// <summary>
    /// Three-way outcome per §5.7: auto-accept / auto-reject / needs-review,
    /// evaluated against ScoringConfig thresholds (§6.4).
    /// </summary>
    public class ThresholdDecisionGate : IDecisionGate
    {
        public MatchResult Decide(IEnumerable<ScoredCandidate> rankedCandidates, ScoringConfig config, string sourceSystem, string sourceId)
        {
            var ranked = rankedCandidates.OrderByDescending(c => c.RunningLlr).ToList();
            if (ranked.Count == 0)
            {
                return new MatchResult
                {
                    SourceSystem = sourceSystem,
                    SourceId = sourceId,
                    TargetSystem = "MusicBrainz",
                    TargetEntityType = "Artist",
                    Status = "needs_review",
                    Confidence = 0,
                    Margin = 0,
                    ScoringConfigVersion = config.Version,
                    DecidedAt = DateTime.UtcNow,
                };
            }

            var top = ranked[0];
            // When there's no runner-up, the margin requirement is trivially satisfied
            // rather than blocking a lone-candidate accept.
            bool hasMargin = ranked.Count == 1
                || (top.RunningLlr - ranked[1].RunningLlr) >= config.MinMarginOverRunnerUp;

            string status;
            if (top.RunningLlr >= config.AutoAcceptThreshold && hasMargin)
                status = "auto_accept";
            else if (ranked.All(c => c.RunningLlr <= config.AutoRejectThreshold))
                status = "auto_reject";
            else
                status = "needs_review";

            return new MatchResult
            {
                SourceSystem = sourceSystem,
                SourceId = sourceId,
                TargetSystem = top.Candidate.TargetSystem,
                TargetEntityType = top.Candidate.TargetEntityType,
                TargetId = top.Candidate.TargetId,
                Status = status,
                Confidence = top.Confidence,
                Margin = ranked.Count > 1 ? top.RunningLlr - ranked[1].RunningLlr : top.RunningLlr,
                ScoringConfigVersion = config.Version,
                DecidedAt = DateTime.UtcNow,
            };
        }
    }
}
