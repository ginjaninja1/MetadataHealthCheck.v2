namespace MetadataHealthCheck.v2.Core.Model
{
    public class MatchResult
    {
        public string SourceSystem { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string TargetSystem { get; set; } = "";
        public string TargetEntityType { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string Status { get; set; } = "";
        public double Confidence { get; set; }
        public double Llr { get; set; }
        public double Margin { get; set; }
        public string ScoringConfigVersion { get; set; } = "";
        public DateTime DecidedAt { get; set; }
        public string DecidedBy { get; set; } = "system";

        // Set only when the decision gate's normal status is overridden for a
        // reason outside the usual LLR/margin math (e.g. a candidate identity fold).
        public string? DecisionReason { get; set; }
    }
}
