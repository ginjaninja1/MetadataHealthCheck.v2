using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface ISourceEntityProvider<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        IEnumerable<TSourceEntity> GetAll(ResolutionContext context);
    }

    // Optional. A source entity type with no natural processing order returns entities unchanged.
    public interface IProcessingOrderStrategy<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        IEnumerable<TSourceEntity> OrderForProcessing(IEnumerable<TSourceEntity> all);
    }

    public interface ICandidateGenerationStrategy<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string StrategyName { get; }
        int Priority { get; }
        IEnumerable<Candidate> GenerateCandidates(TSourceEntity source, ResolutionContext context);
    }

    public interface IEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        EvidenceRecord? Collect(TSourceEntity source, Candidate candidate, ResolutionContext context);
    }

    public interface IBeliefScorer
    {
        ScoredCandidate Score(Candidate candidate, IEnumerable<EvidenceRecord> evidenceSoFar, ScoringConfig config);
    }

    public interface IDecisionGate
    {
        MatchResult Decide(IEnumerable<ScoredCandidate> rankedCandidates, ScoringConfig config, string sourceSystem, string sourceId);
    }

    // The unit of extensibility for new target systems/entity types: one implementation
    // per (target system, target entity type) pair. §11.4.
    public interface IResolverPlugin<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string TargetSystem { get; }
        string TargetEntityType { get; }
        IEnumerable<ICandidateGenerationStrategy<TSourceEntity>> Strategies { get; }
        IEnumerable<IEvidenceCollector<TSourceEntity>> EvidenceCollectors { get; }
        IBeliefScorer Scorer { get; }
        IDecisionGate DecisionGate { get; }
    }

    public interface IIdentityCache
    {
        MatchResult? Get(string sourceSystem, string sourceId, string targetSystem);
        void Set(string sourceSystem, string sourceId, string targetSystem, string targetId, double confidence);
    }

    public interface IMatchRepository
    {
        void SaveCandidate(Candidate candidate);
        void SaveEvidence(EvidenceRecord evidence);
        void SaveMatchResult(MatchResult result);
        MatchResult? GetExisting(string sourceSystem, string sourceId, string targetSystem);
    }
}
