namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// One credited name on a track -- an artist, album artist, or composer
    /// entry from Emby's own per-track credit lists. Mbid is an optional,
    /// already-known MusicBrainz artist id for this credited name; it is not
    /// used by any collector yet, but is carried as a known fact for future use.
    /// </summary>
    public class EmbyCreditedName
    {
        public string Name { get; set; } = "";
        public string? Mbid { get; set; }
    }
}
