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
    /// from Emby's own per-track credit lists.
    ///
    /// CHANGED 2026-07-15 (Project Log Directives): EmbyId and per-credited-name
    /// ProviderIds dropped. Confirmed by checking every evidence collector directly:
    /// nothing in the resolution pipeline reads a co-credited name's Emby id — the
    /// only entity id that matters to resolution is the SOURCE artist's own SourceId
    /// (identity cache key, §5.6) and TrackId/AlbumId (RecordingLookup's memoization
    /// key). Carrying Emby ids for OTHER people mentioned on a track has exactly one
    /// theoretical consumer — the parked cross-artist anchoring mechanism (§9
    /// anchor_dependencies) — which is not implemented and stays out of scope here.
    ///
    /// Replaced with an optional Mbid: a real, already-known MusicBrainz artist id
    /// for this credited name, if one happens to be known. This is NOT the same
    /// mechanism as anchoring either — it's just an optional fact carried on the
    /// observation, unused by any collector today, reserved for whenever anchoring
    /// (or Tier 0-adjacent uses) is un-parked. See EmbyTrackCredit.ProviderIds below
    /// for the actual, real, currently-consumed Tier 0 mechanism (the TRACK's own
    /// tagged MusicBrainz id) — the two are deliberately kept distinct.
    /// </summary>
    public class EmbyCreditedName
    {
        public string Name { get; set; } = "";
        public string? Mbid { get; set; }
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
        public Dictionary<string, string> ProviderIds { get; set; } = new();  // this TRACK's own provider ids -- §6.1's Tier 0 evidence concept. Real, would-be-consumed key: "MusicBrainzArtist" -> a confirmed MBID. No collector currently reads this (ProviderIdEvidenceCollector removed 2026-07-19, confirmed vestigial -- see ScoringConfig.cs and MusicBrainzArtistResolverPlugin.cs comments); field kept since the underlying Tier-0 concept is still a real, undecided product question, not because anything consumes it today.

        public List<EmbyCreditedName> AlbumArtists { get; set; } = new();
        public List<EmbyCreditedName> Artists { get; set; } = new();
        public List<EmbyCreditedName> Composers { get; set; } = new();
        public TimeSpan? Duration { get; set; }
    }
}