using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // A distinct category from IObservationEvidenceCollector -- not a variant of
    // it, and not required to also implement it. Ordinary observation collectors
    // are called once per candidate, independently; this category is for
    // collectors whose underlying lookup is inherently shared across every live
    // candidate for one observation (one recording search, one relationship
    // fetch, checked against all candidates at once), where stopping early must
    // prevent further per-recording API calls from firing at all, not just skip
    // the next observation.
    //
    // CollectRounds yields one dictionary per round (candidate.Id -> newly-
    // produced evidence that round only, never a running total); the caller
    // merges each round in, re-scores, and checks the decision gate before
    // asking for the next round. Because this is yield-return-based, the next
    // round's API call genuinely never executes until the caller asks for it.
    public interface IRoundBasedObservationEvidenceCollector<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string EvidenceType { get; }
        IReadOnlyList<string> PossibleWeightedEvidenceTypes { get; }

        IEnumerable<IReadOnlyDictionary<string, IReadOnlyList<EvidenceRecord>>> CollectRounds(TSourceEntity source, IReadOnlyList<Candidate> candidates, IObservationUnit unit, ResolutionContext context);
    }
}
