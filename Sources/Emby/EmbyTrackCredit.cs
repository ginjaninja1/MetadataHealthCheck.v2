namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// One track-credit observation for an artist. Populated uniformly
    /// regardless of Role: an empty Composers list on an AlbumArtist-tier track
    /// is a real fact (this track has no composer credit in Emby), not a gap.
    /// </summary>
    public class EmbyTrackCredit
    {
        public string TrackId { get; set; } = "";
        public string TrackName { get; set; } = "";
        public string AlbumName { get; set; } = "";
        public string AlbumId { get; set; } = "";

        // This artist's own tier on this track: AlbumArtist | Artist | Composer.
        public string Role { get; set; } = "";

        // This track's own provider ids, keyed e.g. "MusicBrainzArtist" -> a
        // confirmed MBID, if Emby already has one tagged. Not currently read by
        // any collector; kept because a tagged-id evidence source remains a
        // real, undecided design question.
        public Dictionary<string, string> ProviderIds { get; set; } = new();

        public List<EmbyCreditedName> AlbumArtists { get; set; } = new();
        public List<EmbyCreditedName> Artists { get; set; } = new();
        public List<EmbyCreditedName> Composers { get; set; } = new();
        public TimeSpan? Duration { get; set; }
    }
}
