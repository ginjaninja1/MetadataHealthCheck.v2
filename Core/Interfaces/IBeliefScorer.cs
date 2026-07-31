using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface IBeliefScorer<TConfig> where TConfig : IScoringConfig
    {
        ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar, TConfig config);
    }
}
