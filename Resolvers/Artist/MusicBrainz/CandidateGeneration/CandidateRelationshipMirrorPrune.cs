using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    /// <summary>
    /// A candidate's RelationshipMbids can point at another MBID that is itself
    /// a live admitted candidate in this same resolution (e.g. a person and the
    /// band they're a member of, both admitted separately since
    /// CandidateIdentityFoldPass correctly does not fold GroupMembership
    /// links). Left unpruned, that mirrored MBID lets the group candidate
    /// piggyback on the same recording-relationship evidence as the person,
    /// producing a false tie. Once a related MBID is confirmed to be a live
    /// candidate here, it stops being useful as a corroboration proxy for
    /// recording-side evidence and is dropped from RelationshipMbids.
    ///
    /// GroupMembershipMbids (used by ComposerBucketCandidateFilter) is a
    /// separate field and is untouched by this pass -- that consumer needs the
    /// group-to-person link to survive even when the person is a live candidate.
    ///
    /// Unlike CandidateIdentityFoldPass, this does not merge two hypotheses
    /// into one -- the candidates remain genuinely independent competitors,
    /// this only stops one relationship fact from scoring as evidence for both.
    ///
    /// Runs on ArtistCandidateStrategy's local attributesByCandidate list,
    /// before it's attached to any surviving Candidate via
    /// ArtistCandidateAttributeSet -- Candidate itself carries no MusicBrainz-
    /// specific fields.
    /// </summary>
    internal class CandidateRelationshipMirrorPrune
    {
        private readonly StructuredLogger? _logger;

        public CandidateRelationshipMirrorPrune(StructuredLogger? logger) => _logger = logger;

        public List<string>[] Apply(
            IReadOnlyList<Candidate> candidates,
            IReadOnlyList<ArtistCandidateAttributeSet.Attributes> attributesByCandidate,
            bool[] folded,
            IReadOnlyList<IReadOnlyList<AdmittedArtistRelationship>> relationshipsByCandidate)
        {
            var liveCandidateIds = new HashSet<string>(candidates.Where((c, idx) => !folded[idx]).Select(c => c.TargetId));
            var prunedNamesByCandidate = new List<string>[candidates.Count];

            for (int i = 0; i < candidates.Count; i++)
            {
                prunedNamesByCandidate[i] = new List<string>();
                if (folded[i]) continue;

                var candidate = candidates[i];
                var attributes = attributesByCandidate[i];
                var mirrored = attributes.RelationshipMbids.Where(m => liveCandidateIds.Contains(m)).ToList();
                if (mirrored.Count == 0) continue;

                attributes.RelationshipMbids = attributes.RelationshipMbids.Where(m => !liveCandidateIds.Contains(m)).ToList();
                foreach (var m in mirrored)
                {
                    var mirroredName = relationshipsByCandidate[i].FirstOrDefault(r => r.Mbid == m)?.Name ?? m;
                    prunedNamesByCandidate[i].Add(mirroredName);
                    _logger?.Info("ArtistCandidateGen",
                        "  [{0}] \"{1}\" -- pruned mirrored relationship MBID [{2}] \"{3}\" from RelationshipMbids: that MBID is itself a live candidate in this resolution, so it can't corroborate recording evidence without double-counting.",
                        candidate.TargetId, candidate.Name, m, mirroredName);
                }
            }
            return prunedNamesByCandidate;
        }
    }
}