namespace MetadataHealthCheck.v2.Core.Model
{
    // One replacement/allowance rule in the Stage 1 admission-gate name normalization
    // table (§5.3/§10.3). Pattern is a case-insensitive regex; Replacement is applied
    // via Regex.Replace. Kept as data (not hardcoded logic) so it's genuinely the
    // "editable replacement/allowance table" §10.3 calls for. Diacritics folding is
    // NOT one of these rules -- it's a fixed Unicode-normalization step applied before
    // the rule list runs (ArtistNameNormalizer.cs), since it isn't meaningfully
    // expressible as a small edited table the way "strip leading The" is.
    public class NameNormalizationRule
    {
        public string Pattern { get; set; } = "";
        public string Replacement { get; set; } = "";
    }

    // §5.3 Stage 1 (admission gate) + Stage 2 (per-recording corroboration) config.
    // Nested under ScoringConfig.CandidateGeneration per §10.3's own tree, not flat --
    // corrected 2026-07-13 (an earlier commit added ArtistCandidateMinScore directly
    // on ScoringConfig, which didn't match the spec's actual structure).
    public class CandidateGenerationConfig
    {
        // MusicBrainz's own C1 text-relevance score (0-100), a distinct, separate gate
        // from edit-distance below (§5.3). Default 0 -- inert until real resolution
        // volume exists to calibrate against (spec explicitly asserts no default is
        // known to be correct yet); the gate logic itself is real, not stubbed.
        // 2026-07-18: default changed from 0 to 67 per Nick's confirmed preferred
        // threshold for the (artist:"NAME" OR alias:"NAME") query -- MB's alias hits
        // score inherently lower than direct name hits, and 67 is where genuine
        // matches were observed to separate from noise.
        public int ArtistCandidateMinScore { get; set; } = 80;

        // Stage 1's actual admission mechanism (§5.3): raw Levenshtein edit distance
        // (not a normalized 0-1 ratio) between normalized source name and normalized
        // candidate name-or-alias. UNVALIDATED PLACEHOLDER: the spec is explicit that
        // no default here is yet asserted as correct ("needs real-data tuning, no
        // default yet asserted as correct") -- 3 is a reasonable small-typo-tolerance
        // starting point, not a calibrated value. Revisit once real resolution volume
        // exists (§16).
        public int ArtistCandidateMaxEditDistance { get; set; } = 3;

        // Seed list per §5.3's own enumeration (strip leading "The", fold &/and/+,
        // strip apostrophes, strip feat/featuring/vs/with credit suffixes, strip
        // remaining punctuation, collapse whitespace). Case-folding and whitespace
        // collapse are applied as a fixed final step in ArtistNameNormalizer, not as
        // table entries, since they're unconditional rather than "replacement" rules.
        public List<NameNormalizationRule> NameNormalizationRules { get; set; } = new()
        {
            new NameNormalizationRule { Pattern = @"^\s*the\s+", Replacement = "" },
            new NameNormalizationRule { Pattern = @"\s*\+\s*", Replacement = " and " },
            new NameNormalizationRule { Pattern = @"\s*&\s*", Replacement = " and " },
            new NameNormalizationRule { Pattern = @"'", Replacement = "" },
            new NameNormalizationRule { Pattern = @"\s+(feat\.?|featuring|vs\.?|with)\s+.*$", Replacement = "" },
            new NameNormalizationRule { Pattern = @"[^\w\s]", Replacement = "" },
        };

        // Added 2026-07-18: which MusicBrainz artist-relationship type-ids count as
        // "this is really the same identity" for RelationshipMbids purposes. Seeded
        // with only "is person" (dd9886f2-1dfe-4270-97db-283f6839a666), confirmed via
        // a real two-artist round trip (Del Serino <-> Cirino Colacrai) to be
        // direction-agnostic. Configurable list per Nick's direction -- other MB
        // relationship types (e.g. "member of band") must NOT be added here without a
        // deliberate decision, since they mean something structurally different
        // (a person belongs to a group, not "is the same identity as").
        public List<string> ValidArtistRelationshipTypeIds { get; set; } = new()
        {
            "dd9886f2-1dfe-4270-97db-283f6839a666", // "is person"
        };
    }

    /// <summary>
    /// Phase 1 subset of §10.3's ScoringConfig — only what the simple weighted-sum
    /// scorer and decision gate need. Full grid-editable version (BucketCeiling,
    /// JointEvidencePairs, role weights, etc.) arrives in Phase 5 per §21.
    /// </summary>
    public class ScoringConfig
    {
        // Lowered 2026-07-23 (settled directive, per real-world API-cost concern
        // outweighing marginal accuracy gain): a single Tier2 (track+artist, no
        // album) confirmation with no competing candidate is now sufficient on its
        // own to auto-accept and stop further sampling -- was 4.0/2.0, requiring the
        // full rung ladder (up to hundreds of MusicBrainz calls on high-collision
        // names) to be exhausted before any decision could be reached. This is a
        // deliberate, permanent lowering of the bar for every artist resolved via
        // Audio's Artist bucket, not a per-case exception.
        public double AutoAcceptThreshold { get; set; } = 1.5;
        public double AutoRejectThreshold { get; set; } = -3.0;
        public double MinMarginOverRunnerUp { get; set; } = 1.5;
        public string Version { get; set; } = "phase2-default";

        public CandidateGenerationConfig CandidateGeneration { get; set; } = new();

        // §5.3 Stage 2 / §6.3: multiplies whatever Corroboration Tier LLR (§6.1) a
        // recording-lookup hit would otherwise contribute, BEFORE role-weighting
        // (§6.2) is applied -- depending on whether the match was against the
        // candidate's primary name or only one of its registered aliases
        // (EvidenceRecord.MatchedViaAlias). Applied by SimpleWeightedSumScorer, not
        // baked into CorroborationTierEvidenceCollector's raw EvidenceRecord (§5.4).
        public double NameMatchWeight { get; set; } = 1.0;
        public double AliasMatchWeight { get; set; } = 0.9;

        // Added 2026-07-18, per settled directive on recording-lookup disambiguation.
        // A real 772-recording same-title search sample showed MusicBrainz's own
        // relevance score giving zero disambiguation power (every result scored 100).
        // Recording-level duration (free from the search response, no extra API call)
        // is used as a GATE in RecordingLookup, before any relationship-scan
        // confirmation is attempted: percentage-based rather than a flat number of
        // seconds, since the same sample showed legitimate variance even WITHIN one
        // correct recording across different releases (a single AC/DC recording's
        // length differed by ~4 seconds between two of its own release listings).
        // UNVALIDATED PLACEHOLDER, same status as ArtistCandidateMaxEditDistance above
        // -- a "suck it and see" knob, no default yet asserted as correct; revisit
        // once the 70k-artist run gives real data to tune against.
        public double DurationGateTolerancePercent { get; set; } = 0.03;

        // Settled directive (2026-07-18): missing duration data on a candidate
        // recording is NOT a disqualification -- only a CONFIRMED mismatch excludes.
        // Kept as a config bool (rather than a hardcoded skip) so this can be
        // tightened later if real-data analysis shows sparse MB entries are a bigger
        // source of false positives than false negatives, without a code change.
        public bool ExcludeRecordingsWithMissingDuration { get; set; } = false;

        // Added 2026-07-19: the TrackDuration rung (recording:"TITLE" AND qdur:[..])
        // narrows a bare title search using MusicBrainz's own quantized-duration
        // index field, for the case where BOTH the album and artist strings have
        // already failed as narrowing fields (§7.2's "Bohemian Rhapsody"/351000ms
        // trace -- 13,113 unfiltered matches down to 62 with this filter applied).
        // This is a bucket-COUNT tolerance (how many qdur buckets either side of the
        // observed track's own bucket to include), NOT the bucket WIDTH itself --
        // the bucket width is not ours to set; see AssumedMbQdurBucketSeconds in
        // HttpMusicbrainzApiClient.cs for why that's a code constant, not a config
        // value here. Default 2, confirmed by Nick against a real query.
        public int QdurToleranceBuckets { get; set; } = 2;

        // Added 2026-07-19: minimum lead (in recording count) the top-ranked artist
        // must hold over the second-place artist, within a TrackDuration rung's
        // title+qdur result set, before that frequency ranking is trusted as a real
        // signal. Without this floor, an obscure/rarely-covered title could produce
        // a "leader" of just 1 recording -- not actually informative, just whoever
        // happened to show up. UNVALIDATED PLACEHOLDER, same status as
        // DurationGateTolerancePercent above -- a starting guess, not yet tuned
        // against real data.
        public int TrackDurationMinArtistLead { get; set; } = 2;

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
        //
        // "NameSimilarity.*" entries retired 2026-07-13 per §6.1's explicit retirement
        // of name-similarity as an additive evidence type -- name/alias comparison is
        // a two-stage mechanism instead (admission gate in ArtistStrategy + the
        // MatchedViaAlias/TooPoorToTrust confirmation check in RecordingLookup), not a
        // scored fact. RESOLVED 2026-07-17: NameDistanceEvidenceCollector's Collect()
        // sets Contributing=false, so its NameSimilarity.* records are opportunistic/
        // logged-only (see its own doc comment) and SequentialSampler's Contributing
        // filter means these three weight entries were never actually consulted --
        // removed here 2026-07-19 as dead config, not a behavior change. The collector
        // itself stays wired: its Collect() output is still useful diagnostic logging,
        // and its static Levenshtein/EvaluateRecordingMatch methods remain load-bearing
        // for ArtistStrategy's admission gate and RecordingLookup's confirmation check.
        public Dictionary<string, double> EvidenceWeights { get; set; } = new()
        {
            // Tier 0 (§6.1)'s "ProviderIds.Confirmed" (the track's own asserted
            // MusicBrainz id) REMOVED 2026-07-19 along with ProviderIdEvidenceCollector
            // itself -- confirmed vestigial: with Contributing=false (set 2026-07-17,
            // same directive that neutered NameSimilarity above), nothing anywhere in
            // the codebase ever read this evidence type or short-circuited on it, so
            // it produced one diagnostic log line and had zero effect on any decision.
            // A real Tier-0 short-circuit (skip the live MB lookup when a file's own
            // tag already matches) remains a genuine, undecided product question --
            // this removal is not a decision against that, just against dead code.
            // "WorkRelationship.Writer/Composer/Lyricist" and "RecordingRelationship.
            // Producer/Arranger" REMOVED 2026-07-19: same shape as the ProviderIds
            // removal above -- each was only ever emitted by one of the three
            // collectors (WorkRelationshipEvidenceCollector, RecordingRelationship-
            // EvidenceCollector) collapsed into RecordingCorroborationEvidenceCollector
            // 2026-07-17, which reports everything as CorroborationTier.* instead
            // regardless of whether confirmation came via performer-credit or a
            // relationship scan. Confirmed dead: nothing in the codebase emits these
            // exact strings anymore. Relationship-based confirmations ARE still scored
            // -- just folded into the same CorroborationTier weight as any other
            // confirmation at that rung, not distinguished by relationship type. That
            // collapse itself is unchanged by this removal -- it was already settled
            // 2026-07-18; this is only removing the now-pointless weight entries left
            // behind by it.
            // Added 2026-07-13, per §6.1 -- previously missing entirely, which meant
            // AlbumMatchEvidenceCollector/CorroborationTierEvidenceCollector/
            // RecordingRelationshipEvidenceCollector's output would have silently
            // contributed 0 LLR (SimpleWeightedSumScorer's existing "unrecognized
            // evidence type contributes 0" fallback) even once those collectors existed.
            ["AlbumMatch.Distinctive"] = 1.5,
            ["AlbumMatch.Generic"] = 0.3,
            ["CorroborationTier.Tier1"] = 3.5,
            ["CorroborationTier.Tier2"] = 1.8,
            ["CorroborationTier.Tier3"] = 0.5,
        };
    }
}