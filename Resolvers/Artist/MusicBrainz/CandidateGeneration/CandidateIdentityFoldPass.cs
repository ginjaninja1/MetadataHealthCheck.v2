using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    /// <summary>
    /// Drops the lower-tier candidate of any pair linked by an "is person"
    /// relationship -- MusicBrainz says they're the same real-world identity,
    /// not two competing hypotheses. Without this, both independently
    /// accumulate identical recording-relationship evidence, producing a false
    /// LLR tie that can suppress a correct auto-accept. Equal-tier pairs are
    /// left alone -- neither survives on stronger grounds than the other.
    ///
    /// A candidate identity fold is a probationary rule, not yet validated
    /// against real volume: any fold recorded here sets a ForcedReviewSignal
    /// on the context (Core's generic mechanism -- see that type), which
    /// ResolutionEngine picks up and forces the run's final decision to
    /// needs_review regardless of what the LLR/margin math would otherwise
    /// produce.
    /// </summary>
    internal class CandidateIdentityFoldPass
    {
        private readonly StructuredLogger? _logger;

        public CandidateIdentityFoldPass(StructuredLogger? logger) => _logger = logger;

        public bool[] Apply(
            IReadOnlyList<Candidate> candidates,
            IReadOnlyList<ArtistMatchTier> tiers,
            IReadOnlyList<IReadOnlyList<string>> identityRelationshipMbidsByCandidate,
            ResolutionContext context)
        {
            var folded = new bool[candidates.Count];
            var foldNotes = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (folded[i]) continue;
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    if (folded[j]) continue;

                    var linked = identityRelationshipMbidsByCandidate[i].Contains(candidates[j].TargetId)
                              || identityRelationshipMbidsByCandidate[j].Contains(candidates[i].TargetId);
                    if (!linked) continue;

                    var tierI = tiers[i];
                    var tierJ = tiers[j];

                    if (tierI == tierJ)
                    {
                        _logger?.Info("ArtistCandidateGen",
                            "  [{0}] \"{1}\" (tier={2}) and [{3}] \"{4}\" (tier={5}) are linked via an is-person relationship, but tiers are equal -- NOT folding, both remain independent candidates.",
                            candidates[i].TargetId, candidates[i].Name, tierI,
                            candidates[j].TargetId, candidates[j].Name, tierJ);
                        continue;
                    }

                    // Lower enum value = higher tier (Name=0 beats Alias=1 beats Neither=2).
                    int survivorIdx = tierI < tierJ ? i : j;
                    int droppedIdx = tierI < tierJ ? j : i;
                    folded[droppedIdx] = true;

                    var note = string.Format(
                        "Folded [{0}] \"{1}\" (tier={2}) into [{3}] \"{4}\" (tier={5}) -- linked via is-person relationship, same real-world identity.",
                        candidates[droppedIdx].TargetId, candidates[droppedIdx].Name, tiers[droppedIdx],
                        candidates[survivorIdx].TargetId, candidates[survivorIdx].Name, tiers[survivorIdx]);
                    _logger?.Info("ArtistCandidateGen", "  {0}", note);
                    foldNotes.Add(note);

                    if (droppedIdx == i) break; // i itself was dropped -- stop comparing it against later j's
                }
            }

            if (foldNotes.Count > 0)
                context.SetExtension(new ForcedReviewSignal("forced_needs_review_candidate_fold", foldNotes));

            return folded;
        }
    }
}