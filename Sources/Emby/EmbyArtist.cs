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
    /// One credited name on a track — an artist, album artist, or composer entry
    /// from Emby's own per-track credit lists. Carries its own Emby id and
    /// ProviderIds separately from the track's own ProviderIds (§5.1's Tier 0 fast
    /// path is about the TRACK's own asserted MusicBrainz id; this is about whether
    /// a given CREDITED NAME on the track already has a confirmed identity via
    /// artist_cooccurrence + identity_cache, §9 — the two are unrelated lookups
    /// that happen to both be called "ProviderIds").
    /// </summary>
    public class EmbyCreditedName
    {
        public string Name { get; set; } = "";
        public string EmbyId { get; set; } = "";
        public Dictionary<string, string> ProviderIds { get; set; } = new();
    }

    /// <summary>
    /// One track-credit observation for an artist, as surfaced by the E2 fat query.
    ///
    /// WIDENED 2026-07-12 (Project Log Directives + "Coding checklist: widen
    /// EmbyTrackCredit"): AlbumArtists/Artists/Composers/Duration added, matching
    /// §8.2's own E2 call specification (AlbumArtists, Artists, People,
    /// RunTimeTicks). Populated uniformly regardless of Role/tier — an empty
    /// Composers list on an AlbumArtist-tier track is a real fact (this track has
    /// no composer credit in Emby), not a gap to fill or suppress.
    ///
    /// Still NOT done, deliberately separate from this step (see the same coding
    /// checklist): the actual anchor lookup (joining a credited name's EmbyId
    /// against artist_cooccurrence + identity_cache, §9, to check "is this
    /// co-occurring credit already confirmed") is real logic, not just a data
    /// shape, and hasn't been wired up anywhere yet — this step only adds the
    /// data these lists need to carry to make that lookup possible later.
    /// </summary>
    public class EmbyTrackCredit
    {
        public string TrackId { get; set; } = "";
        public string TrackName { get; set; } = "";
        public string AlbumName { get; set; } = "";
        public string AlbumId { get; set; } = "";
        public string Role { get; set; } = "";                 // AlbumArtist | Artist | Composer -- this artist's own tier on this track
        public Dictionary<string, string> ProviderIds { get; set; } = new();  // this TRACK's own provider ids (Tier 0 fast path, §5.1) -- unrelated to the per-credited-name ids below

        public List<EmbyCreditedName> AlbumArtists { get; set; } = new();
        public List<EmbyCreditedName> Artists { get; set; } = new();
        public List<EmbyCreditedName> Composers { get; set; } = new();
        public TimeSpan? Duration { get; set; }
    }
}