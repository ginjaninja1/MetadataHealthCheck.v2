using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // The unit of extensibility for new target systems/entity types: one
    // implementation per (target system, target entity type) pair. TConfig is
    // that resolver's own tuning-config type; Core only ever requires it to
    // satisfy IScoringConfig.
    public interface IResolverPlugin<TSourceEntity, TConfig>
        where TSourceEntity : ISourceEntity
        where TConfig : IScoringConfig
    {
        string TargetSystem { get; }
        string TargetEntityType { get; }
        IEnumerable<ICandidateGenerationStrategy<TSourceEntity>> Strategies { get; }
        IEnumerable<IEvidenceCollector<TSourceEntity>> EvidenceCollectors { get; }

        // Empty list if the entity type has no per-observation evidence at all.
        IEnumerable<IObservationEvidenceCollector<TSourceEntity>> ObservationEvidenceCollectors { get; }

        // Empty list for any plugin that has no round-based collectors at all.
        IEnumerable<IRoundBasedObservationEvidenceCollector<TSourceEntity>> RoundBasedObservationEvidenceCollectors { get; }

        // Null if the entity type has no observation/role concept -- the sampler
        // then scores from static evidence alone.
        IObservationUnitProvider<TSourceEntity>? ObservationUnitProvider { get; }

        // Null if the plugin has no pathway-local fold rule for any bucket.
        IBucketCandidateFilter? BucketCandidateFilter { get; }

        IBeliefScorer<TConfig> Scorer { get; }
        IDecisionGate<TConfig> DecisionGate { get; }
    }
}
