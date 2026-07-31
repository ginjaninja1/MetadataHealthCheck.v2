using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client
{
    /// <summary>
    /// Abstraction over MusicBrainz's search/lookup API for the Artist resolver.
    /// </summary>
    public interface IMusicBrainzApiClient
    {
        IReadOnlyList<MbArtistResult> SearchArtist(string name);

        // The query actually used by the most recent SearchArtist call -- the
        // quoted artist/alias primary query, or the unquoted artist-only
        // fallback rung if the primary found nothing. Null before SearchArtist
        // has ever been called.
        string? LastSearchArtistQueryUsed { get; }
        IReadOnlyList<MbAlbumTitle> GetReleaseGroupTitles(string artistMbid);

        IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle, IEnumerable<string>? artistNames = null);

        // The TrackDuration rung: title + MusicBrainz's own quantized-duration
        // index field (qdur), used only once both album and artist have
        // already failed as narrowing fields. limit=100 (not the usual 25) is
        // deliberate -- this rung's entire value is counting occurrences
        // across the full narrowed result set, not a partial page.
        IReadOnlyList<MbRecordingResult> SearchRecordingByTitleAndDuration(string trackTitle, int observedDurationMs, int qdurToleranceBuckets);

        // Exposes the same query-construction logic SearchRecording/
        // SearchRecordingByTitleAndDuration use internally, purely so a cache
        // layer sitting above this client (RecordingLookup's per-rung caches)
        // can log the URL a cache hit avoided calling. Neither method makes an HTTP call.
        string DescribeSearchRecordingUrl(string trackTitle, string? albumTitle, IEnumerable<string>? artistNames = null);
        string DescribeSearchRecordingByTitleAndDurationUrl(string trackTitle, int observedDurationMs, int qdurToleranceBuckets);

        // Yields both work-level and recording-level relations in one call
        // (RelationshipLevel discriminates).
        IReadOnlyList<MbRelationship> GetRelationships(string recordingId);

        string GetArtistDisplayName(string artistMbid);

        // A candidate's registered aliases (by MBID), for determining
        // MatchedViaAlias on a recording hit without re-issuing the original
        // SearchArtist(name) call that produced this candidate in the first place.
        IReadOnlyList<string> GetArtistAliases(string artistMbid);

        // Artist-to-artist relationships (e.g. "is person"), used by the
        // candidate generation strategy to populate Candidate.RelationshipMbids.
        // Distinct from GetRelationships, which is scoped to a recording.
        IReadOnlyList<MbArtistRelationship> GetArtistRelationships(string artistMbid);
    }
}
