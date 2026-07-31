using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Static, candidate-pair-level evidence -- computed once per candidate,
    // e.g. name similarity. Called once, not per observation.
    public interface IEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }

        // The fixed, complete set of EvidenceRecord.EvidenceType strings this
        // collector can ever emit with Contributing=true -- i.e. the exact
        // ScoringConfig.EvidenceWeights keys it depends on existing. Empty if
        // this collector never contributes to scoring (opportunistic/logged only).
        // Lets EvidenceConfigValidator catch drift between what a collector emits
        // and what the config has weights for.
        IReadOnlyList<string> PossibleWeightedEvidenceTypes { get; }

        EvidenceRecord? Collect(TSourceEntity source, Candidate candidate, ResolutionContext context);
    }
}
