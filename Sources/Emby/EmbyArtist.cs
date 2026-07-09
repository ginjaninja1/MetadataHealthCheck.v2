using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    public class EmbyArtist : ISourceEntity
    {
        public string SourceSystem => "Emby";
        public string EntityType => "Artist";
        public string SourceId { get; set; } = "";
        public string DisplayName { get; set; } = "";

        // Populated by EmbyLibraryReader's single E2 pass (§8.2) — the tracks
        // this artist is credited on, carrying role/album/duration/ProviderIds.
        public List<EmbyTrackCredit> Tracks { get; set; } = new();
    }

    /// <summary>
    /// One track-credit observation for an artist, as surfaced by the E2 fat query.
    /// Phase 1 only needs the fields the two Phase-1 evidence collectors and
    /// Strategy A/B use; full field set (People, RunTimeTicks, etc.) lands as
    /// later evidence collectors are implemented (§21 phase 2).
    /// </summary>
    public class EmbyTrackCredit
    {
        public string TrackId { get; set; } = "";
        public string TrackName { get; set; } = "";
        public string AlbumName { get; set; } = "";
        public string AlbumId { get; set; } = "";
        public string Role { get; set; } = "";                 // AlbumArtist | Artist | Composer
        public Dictionary<string, string> ProviderIds { get; set; } = new();
    }
}
