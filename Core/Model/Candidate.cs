namespace MetadataHealthCheck.v2.Core.Model
{
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

        // MBIDs of other artists this candidate is known to be related to
        // (e.g. "performs as"/"is person"), for corroboration and identity folding.
        public IReadOnlyList<string> RelationshipMbids { get; set; } = Array.Empty<string>();

        // MusicBrainz's own artist "type" (e.g. "Person", "Group"). Not a candidate-
        // admission gate; used only by pathway-local bucket filters.
        public string Type { get; set; } = "";

        // Subset of RelationshipMbids specifically representing group-membership
        // relationships (e.g. "member of band"), for bucket filters that need to
        // distinguish this from identity relationships.
        public IReadOnlyList<string> GroupMembershipMbids { get; set; } = Array.Empty<string>();
    }
}
