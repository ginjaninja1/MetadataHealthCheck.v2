using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    /// <summary>
    /// MusicBrainz-specific attributes for a Candidate that Core has no concept
    /// of: MusicBrainz's own artist "type" (Person/Group), the MBIDs of other
    /// artists this candidate is related to (for corroboration and identity
    /// folding), and the subset of those representing group-membership
    /// specifically (for bucket filters that need to distinguish that from
    /// identity relationships).
    ///
    /// Owned entirely by this resolver, scoped to one resolution: populated
    /// once by ArtistCandidateStrategy during GenerateCandidates, stored on
    /// the ResolutionContext for that resolution via SetExtension, and read
    /// back by ComposerBucketCandidateFilter/RecordingCorroborationEvidence-
    /// Collector via GetExtension -- all three already receive the same
    /// ResolutionContext instance for the one resolution these attributes are
    /// valid for, so there's no separate lifetime to manage: this set is
    /// discarded along with the context.
    /// </summary>
    public class ArtistCandidateAttributeSet
    {
        public class Attributes
        {
            public string Type { get; set; } = "";
            public IReadOnlyList<string> RelationshipMbids { get; set; } = Array.Empty<string>();
            public IReadOnlyList<string> GroupMembershipMbids { get; set; } = Array.Empty<string>();
        }

        private readonly Dictionary<string, Attributes> _byCandidateId = new();
        private static readonly Attributes Empty = new();

        public void Set(string candidateId, Attributes attributes) => _byCandidateId[candidateId] = attributes;

        // Defensive-empty rather than throwing: a candidate this resolver never
        // registered attributes for (shouldn't happen for any candidate that
        // survived generation) just reports no MusicBrainz-specific data.
        public Attributes Get(Candidate candidate) =>
            _byCandidateId.TryGetValue(candidate.Id, out var attrs) ? attrs : Empty;

        // Convenience for GetExtension callers: returns an empty set rather
        // than null when this resolver's strategy hasn't populated the
        // context's extension slot yet (e.g. Filter/CollectRounds called
        // before GenerateCandidates for some reason) -- callers can always
        // call Get(candidate) unconditionally rather than null-checking twice.
        public static ArtistCandidateAttributeSet GetOrEmpty(ResolutionContext context) =>
            context.GetExtension<ArtistCandidateAttributeSet>() ?? new ArtistCandidateAttributeSet();
    }
}