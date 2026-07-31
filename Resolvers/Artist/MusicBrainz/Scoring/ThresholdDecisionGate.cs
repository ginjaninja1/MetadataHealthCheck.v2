using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Scoring
{
    /// <summary>
    /// Three-way outcome -- auto-accept / auto-reject / needs-review -- evaluated
    /// against ArtistMusicBrainzConfig's thresholds.
    /// </summary>
    public class ThresholdDecisionGate : IDecisionGate<ArtistMusicBrainzConfig>
    {
        public MatchResult Decide(IEnumerable<ScoredCandidate> rankedCandidates, ArtistMusicBrainzConfig config, string sourceSystem, string sourceId)
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
                    Llr = 0,
                    Margin = 0,
                    ScoringConfigVersion = config.Version,
                    DecidedAt = DateTime.UtcNow,
                };
            }

            var top = ranked[0];
            // When there's no runner-up, the margin requirement is trivially
            // satisfied rather than blocking a lone-candidate accept.
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
                Llr = top.RunningLlr,
                Margin = ranked.Count > 1 ? top.RunningLlr - ranked[1].RunningLlr : top.RunningLlr,
                ScoringConfigVersion = config.Version,
                DecidedAt = DateTime.UtcNow,
            };
        }
    }
}
