namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model
{
    /// <summary>
    /// An artist-to-artist relationship (e.g. "is person" linking a stage name
    /// to a real-person identity). No Direction field: confirmed against a real
    /// two-artist round trip that the same relationship type-id appears from
    /// either artist's own artist-rels fetch, just with "direction" flipped
    /// depending on which side was queried -- direction carries no
    /// extraction-relevant information, so callers should just treat ArtistMbid
    /// as "the other artist in this relation", full stop.
    /// </summary>
    public class MbArtistRelationship
    {
        public string ArtistMbid { get; set; } = "";       // the OTHER artist in the relation
        public string ArtistName { get; set; } = "";       // for logging only
        public string RelationshipType { get; set; } = ""; // e.g. "is person"
        public string RelationshipTypeId { get; set; } = ""; // MB's stable type-id GUID
    }
}
