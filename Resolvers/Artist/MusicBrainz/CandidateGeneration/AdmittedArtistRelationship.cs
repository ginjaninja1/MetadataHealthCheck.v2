using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration
{
    // One artist-relationship admitted onto a candidate during generation.
    // Classification exists only so the identity-fold pass can filter to
    // same-identity relations -- Candidate.RelationshipMbids collapses every
    // admitted relationship into one equal-weight list regardless of Classification.
    internal class AdmittedArtistRelationship
    {
        public string Name { get; set; } = "";
        public string Mbid { get; set; } = "";
        public ArtistRelationshipClassification Classification { get; set; }
    }
}
