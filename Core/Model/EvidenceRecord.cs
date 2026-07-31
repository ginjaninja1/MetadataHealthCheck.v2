namespace MetadataHealthCheck.v2.Core.Model
{
    public class EvidenceRecord
    {
        public string CandidateId { get; set; } = "";
        public string EvidenceType { get; set; } = "";
        public string RawValue { get; set; } = "";
        public string? Role { get; set; }
        public string? SourceTrackId { get; set; }
        public string? AlbumId { get; set; }
        public string? RelationshipType { get; set; }

        public bool MatchedViaAlias { get; set; }
        public bool MatchedViaRelationship { get; set; }

        // False marks evidence as opportunistic/logged-only: recorded and visible in
        // the trace, but excluded from the sum the scorer uses to reach a decision.
        public bool Contributing { get; set; } = true;

        // The specific rung a corroboration hit was confirmed at, if evidence type is rung-based.
        public string? Rung { get; set; }

        public string Rationale { get; set; } = "";
    }
}
