using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence;
using MetadataHealthCheck.v2.Sources.Emby;
using System.Linq;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies
{
    /// <summary>
    /// RENAMED 2026-07-17 from SoftBucketStrategy -> ArtistStrategy: this is the only
    /// artist candidate-generation strategy actually wired into the pipeline (Strategy
    /// A/AnchoredRecordingStrategy is parked; Strategy C never built).
    ///
    /// REWRITTEN 2026-07-18 per direct instruction, replacing the edit-distance
    /// admission gate:
    ///
    ///   1. SearchArtist(source.DisplayName) now queries MusicBrainz with
    ///      (artist:"NAME" OR alias:"NAME") (see HttpMusicBrainzApiClient) -- alias
    ///      hits are found by MB's own search, not just carried inline on
    ///      name-matched results.
    ///   2. Admission gate is MB's own text-relevance Score alone
    ///      (ScoringConfig.CandidateGeneration.ArtistCandidateMinScore). Per Nick's
    ///      direction 2026-07-18: "we don't need closeness to achieve [finding the
    ///      candidate artist id signal]... the cleverness is in finding the candidate
    ///      artist id signal in the recorded artist id" -- i.e. admitting some extra
    ///      candidates is fine as long as it doesn't cost extra API lookups, since a
    ///      wrong candidate simply accumulates no corroborating evidence downstream
    ///      and gets rejected by the normal decision gate.
    ///   3. ArtistCandidateMaxEditDistance / IsNameOrAliasWithinEditDistance are NOT
    ///      removed -- per explicit instruction, the value in this function shouldn't
    ///      be lost in case it's wanted again later. It's repurposed here from an
    ///      admission gate into a SORT TIER classifier (see ClassifyMatchTier below):
    ///      still pure string comparison, same normalization, same Levenshtein
    ///      distance -- just informing order, not admit/reject.
    ///   4. For every admitted candidate, artist-rels are fetched EAGERLY (Nick's
    ///      confirmed directive: "I think we request artist-rels for all candidates
    ///      above 67" -- i.e. above the score cutoff, not lazily per sampler-order),
    ///      filtered to ScoringConfig.CandidateGeneration.ValidArtistRelationshipTypeIds
    ///      ("is person" only, seeded 2026-07-18), and the other artist's MBID is
    ///      collected into Candidate.RelationshipMbids.
    ///   5. Candidates are sorted tier-first (name-match tier before alias-match tier,
    ///      "neither" tier last), MB Score descending within a tier, before being
    ///      yielded -- so the sequential sampler's early-stopping sees the most likely
    ///      candidate first. Sort-order question of "can one candidate's strong alias
    ///      match ever outrank another's weaker name match" is deliberately NOT
    ///      answered here (flagged, not decided): tier-first is the safer default
    ///      pending real resolution volume to test it against.
    ///
    /// RESOLVED 2026-07-18: ArtistCandidateMinScore's default is now 67 (Nick's
    /// confirmed preferred threshold), set in ScoringConfig.cs. The score-only
    /// admission mechanism below simply uses whatever this is configured to.
    ///
    /// Strategy B (§5.3): used when no own anchor (Strategy A) and, in later
    /// phases, no borrowed anchor (Strategy C) is available.
    ///
    /// ADDED (candidate identity fold, on probation): when two admitted candidates
    /// are linked by an "is person"/"performs as" relationship (the same
    /// relationship data already fetched in step 4 above), they represent the
    /// same real-world identity, not two competing hypotheses -- MusicBrainz
    /// itself says so. Without folding, both independently accumulate the exact
    /// same recording-relationship evidence (since each candidate's own
    /// RelationshipMbids match-set includes the other), producing a false
    /// LLR=LLR tie between "two candidates" that are actually one person,
    /// which can suppress a correct auto-accept by making the real margin look
    /// like zero.
    ///
    /// Fold rule (settled after ruling out several alternatives -- see
    /// EvidenceLog.md for the general principle this produced): the candidate
    /// whose own name/alias tier (Name > Alias > Neither -- the same
    /// ClassifyMatchTier already computed for sort order) is higher survives;
    /// the other is dropped from the candidate list entirely (no data merge
    /// needed -- the survivor's RelationshipMbids already contains the dropped
    /// candidate's MBID, which is what made the fold decision possible in the
    /// first place). Equal tiers -> do NOT fold; both candidates are left
    /// independent, exactly as before this change. Explicitly NOT used as a
    /// signal: the relationship's own descriptive name field -- for "is person"
    /// relationships that field is definitionally derived from one side of the
    /// link, so it can appear to match the query from BOTH directions and can't
    /// discriminate anything (confirmed against a real case before ruling it
    /// out, not assumed).
    ///
    /// Because this rule is new and not yet validated against volume, any
    /// actual fold forces the final decision to needs_review regardless of what
    /// the LLR/margin math would otherwise produce -- see ResolutionEngine.cs,
    /// which reads ResolutionContext.CandidateFoldOccurred after the decision
    /// gate runs. This override is meant to be temporary, pending enough
    /// reviewed real cases to trust the rule -- revisit once that's true.
    /// </summary>
    public class ArtistStrategy : ICandidateGenerationStrategy<EmbyArtist>
    {
        private enum MatchTier
        {
            Name = 0,     // normalized source name exactly matches candidate's normalized primary name
            Alias = 1,    // normalized source name exactly matches one of candidate's normalized aliases
            Neither = 2,  // admitted on MB score alone; no exact name/alias match found
        }

        private readonly IMusicBrainzApiClient _client;
        private readonly ScoringConfig _config;
        private readonly MetadataHealthCheck.v2.Diagnostics.StructuredLogger? _logger;

        // logger is optional (nullable), 2026-07-16 -- added after this class was
        // first built; existing/future callers that don't have a logger handy
        // shouldn't be forced to supply one just to keep compiling.
        public ArtistStrategy(IMusicBrainzApiClient client, ScoringConfig config, MetadataHealthCheck.v2.Diagnostics.StructuredLogger? logger = null)
        {
            _client = client;
            _config = config;
            _logger = logger;
        }

        public string StrategyName => "ArtistStrategy";
        public int Priority => 30; // tried after A (and, later, C) — §5.3

        public IEnumerable<Candidate> GenerateCandidates(EmbyArtist source, ResolutionContext context)
        {
            var artistResults = _client.SearchArtist(source.DisplayName);
            var cgConfig = _config.CandidateGeneration;
            var normalizedSource = ArtistNameNormalizer.Normalize(source.DisplayName, cgConfig.NameNormalizationRules);

            _logger?.Info("ArtistCandidateGen", "[{0}] Filtering {1} artist search result(s) by MB score (>= {2})...", source.DisplayName, artistResults.Count, cgConfig.ArtistCandidateMinScore);

            var admitted = new List<(MbArtistResult Result, MatchTier Tier)>();
            var seen = new HashSet<string>();
            foreach (var result in artistResults)
            {
                if (!seen.Add(result.Mbid))
                {
                    _logger?.Debug("ArtistCandidateGen", "  [{0}] \"{1}\" -- DUPLICATE of an already-seen result, skipped.", result.Mbid, result.Name);
                    continue;
                }

                if (result.Score < cgConfig.ArtistCandidateMinScore)
                {
                    _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} -- DROPPED: below ArtistCandidateMinScore ({3}).", result.Mbid, result.Name, result.Score, cgConfig.ArtistCandidateMinScore);
                    continue; // MB's own text-relevance score too low to consider, §5.4
                }

                var tier = ClassifyMatchTier(normalizedSource, result, cgConfig);
                _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} tier={3} -- ADMITTED as a candidate.", result.Mbid, result.Name, result.Score, tier);
                admitted.Add((result, tier));
            }

            // Tier-first, MB score descending within a tier -- so the sampler's
            // early-stopping sees the most likely candidate first (§ sort-order
            // directive, 2026-07-18).
            var ordered = admitted
                .OrderBy(a => a.Tier)
                .ThenByDescending(a => a.Result.Score)
                .ToList();

            _logger?.Info("ArtistCandidateGen", "[{0}] Complete: {1} of {2} search result(s) admitted as candidates.", source.DisplayName, ordered.Count, artistResults.Count);

            // Materialized eagerly (rather than fetched lazily inside the yield loop
            // below) purely so a consolidated summary can be logged once, after every
            // candidate's relationships are known, before any candidate is handed to
            // the sampler.
            var finalCandidates = new List<Candidate>();
            var relationshipNamesByCandidate = new List<IReadOnlyList<(string Name, string Mbid, ArtistRelationshipClassification Classification)>>();
            // Identity-only projection, indexed the same as finalCandidates/ordered --
            // exists solely for the fold pass below. Candidate.RelationshipMbids (built
            // just below) deliberately keeps ALL admitted classifications as one
            // equal-weight list for any onward/scoring use.
            var identityRelationshipMbidsByCandidate = new List<IReadOnlyList<string>>();
            foreach (var (result, tier) in ordered)
            {
                var relationships = FetchValidRelationships(result.Mbid, result.Name, cgConfig);
                relationshipNamesByCandidate.Add(relationships);
                identityRelationshipMbidsByCandidate.Add(
                    relationships.Where(r => r.Classification == ArtistRelationshipClassification.Identity)
                                 .Select(r => r.Mbid)
                                 .ToList());

                finalCandidates.Add(new Candidate
                {
                    SourceEntityId = source.SourceId,
                    TargetSystem = "MusicBrainz",
                    TargetEntityType = "Artist",
                    TargetId = result.Mbid,
                    Name = result.Name,
                    GenerationStrategy = StrategyName,
                    GenerationQuery = $"(artist:\"{source.DisplayName}\" OR alias:\"{source.DisplayName}\")",
                    CreatedAt = DateTime.UtcNow,
                    RelationshipMbids = relationships.Select(r => r.Mbid).ToList(),
                });
            }

            // FOLD PASS (see class doc comment for full rationale): drop the
            // lower-tier candidate of any pair linked via an "is person"/
            // "performs as" relationship -- they're the same real-world
            // identity, not two competitors. Equal-tier pairs are left alone.
            var folded = new bool[finalCandidates.Count];
            for (int i = 0; i < finalCandidates.Count; i++)
            {
                if (folded[i]) continue;
                for (int j = i + 1; j < finalCandidates.Count; j++)
                {
                    if (folded[j]) continue;

                    var linked = identityRelationshipMbidsByCandidate[i].Contains(finalCandidates[j].TargetId)
                              || identityRelationshipMbidsByCandidate[j].Contains(finalCandidates[i].TargetId);
                    if (!linked) continue;

                    var tierI = ordered[i].Tier;
                    var tierJ = ordered[j].Tier;

                    if (tierI == tierJ)
                    {
                        _logger?.Info("ArtistCandidateGen",
                            "  [{0}] \"{1}\" (tier={2}) and [{3}] \"{4}\" (tier={5}) are linked via an is-person relationship, but tiers are equal -- NOT folding, both remain independent candidates.",
                            finalCandidates[i].TargetId, ordered[i].Result.Name, tierI,
                            finalCandidates[j].TargetId, ordered[j].Result.Name, tierJ);
                        continue;
                    }

                    // Lower enum value = higher tier (Name=0 beats Alias=1 beats Neither=2).
                    int survivorIdx = tierI < tierJ ? i : j;
                    int droppedIdx = tierI < tierJ ? j : i;
                    folded[droppedIdx] = true;

                    var note = string.Format(
                        "Folded [{0}] \"{1}\" (tier={2}) into [{3}] \"{4}\" (tier={5}) -- linked via is-person relationship, same real-world identity.",
                        finalCandidates[droppedIdx].TargetId, ordered[droppedIdx].Result.Name, ordered[droppedIdx].Tier,
                        finalCandidates[survivorIdx].TargetId, ordered[survivorIdx].Result.Name, ordered[survivorIdx].Tier);
                    _logger?.Info("ArtistCandidateGen", "  {0}", note);
                    context.CandidateFoldOccurred = true;
                    context.FoldNotes.Add(note);

                    if (droppedIdx == i) break; // i itself was dropped -- stop comparing it against later j's
                }
            }

            _logger?.Info("ArtistCandidateGen", "================================================================");
            _logger?.Info("ArtistCandidateGen", "Artist Candidate Summary");
            _logger?.Info("ArtistCandidateGen", "================================================================");
            for (int i = 0; i < finalCandidates.Count; i++)
            {
                var (result, _) = ordered[i];
                var aliasText = result.Aliases.Count == 0 ? "(none)" : string.Join(", ", result.Aliases);
                var relText = relationshipNamesByCandidate[i].Count == 0
                    ? "(none)"
                    : string.Join(", ", relationshipNamesByCandidate[i].Select(r => r.Name));
                var foldedSuffix = folded[i] ? "  [FOLDED -- excluded from sampling, see fold pass log above]" : "";
                _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" score={2} aliases=[{3}] relationships=[{4}]{5}",
                    result.Mbid, result.Name, result.Score, aliasText, relText, foldedSuffix);
            }
            _logger?.Info("ArtistCandidateGen", "================================================================");

            for (int i = 0; i < finalCandidates.Count; i++)
            {
                if (!folded[i])
                    yield return finalCandidates[i];
            }
        }

        // Eager fetch, per candidate, for every admitted candidate (Nick's confirmed
        // directive -- not lazy/sampler-order). Filters to
        // ValidArtistRelationshipTypes (now "is person" + "member of band", each
        // tagged with a Classification -- 2026-07-26); logs each relation's
        // admit/drop decision the same way SearchArtist's own admission gate is
        // logged above. Returns name, MBID, and Classification per admitted
        // relation -- Classification exists ONLY so the fold pass below can filter
        // to same-identity relations; Candidate.RelationshipMbids (built by the
        // caller) still collapses all admitted types into one equal-weight list.
        private IReadOnlyList<(string Name, string Mbid, ArtistRelationshipClassification Classification)> FetchValidRelationships(string candidateMbid, string candidateName, CandidateGenerationConfig cgConfig)
        {
            var relations = _client.GetArtistRelationships(candidateMbid);
            if (relations.Count == 0)
                return Array.Empty<(string, string, ArtistRelationshipClassification)>();

            _logger?.Info("ArtistCandidateGen", "  [{0}] \"{1}\" -- fetching artist-rels...", candidateMbid, candidateName);

            var validTypes = cgConfig.ValidArtistRelationshipTypes
                .GroupBy(t => t.TypeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Classification, StringComparer.OrdinalIgnoreCase);

            var admitted = new List<(string Name, string Mbid, ArtistRelationshipClassification Classification)>();
            foreach (var rel in relations)
            {
                if (validTypes.TryGetValue(rel.RelationshipTypeId, out var classification))
                {
                    _logger?.Info("ArtistCandidateGen", "    relation type=\"{0}\" -> \"{1}\" [{2}] -- ADMITTED as {3}.", rel.RelationshipType, rel.ArtistName, rel.ArtistMbid, classification);
                    admitted.Add((rel.ArtistName, rel.ArtistMbid, classification));
                }
                else
                {
                    _logger?.Info("ArtistCandidateGen", "    relation type=\"{0}\" -> \"{1}\" [{2}] -- DROPPED: not a valid relationship type-id.", rel.RelationshipType, rel.ArtistName, rel.ArtistMbid);
                }
            }
            return admitted;
        }

        // REPURPOSED 2026-07-18 from an admission gate into a sort-tier classifier
        // (see class doc comment). Same normalization/Levenshtein machinery as
        // before -- IsNameOrAliasWithinEditDistance's logic lives on here, just
        // answering a different question ("which tier" rather than "admit or not").
        private static MatchTier ClassifyMatchTier(string normalizedSource, MbArtistResult result, CandidateGenerationConfig config)
        {
            var normalizedName = ArtistNameNormalizer.Normalize(result.Name, config.NameNormalizationRules);
            if (NameDistanceEvidenceCollector.Levenshtein(normalizedSource, normalizedName) == 0)
                return MatchTier.Name;

            foreach (var alias in result.Aliases)
            {
                var normalizedAlias = ArtistNameNormalizer.Normalize(alias, config.NameNormalizationRules);
                if (NameDistanceEvidenceCollector.Levenshtein(normalizedSource, normalizedAlias) == 0)
                    return MatchTier.Alias;
            }

            return MatchTier.Neither;
        }

        // KEPT, NOT REMOVED, per explicit instruction 2026-07-18 ("the value in the
        // name closeness function wont be lost if we ever want to call upon it
        // later") -- no longer called anywhere in this class now that admission is
        // score-only, but preserved here rather than deleted in case closeness-based
        // admission or tiering is wanted again.
        private static bool IsNameOrAliasWithinEditDistance(string normalizedSource, MbArtistResult result, CandidateGenerationConfig config)
        {
            var normalizedName = ArtistNameNormalizer.Normalize(result.Name, config.NameNormalizationRules);
            if (NameDistanceEvidenceCollector.Levenshtein(normalizedSource, normalizedName) <= config.ArtistCandidateMaxEditDistance)
                return true;

            foreach (var alias in result.Aliases)
            {
                var normalizedAlias = ArtistNameNormalizer.Normalize(alias, config.NameNormalizationRules);
                if (NameDistanceEvidenceCollector.Levenshtein(normalizedSource, normalizedAlias) <= config.ArtistCandidateMaxEditDistance)
                    return true;
            }

            return false;
        }
    }
}