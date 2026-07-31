namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model
{
    public class MbAlbumTitle
    {
        public string Title { get; set; } = "";

        // False for "Greatest Hits"/self-titled-style generic titles.
        public bool IsDistinctive { get; set; } = true;
    }
}
