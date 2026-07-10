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

    // Static, candidate-pair-level evidence (§5.4) -- computed once per candidate,
    // e.g. name similarity. Called once, not per observation.
    public interface IEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        EvidenceRecord? Collect(TSourceEntity source, Candidate candidate, ResolutionContext context);
    }

    // Per-observation evidence (§5.4) -- re-run once per sampled IObservationUnit,
    // as many times as the Sequential Sampler (§5.5) draws units. Never called
    // directly by anything except SequentialSampler.
    public interface IObservationEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        EvidenceRecord? Collect(TSourceEntity source, Candidate candidate, IObservationUnit unit, ResolutionContext context);
    }

    // Optional -- entity types with no natural observation/role concept (§11.4's
    // Album example) simply don't provide one; IResolverPlugin.ObservationUnitProvider
    // is nullable for exactly this reason.
    public interface IObservationUnitProvider<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        // Outer sequence: buckets, in priority sampling order (e.g. AlbumArtist, Artist,
        // Composer, highest signal first). Inner sequence: units within that bucket,
        // already in distance-seeking sample order (§5.5.1). SequentialSampler consumes
        // both orderings as given -- it does not re-sort either one itself.
        IEnumerable<IEnumerable<IObservationUnit>> GetOrderedBuckets(TSourceEntity source, ResolutionContext context);
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
        // Empty list if the entity type has no per-observation evidence at all.
        IEnumerable<IObservationEvidenceCollector<TSourceEntity>> ObservationEvidenceCollectors { get; }
        // Null if the entity type has no observation/role concept (§11.4) -- SequentialSampler
        // then scores from static evidence alone, same as Phase 1's behavior.
        IObservationUnitProvider<TSourceEntity>? ObservationUnitProvider { get; }
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