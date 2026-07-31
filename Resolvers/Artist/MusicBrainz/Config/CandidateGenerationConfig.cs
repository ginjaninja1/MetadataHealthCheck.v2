namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config
{
    /// <summary>
    /// Candidate admission-gate config: which MusicBrainz artist search hits are
    /// admitted as candidates at all, and which of a candidate's artist-relationships
    /// are trusted for corroboration/identity-fold purposes.
    /// </summary>
    public class CandidateGenerationConfig
    {
        // MusicBrainz's own text-relevance score (0-100) for an artist search hit,
        // the admission gate's primary threshold. Unvalidated placeholder -- no
        // default is yet asserted as correct; revisit once real resolution volume
        // exists to tune against. MB's alias hits score inherently lower than
        // direct name hits, which is why this sits well below 100.
        public int ArtistCandidateMinScore { get; set; } = 80;

        // Raw Levenshtein edit distance (not a normalized ratio) between
        // normalized source name and normalized candidate name-or-alias. No
        // longer an admission gate (see ArtistMatchTierClassifier) -- retained
        // as a tunable in case edit-distance-based admission is wanted again.
        public int ArtistCandidateMaxEditDistance { get; set; } = 3;

        // Applied in order: strip leading "The", fold &/and/+, strip
        // apostrophes, strip feat/featuring/vs/with credit suffixes, strip
        // remaining punctuation, collapse whitespace. Case-folding and
        // whitespace collapse are a fixed final step in ArtistNameNormalizer,
        // not table entries, since they're unconditional rather than "replacement" rules.
        public List<NameNormalizationRule> NameNormalizationRules { get; set; } = new()
        {
            new NameNormalizationRule { Pattern = @"^\s*the\s+", Replacement = "" },
            new NameNormalizationRule { Pattern = @"\s*\+\s*", Replacement = " and " },
            new NameNormalizationRule { Pattern = @"\s*&\s*", Replacement = " and " },
            new NameNormalizationRule { Pattern = @"'", Replacement = "" },
            new NameNormalizationRule { Pattern = @"\s+(feat\.?|featuring|vs\.?|with)\s+.*$", Replacement = "" },
            new NameNormalizationRule { Pattern = @"[^\w\s]", Replacement = "" },
        };

        // Which MusicBrainz artist-relationship type-ids are admitted onto a
        // candidate at all (ArtistCandidateAttributeSet.Attributes.RelationshipMbids), each tagged with a
        // Classification. Every admitted type is equal weight for scoring; the
        // classification exists only so the identity-fold pass (ArtistCandidateStrategy)
        // can tell "is person" apart from "member of band" -- fold only ever acts
        // on Classification==Identity, since a person and a real person they
        // perform as are the same real-world identity, while a person and a
        // group they belong to are not.
        public List<ArtistRelationshipTypeConfig> ValidArtistRelationshipTypes { get; set; } = new()
        {
            new ArtistRelationshipTypeConfig
            {
                TypeId = "dd9886f2-1dfe-4270-97db-283f6839a666", // "is person"
                Classification = ArtistRelationshipClassification.Identity,
            },
            new ArtistRelationshipTypeConfig
            {
                TypeId = "5be4c609-9afa-4ea0-910b-12ffb71e3821", // "member of band"
                Classification = ArtistRelationshipClassification.GroupMembership,
            },
        };
    }
}