namespace MetadataHealthCheck.v2.Core.Model
{
    public interface ISourceEntity
    {
        string SourceSystem { get; }   // "Emby"
        string EntityType { get; }     // "Artist" (Phase 1); "Album" later
        string SourceId { get; }       // Emby ItemId
        string DisplayName { get; }
    }

    /// <summary>
    /// One unit the Sequential Sampler (§5.5, Core/Engine/SequentialSampler.cs) can
    /// draw an observation from. Deliberately opaque to Core: for the Artist/MusicBrainz
    /// case a unit is one track and BucketKey is "AlbumArtist"/"Artist"/"Composer"
    /// (§5's role tiers); a future entity type with no natural role/bucket concept
    /// (e.g. Album, per §11.4's own example) can use a single constant BucketKey, or
    /// simply not implement IObservationUnitProvider at all.
    /// </summary>
    public interface IObservationUnit
    {
        string BucketKey { get; }

        // Added 2026-07-16 for SmokeTest readability (Nick's explicit request): the
        // sampler needs to announce each observation's raw content, one at a time, as
        // it's actually consumed -- not have a caller print the whole source entity's
        // observation list upfront, which misrepresents the one-at-a-time flow as a
        // batch. Deliberately just a human-readable string, same "opaque to Core"
        // philosophy as BucketKey -- Core/Engine never interprets what's inside it.
        string Describe();
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
        // Added 2026-07-18: MBIDs of artists this candidate "performs as" / "is person"
        // relations point to (e.g. a stage name's real-person MBID, or vice versa),
        // sourced from the artist stage's artist-rels lookup. Empty until the artist
        // candidate generator is updated to populate it -- until then this is inert and
        // every existing identity check (TargetId only) behaves exactly as before.
        public IReadOnlyList<string> RelationshipMbids { get; set; } = Array.Empty<string>();
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
        // Raw fact only (§5.4's "never pre-baked LLR" rule) -- whether a Corroboration
        // Tier hit's artist-credit text matched the candidate's primary name (false) or
        // only one of its registered aliases (true). Added 2026-07-13 alongside
        // CorroborationTierEvidenceCollector; the actual NameMatchWeight/AliasMatchWeight
        // multiplier this drives is applied by the scorer, not baked in here (§5.3/§6.3).
        public bool MatchedViaAlias { get; set; }
        // Added 2026-07-18, same rationale as MatchedViaAlias: whether a relationship
        // hit (WorkRelationship.*/RecordingRelationship.*) matched via one of the
        // candidate's RelationshipMbids (a performs-as/is-person identity) rather than
        // the candidate's own TargetId. Raw fact only -- no scoring weight attached yet;
        // kept distinct from MatchedViaAlias because the two are different confirmation
        // mechanisms (registered alias text vs. a separate artist-relationship MBID) and
        // may one day want different weights, logging, or review treatment.
        public bool MatchedViaRelationship { get; set; }
        // Added 2026-07-17 alongside the "opportunistic evidence" directive: relationship
        // evidence (WorkRelationship.*/RecordingRelationship.*) is pulled from the same
        // recording lookup as Corroboration Tier "because we're already there", purely so
        // we can see in the log whether it would ever have mattered -- NOT because it's
        // meant to influence auto_accept/needs_review/reject today. Contributing=false
        // means: still logged, still saved to the repository, but excluded from the sum
        // SimpleWeightedSumScorer actually uses to decide. Default true (the normal case)
        // so every existing collector's behavior is unchanged unless explicitly opted out.
        // Re-promote a type to Contributing=true only on a deliberate decision once we've
        // seen enough of these in real logs to know it's worth the cost (see
        // AlbumMatchEvidenceCollector, parked the same way).
        public bool Contributing { get; set; } = true;
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