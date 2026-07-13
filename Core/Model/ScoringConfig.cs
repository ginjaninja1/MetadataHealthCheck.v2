namespace MetadataHealthCheck.v2.Core.Model
{
    /// <summary>
    /// Phase 1 subset of §10.3's ScoringConfig — only what the simple weighted-sum
    /// scorer and decision gate need. Full grid-editable version (BucketCeiling,
    /// JointEvidencePairs, role weights, etc.) arrives in Phase 5 per §21.
    /// </summary>
    public class ScoringConfig
    {
        public double AutoAcceptThreshold { get; set; } = 4.0;
        public double AutoRejectThreshold { get; set; } = -3.0;
        public double MinMarginOverRunnerUp { get; set; } = 2.0;
        public string Version { get; set; } = "phase2-default";

        // Admission-gate floor on MusicBrainz's own artist-search text-relevance score
        // (§5.3/§5.4), added 2026-07-13 alongside SoftBucketStrategy's artist-search-first
        // rewrite. The gate itself is real and exercised now (SoftBucketStrategy actually
        // compares each SearchArtist result's Score against this); the default of 0 just
        // means every real MB result passes for now, since there's no output yet to
        // calibrate a real cutoff against. Not a stub -- tune this once there's real data.
        public int ArtistCandidateMinScore { get; set; } = 0;

        // Sampling budget per bucket (§5.5) -- a ceiling, not a target. The sampler
        // stops as soon as confidence crosses a bound, which may happen well before
        // a bucket's ceiling is reached (§18's worked example: AlbumArtist ceiling of
        // 3 never approached, resolved after 1 observation).
        public Dictionary<string, int> BucketCeiling { get; set; } = new()
        {
            ["AlbumArtist"] = 3,
            ["Artist"] = 4,
            ["Composer"] = 6,
        };

        // Multiplier applied to per-observation evidence based on which bucket it
        // came from (§6.4). Starting neutral (1.0 everywhere) -- tune once there's
        // real output to look at, per-bucket, rather than guessing up front.
        public Dictionary<string, double> RoleWeights { get; set; } = new()
        {
            ["AlbumArtist"] = 1.0,
            ["Artist"] = 1.0,
            ["Composer"] = 1.0,
        };

        // One entry per §6.1 evidence type. Raw evidence -> LLR lookup happens here,
        // never baked into the EvidenceRecord itself (§5.4, required for re-score).
        public Dictionary<string, double> EvidenceWeights { get; set; } = new()
        {
            ["NameSimilarity.NearExact"] = 1.5,
            ["NameSimilarity.Close"] = 0.5,
            ["NameSimilarity.Poor"] = -2.0,
            ["WorkRelationship.Writer"] = 2.5,
            ["WorkRelationship.Composer"] = 2.5,
            ["WorkRelationship.Lyricist"] = 2.5,
        };
    }
}