using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Contract: Collect is given only the source entity and one candidate --
    // no IObservationUnit, ever. That's the one fact this interface promises
    // and the only thing that distinguishes it from IPerUnitEvidenceCollector
    // and IJointCandidateEvidenceCollector (both of which are fed a unit).
    // The engine calls it exactly once per candidate, before any observation
    // sampling begins.
    //
    // What it's typically for: anything you can compute from the candidate
    // itself with no per-unit lookup -- e.g. name similarity between source
    // and candidate. Nothing stops an implementation from doing an API call
    // inside Collect, but if you find yourself wanting to look at more than
    // one candidate at once, or needing a specific track/episode/whatever to
    // check against, you want IPerUnitEvidenceCollector or
    // IJointCandidateEvidenceCollector instead -- this interface's shape
    // can't give you either of those.
    public interface ICandidateEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
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