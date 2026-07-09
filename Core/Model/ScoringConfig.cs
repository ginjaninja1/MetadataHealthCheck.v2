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
        public string Version { get; set; } = "phase1-default";

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
