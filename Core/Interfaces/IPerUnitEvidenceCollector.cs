using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Contract: Collect is given the source entity, ONE candidate, and ONE
    // IObservationUnit -- called independently, once per (candidate, unit)
    // pair, for every unit the sampler draws. That's the promise: you see one
    // candidate at a time, never the whole live set together. If your
    // underlying lookup is naturally shared across every candidate at once
    // (one search whose result you then check against each candidate), use
    // IJointCandidateEvidenceCollector instead -- doing that here means
    // re-running the same shared lookup once per candidate for no reason.
    //
    // Called by the sampler only, never called directly by anything else.
    // Collect returns zero or more EvidenceRecords (never null) when there's
    // nothing to report for this (candidate, unit) pair.
    public interface IPerUnitEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }

        IReadOnlyList<string> PossibleWeightedEvidenceTypes { get; }

        IEnumerable<EvidenceRecord> Collect(TSourceEntity source, Candidate candidate, IObservationUnit unit, ResolutionContext context);
    }
}