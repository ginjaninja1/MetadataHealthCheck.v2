namespace MetadataHealthCheck.v2.Core.Model
{
    public class ScoredCandidate
    {
        public Candidate Candidate { get; set; } = null!;
        public double RunningLlr { get; set; }
        public double Confidence => 1.0 / (1.0 + Math.Exp(-RunningLlr));
        public IReadOnlyList<EvidenceRecord> EvidenceSoFar { get; set; } = Array.Empty<EvidenceRecord>();
    }
}
