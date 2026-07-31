namespace MetadataHealthCheck.v2.Core.Model
{
    public class ResolutionContext
    {
        public CancellationToken CancellationToken { get; set; }
        public IProgress<double>? Progress { get; set; }
        public string RunId { get; set; } = Guid.NewGuid().ToString("N");

        // Set by candidate-generation strategies when two admitted candidates are
        // folded into one because the target system's own relationship data says
        // they represent the same real-world identity. The engine reads this after
        // the decision gate runs to force needs_review while this rule is on probation.
        public bool CandidateFoldOccurred { get; set; }
        public List<string> FoldNotes { get; set; } = new();

        // Generic per-resolution extension slot, keyed by type. Core has no
        // concept of what's stored here -- a resolver's candidate-generation
        // strategy can stash its own opaque side-data during GenerateCandidates
        // and read it back during IBucketCandidateFilter.Filter or
        // IRoundBasedObservationEvidenceCollector.CollectRounds, since all
        // three already receive this same ResolutionContext instance for one
        // resolution. Correctly scoped by construction: callers create a fresh
        // ResolutionContext per source-entity resolution (see SmokeTest/
        // BatchHarness's per-artist context, required so CandidateFoldOccurred
        // doesn't leak across artists), so anything stored here is discarded
        // along with the context at the same point.
        private readonly Dictionary<Type, object> _extensions = new();

        public void SetExtension<T>(T value) where T : class => _extensions[typeof(T)] = value;

        public T? GetExtension<T>() where T : class =>
            _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;
    }
}