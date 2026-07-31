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
    }
}
