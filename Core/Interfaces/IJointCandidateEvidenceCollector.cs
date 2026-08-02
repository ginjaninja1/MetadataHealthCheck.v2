using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Contract: CollectRounds is given the source entity, ALL live candidates
    // together, and ONE IObservationUnit -- the one fact that distinguishes
    // this from IPerUnitEvidenceCollector, which only ever sees one candidate
    // at a time. Use this when your underlying lookup is inherently shared
    // across every live candidate for one observation (one search, one
    // relationship fetch, checked against all candidates at once) -- doing
    // that per-candidate via IPerUnitEvidenceCollector would repeat the same
    // shared lookup once per candidate for no reason. Not a variant of
    // IPerUnitEvidenceCollector and not required to also implement it.
    //
    // CollectRounds yields one dictionary per round (candidate.Id -> newly-
    // produced evidence that round only, never a running total); the caller
    // merges each round in, re-scores, and checks the decision gate before
    // asking for the next round. Because this is yield-return-based, stopping
    // early (the caller simply doesn't ask for the next round) means the next
    // round's underlying lookup genuinely never fires -- this matters when
    // stopping early needs to prevent further API calls from firing at all,
    // not just skip processing their result.
    public interface IJointCandidateEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        IReadOnlyList<string> PossibleWeightedEvidenceTypes { get; }

        IEnumerable<IReadOnlyDictionary<string, IReadOnlyList<EvidenceRecord>>> CollectRounds(TSourceEntity source, IReadOnlyList<Candidate> candidates, IObservationUnit unit, ResolutionContext context);
    }
}