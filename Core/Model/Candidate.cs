namespace MetadataHealthCheck.v2.Core.Model
{
    // the universal "what are we guessing, and against what" envelope
    public class Candidate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SourceEntityId { get; set; } = "";
        public string TargetSystem { get; set; } = "";
        public string TargetEntityType { get; set; } = "";
        public string TargetId { get; set; } = "";

        // Human-readable label only, captured at generation time for logging.
        // Never used for identity/matching logic -- TargetId is the only match key.
        public string Name { get; set; } = "";

        public string GenerationStrategy { get; set; } = "";
        public string GenerationQuery { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}