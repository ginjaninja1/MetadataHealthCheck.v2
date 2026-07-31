namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// Which rung of the fallback ladder produced a trustworthy hit for a
    /// given (candidate, track) lookup, most specific first:
    /// title+artist+album, title+artist, title+album, title+duration
    /// (MusicBrainz's own qdur field, tried once both artist and album have
    /// failed as narrowing fields, ordered by artist-recording-frequency
    /// rather than richness), title alone.
    /// </summary>
    public enum RecordingLookupRung
    {
        NotFound = 0,
        TrackArtistAlbum = 1,
        TrackArtist = 2,
        TrackAlbum = 3,
        TrackDuration = 4,
        TrackOnly = 5,
    }
}
