namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model
{
    public class MbRelationship
    {
        public string RelationshipType { get; set; } = "";   // writer|composer|lyricist|librettist|producer|arranger|...
        public string ArtistMbid { get; set; } = "";
        public RelationshipLevel Level { get; set; }
    }
}
