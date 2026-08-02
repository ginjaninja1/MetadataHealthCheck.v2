using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // The one evidence-collector shape. Replaces the previous three
    // (ICandidateEvidenceCollector, IPerUnitEvidenceCollector,
    // IJointCandidateEvidenceCollector) -- those encoded a resolver-specific
    // implementation choice (does my lookup naturally answer for one
    // candidate or all of them at once?) as if it were an engine-level fact.
    // It isn't; the engine only cares about timing, not that.
    //
    // CollectRounds is called by the sampler at exactly two points:
    //   1. Once, before any observation sampling starts, with unit == null.
    //      Use this call for anything computable from the candidate(s) alone
    //      (e.g. name similarity) -- yield nothing on every other call.
    //   2. Once per observation unit the sampler draws, with unit non-null,
    //      given every live candidate for that unit together. Use this call
    //      for anything that needs a specific observation -- yield nothing on
    //      the unit == null call.
    // A collector decides for itself which of these calls it responds to; it
    // is never required to handle both.
    //
    // CollectRounds yields one dictionary per round (candidate.Id -> newly-
    // produced evidence that round only, never a running total). The caller
    // merges each round in, re-scores, and checks the decision gate before
    // asking for the next round -- because this is yield-return-based,
    // stopping early (the caller simply doesn't ask for the next round) means
    // the next round's underlying lookup genuinely never fires. A collector
    // with only one round just yields once and is done.
    public interface IEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }

        // The fixed, complete set of EvidenceRecord.EvidenceType strings this
        // collector can ever emit with Contributing=true -- i.e. the exact
        // ScoringConfig.EvidenceWeights keys it depends on existing. Empty if
        // this collector never contributes to scoring (opportunistic/logged
        // only). Lets EvidenceConfigValidator catch drift between what a
        // collector emits and what the config has weights for.
        IReadOnlyList<string> PossibleWeightedEvidenceTypes { get; }

        IEnumerable<IReadOnlyDictionary<string, IReadOnlyList<EvidenceRecord>>> CollectRounds(
            TSourceEntity source, IReadOnlyList<Candidate> candidates, IObservationUnit? unit, ResolutionContext context);
    }
}