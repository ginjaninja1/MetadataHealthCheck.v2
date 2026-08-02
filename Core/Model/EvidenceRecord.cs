namespace MetadataHealthCheck.v2.Core.Model
{
    // Deliberately minimal: only fields every resolver's evidence, regardless
    // of domain, actually has. A resolver whose evidence needs its own extra
    // detail fields (which track, which role, which relationship type, etc.)
    // defines its own subtype extending this one -- see
    // MusicBrainzEvidenceRecord for the pattern. Core, the sampler, and
    // IMatchRepository only ever read/write the base type; a resolver's own
    // collectors and reporting code are the only things that need the
    // subtype.
    public class EvidenceRecord
    {
        public string CandidateId { get; set; } = "";
        public string EvidenceType { get; set; } = "";
        public string RawValue { get; set; } = "";

        public bool MatchedViaAlias { get; set; }
        public bool MatchedViaRelationship { get; set; }

        // False marks evidence as opportunistic/logged-only: recorded and visible in
        // the trace, but excluded from the sum the scorer uses to reach a decision.
        public bool Contributing { get; set; } = true;

        public string Rationale { get; set; } = "";
    }
}