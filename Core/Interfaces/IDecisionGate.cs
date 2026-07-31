using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface IDecisionGate<TConfig> where TConfig : IScoringConfig
    {
        MatchResult Decide(IEnumerable<ScoredCandidate> rankedCandidates, TConfig config, string sourceSystem, string sourceId);
    }
}
