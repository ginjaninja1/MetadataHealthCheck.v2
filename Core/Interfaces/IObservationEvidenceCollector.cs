using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Per-observation evidence -- re-run once per sampled IObservationUnit, as
    // many times as the sequential sampler draws units. Never called directly by
    // anything except the sampler. Collect returns zero or more EvidenceRecords
    // (never null) when there's nothing to report.
    public interface IObservationEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }

        IReadOnlyList<string> PossibleWeightedEvidenceTypes { get; }

        IEnumerable<EvidenceRecord> Collect(TSourceEntity source, Candidate candidate, IObservationUnit unit, ResolutionContext context);
    }
}
