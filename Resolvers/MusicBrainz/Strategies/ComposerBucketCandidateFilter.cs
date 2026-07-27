using System;
using System.Collections.Generic;
using System.Linq;
using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies
{
    /// <summary>
    /// Added 2026-07-27. Pathway-local fold rule for the Composer bucket only (see
    /// IBucketCandidateFilter's own doc comment for why this is separate from
    /// ArtistStrategy's candidate-generation-time fold pass).
    ///
    /// Problem this solves: MusicBrainz's own work data frequently credits BOTH a
    /// band and one of its members as "writer" on the same recording (confirmed
    /// against a real case: Alex Chilton / Big Star, "Watch the Sunrise" -- both got
    /// an equal-weight Tier1 writer(Work) hit, tying their LLR exactly and blocking
    /// auto-accept via ThresholdDecisionGate's margin check, even though Chilton is
    /// clearly the more specific, correct answer). Unlike ArtistStrategy's Identity
    /// fold (same real-world person under two MBIDs), a band and its member are two
    /// genuinely different real-world entities -- so this does NOT drop the Group
    /// candidate from the resolution outright. It only stops the Group candidate from
    /// collecting NEW composer-bucket evidence once a live, linked Person candidate is
    /// also in play, so that Person's composer evidence can build an unopposed margin.
    ///
    /// Deliberately NOT applied to Artist/AlbumArtist buckets: a Group is frequently
    /// the CORRECT answer there on its own credit, and if it is, that bucket resolves
    /// before Composer's turn even comes up (SequentialSampler stops the moment any
    /// bucket crosses a decision threshold) -- so a Group only ever reaches this filter
    /// after failing to win on its own performer credit already.
    /// </summary>
    public class ComposerBucketCandidateFilter : IBucketCandidateFilter
    {
        private const string ComposerBucketKey = "Composer";
        private const string GroupType = "Group";
        private const string PersonType = "Person";

        private readonly StructuredLogger? _logger;

        public ComposerBucketCandidateFilter(StructuredLogger? logger = null)
        {
            _logger = logger;
        }

        public IReadOnlyList<Candidate> Filter(string bucketKey, IReadOnlyList<Candidate> liveCandidates, ResolutionContext context)
        {
            if (!string.Equals(bucketKey, ComposerBucketKey, StringComparison.OrdinalIgnoreCase))
                return liveCandidates; // every other bucket: no filtering, unchanged behavior

            var result = new List<Candidate>(liveCandidates.Count);
            foreach (var candidate in liveCandidates)
            {
                if (!string.Equals(candidate.Type, GroupType, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(candidate); // not a Group -- never filtered by this rule
                    continue;
                }

                var linkedPerson = liveCandidates.FirstOrDefault(other =>
                    !ReferenceEquals(other, candidate) &&
                    string.Equals(other.Type, PersonType, StringComparison.OrdinalIgnoreCase) &&
                    candidate.GroupMembershipMbids.Contains(other.TargetId));

                if (linkedPerson == null)
                {
                    result.Add(candidate); // Group with no live linked Person -- nothing to fold into, keep it
                    continue;
                }

                _logger?.Debug("ComposerFold",
                    "  [{0}] \"{1}\" (Group) folded out of the Composer bucket -- linked Person candidate [{2}] \"{3}\" is live and stands for this pair's composer evidence instead.",
                    candidate.TargetId, candidate.Name, linkedPerson.TargetId, linkedPerson.Name);
                // Deliberately omitted from `result` -- not added with Contributing=false
                // evidence, just excluded from this bucket's collection loop entirely (see
                // IBucketCandidateFilter's own doc comment on why that's cleaner than
                // per-evidence-record suppression).
            }
            return result;
        }
    }
}