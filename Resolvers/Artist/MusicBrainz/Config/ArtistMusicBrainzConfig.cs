using MetadataHealthCheck.v2.Core.Interfaces;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config
{
    /// <summary>
    /// All tuning knobs for the Artist/MusicBrainz resolver: candidate admission,
    /// recording-lookup gating/tolerances, and scoring/decision thresholds.
    /// Implements Core's minimal IScoringConfig so the engine/sampler can read
    /// EvidenceWeights/BucketCeiling without depending on this concrete type.
    /// </summary>
    public class ArtistMusicBrainzConfig : IScoringConfig
    {
        // A single Tier2 (track+artist, no album) confirmation with no
        // competing candidate is sufficient on its own to auto-accept and stop
        // further sampling for the Audio Artist bucket -- a deliberate,
        // permanent lowering of the bar (settled, not to be re-opened without
        // a new directive), trading a small amount of accuracy for a large
        // reduction in MusicBrainz API load on high-collision names.
        public double AutoAcceptThreshold { get; set; } = 1.5;
        public double AutoRejectThreshold { get; set; } = -3.0;
        public double MinMarginOverRunnerUp { get; set; } = 1.5;
        public string Version { get; set; } = "phase2-default";

        public CandidateGenerationConfig CandidateGeneration { get; set; } = new();

        // Multiplies whatever corroboration-tier LLR a recording-lookup hit
        // would otherwise contribute, before role-weighting is applied,
        // depending on whether the match was against the candidate's primary
        // name or only a registered alias (EvidenceRecord.MatchedViaAlias).
        // Applied by SimpleWeightedSumScorer, not baked into the raw EvidenceRecord.
        public double NameMatchWeight { get; set; } = 1.0;
        public double AliasMatchWeight { get; set; } = 0.9;

        // Recording-level duration is used as a gate in RecordingLookup, before
        // any relationship-scan confirmation is attempted: percentage-based
        // rather than a flat number of seconds, since legitimate duration
        // variance exists even within one correct recording across different
        // releases. Unvalidated placeholder -- revisit once real resolution
        // volume gives data to tune against.
        public double DurationGateTolerancePercent { get; set; } = 0.05;

        // The duration gate exists to disambiguate the PerformerOnly (name-based)
        // pathway, where MusicBrainz's own relevance score gives no disambiguation
        // power on its own. The RelationshipOnly pathway (Composer bucket) confirms
        // by exact MBID match against relationship data -- a fundamentally
        // stricter check that doesn't need a duration pre-filter, and the gate can
        // only cost it a correct-but-differently-timed recording (e.g. a
        // movement/edit length variance) being excluded before its relationship
        // data is ever scanned. Default false: the gate isn't warranted once the
        // RelationshipOnly pathway exists.
        public bool EnableRecordingDurationGate { get; set; } = false;

        // Missing duration data on a candidate recording is NOT a disqualification --
        // only a confirmed mismatch excludes. Kept as a config bool (rather than a
        // hardcoded skip) so this can be tightened later if real-data analysis shows
        // sparse MB entries are a bigger source of false positives than false
        // negatives, without a code change.
        public bool ExcludeRecordingsWithMissingDuration { get; set; } = false;

        // The TrackDuration rung (recording:"TITLE" AND qdur:[..]) narrows a bare
        // title search using MusicBrainz's own quantized-duration index field, for
        // when both the album and artist strings have already failed as narrowing
        // fields. This is a bucket-COUNT tolerance (how many qdur buckets either
        // side of the observed track's own bucket to include), not the bucket
        // WIDTH itself -- the bucket width isn't ours to set; see
        // MusicBrainzQueryBuilder.AssumedMbQdurBucketSeconds for why that's a code
        // constant, not a config value here.
        public int QdurToleranceBuckets { get; set; } = 2;

        // Minimum lead (in recording count) the top-ranked artist must hold over
        // the second-place artist, within a TrackDuration rung's title+qdur result
        // set, before that frequency ranking is trusted as a real signal. Without
        // this floor, an obscure/rarely-covered title could produce a "leader" of
        // just 1 recording -- not actually informative, just whoever happened to
        // show up. Unvalidated placeholder, not yet tuned against real data.
        public int TrackDurationMinArtistLead { get; set; } = 2;

        // Sampling budget per bucket -- a ceiling, not a target. The sampler
        // stops as soon as confidence crosses a bound, which may happen well
        // before a bucket's ceiling is reached.
        public Dictionary<string, int> BucketCeiling { get; set; } = new()
        {
            ["AlbumArtist"] = 3,
            ["Artist"] = 4,
            ["Composer"] = 6,
        };

        // Multiplier applied to per-observation evidence based on which bucket
        // it came from. Starting neutral (1.0 everywhere) -- tune once there's
        // real output to look at, per-bucket, rather than guessing up front.
        public Dictionary<string, double> RoleWeights { get; set; } = new()
        {
            ["AlbumArtist"] = 1.0,
            ["Artist"] = 1.0,
            ["Composer"] = 1.0,
        };

        // One entry per evidence type. Raw evidence -> LLR lookup happens here,
        // never baked into the EvidenceRecord itself, so a re-score against
        // updated weights is always possible from the stored evidence alone.
        public Dictionary<string, double> EvidenceWeights { get; set; } = new()
        {
            ["CorroborationTier.Tier1"] = 3.5,
            ["CorroborationTier.Tier2"] = 1.8,
            ["CorroborationTier.Tier3"] = 0.5,
        };

        IReadOnlyDictionary<string, double> IScoringConfig.EvidenceWeights => EvidenceWeights;
        IReadOnlyDictionary<string, int> IScoringConfig.BucketCeiling => BucketCeiling;
    }
}
