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
        public int ArtistCandidateMinScore { get; set; } = 67;

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
        public double AutoAcceptThreshold { get; set; } = 4.0;
        public double AutoRejectThreshold { get; set; } = -3.0;
        public double MinMarginOverRunnerUp { get; set; } = 2.0;
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
        // now a two-stage mechanism (admission gate + corroboration multiplier, §5.3),
        // not a scored fact. NOT removed here yet: NameDistanceEvidenceCollector is
        // still wired into the plugin's static EvidenceCollectors list producing these
        // exact evidence types, which is itself a real, open contradiction with the
        // spec -- left alone deliberately (flagged, not silently fixed) pending an
        // explicit decision on retiring that collector's scored-evidence role.
        public Dictionary<string, double> EvidenceWeights { get; set; } = new()
        {
            // Tier 0 (§6.1) -- the track's own asserted MusicBrainz id, via
            // ProviderIdEvidenceCollector.cs. Added 2026-07-15; previously spec'd,
            // never built (no collector existed to produce this evidence type at all).
            ["ProviderIds.Confirmed"] = 5.0,
            ["NameSimilarity.NearExact"] = 1.5,
            ["NameSimilarity.Close"] = 0.5,
            ["NameSimilarity.Poor"] = -2.0,
            ["WorkRelationship.Writer"] = 2.5,
            ["WorkRelationship.Composer"] = 2.5,
            ["WorkRelationship.Lyricist"] = 2.5,
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
            ["RecordingRelationship.Producer"] = 0.8,
            ["RecordingRelationship.Arranger"] = 0.5,
        };
    }
}