namespace MetadataHealthCheck.v2.Core.Model
{
    public interface ISourceEntity
    {
        string SourceSystem { get; }   // "Emby"
        string EntityType { get; }     // "Artist" (Phase 1); "Album" later
        string SourceId { get; }       // Emby ItemId
        string DisplayName { get; }
    }

    public class Candidate
    {
        // Not present in the spec's §4 listing but required for EvidenceRecord.CandidateId
        // to reference anything concrete — filled gap, logged in Project Log Evidence section.
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SourceEntityId { get; set; } = "";      // Emby ItemId of the source entity this candidate was generated for
        public string TargetSystem { get; set; } = "";        // "MusicBrainz"
        public string TargetEntityType { get; set; } = "";     // "Artist"
        public string TargetId { get; set; } = "";              // MBID
        public string GenerationStrategy { get; set; } = "";    // "A" | "B" | "C"
        public string GenerationQuery { get; set; } = "";       // literal query string, for logging
        public DateTime CreatedAt { get; set; }
    }

    public class EvidenceRecord
    {
        public string CandidateId { get; set; } = "";
        public string EvidenceType { get; set; } = "";          // key into the evidence catalog, §6
        public string RawValue { get; set; } = "";               // observed fact — NEVER a pre-computed LLR (§14.2)
        public string? Role { get; set; }                        // AlbumArtist | Artist | Composer | null
        public string? SourceTrackId { get; set; }
        public string? AlbumId { get; set; }                      // for corroboration-tier supersession, §6.3
        public string? RelationshipType { get; set; }             // writer|producer|arranger|... , §7.2
        public string Rationale { get; set; } = "";               // human-readable sentence — always populated, §5.6
    }

    public class ScoredCandidate
    {
        public Candidate Candidate { get; set; } = null!;
        public double RunningLlr { get; set; }                    // nats
        public double Confidence => 1.0 / (1.0 + Math.Exp(-RunningLlr));
        public IReadOnlyList<EvidenceRecord> EvidenceSoFar { get; set; } = Array.Empty<EvidenceRecord>();
    }

    public class MatchResult
    {
        public string SourceSystem { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string TargetSystem { get; set; } = "";
        public string TargetEntityType { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string Status { get; set; } = "";                  // auto_accept | auto_reject | needs_review | human_confirmed | human_rejected
        public double Confidence { get; set; }
        public double Margin { get; set; }                        // over runner-up candidate
        public string ScoringConfigVersion { get; set; } = "";     // traceability
        public DateTime DecidedAt { get; set; }
        public string DecidedBy { get; set; } = "system";           // "system" | Emby user id
    }

    public class ResolutionContext
    {
        public CancellationToken CancellationToken { get; set; }
        public IProgress<double>? Progress { get; set; }
        public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    }
}
