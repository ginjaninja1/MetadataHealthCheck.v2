namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model
{
    public class MbRecordingResult
    {
        public string RecordingId { get; set; } = "";

        // First-credited artist's MBID only. Kept only for the TrackDuration
        // frequency tally (RecordingDurationLookup), which groups by
        // first-artist-only as a known simplification -- do not use this field
        // for candidate-match confirmation; use ArtistMbids instead, since a
        // multi-artist recording credit's 2nd/3rd artist is otherwise invisible
        // to matching entirely.
        public string ArtistMbid { get; set; } = "";
        public string TrackTitle { get; set; } = "";
        public string ReleaseTitle { get; set; } = "";
        public bool TrackTitleMatches { get; set; }
        public bool ReleaseTitleMatches { get; set; }

        // The literal artist-credit text MusicBrainz returned for this recording.
        public string ArtistCreditText { get; set; } = "";

        // Recording/track-level artist-credit MBIDs for every artist on this
        // recording's credit, not just the first (ArtistMbid above). A
        // recording's artist-credit is an ordered list ("Gorillaz feat. Mos Def
        // & Bobby Womack" is 3 credited artists); use this field for confirmation matching.
        public List<string> ArtistMbids { get; set; } = new();

        // Names parallel to ArtistMbids (same index order), for debug-log
        // readability only -- confirmation matching uses ArtistMbids exclusively.
        public List<string> ArtistCreditNames { get; set; } = new();

        // MusicBrainz's own length for this recording, in milliseconds. Null
        // when MusicBrainz has no length for this recording -- missing data is
        // not a disqualification, the duration gate only excludes on a confirmed mismatch.
        public int? LengthMs { get; set; }

        // MusicBrainz's own relevance score for this recording result.
        // Diagnostic only -- does not feed corroboration-tier classification
        // (derived from which rung produced a confirmed hit) or the duration gate.
        public int Score { get; set; }

        // Richness signals only -- these inform walk order among gate-survivors
        // (which recording to spend the first relationship-fetch call scanning),
        // they do not gate or otherwise affect correctness.
        public string? ReleaseStatus { get; set; }              // "Official" | "Promotion" | "Bootleg" | null
        public string? ReleaseGroupPrimaryType { get; set; }     // "Album" | "EP" | "Single" | null
        public List<string> ReleaseGroupSecondaryTypes { get; set; } = new();  // e.g. "Live", "Compilation"
        public int ReleaseCount { get; set; }                     // distinct releases this recording appears on

        // Release-level artist credit (MusicBrainz's actual "album artist"
        // concept), distinct from ArtistMbid/ArtistCreditText above, which are
        // the recording/track-level credit. Needed so richness ranking can tell
        // a genuine Various-Artists compilation apart from an ordinary studio
        // album that merely carries the "Compilation" secondary type for other
        // MB-cataloguing reasons.
        public string? ReleaseAlbumArtistMbid { get; set; }
        public string? ReleaseAlbumArtistCreditText { get; set; }

        // Release-level artist-credit MBIDs across every release this recording
        // appears on -- not just the single representative release used above
        // for richness ranking. A recording can legitimately carry different
        // release-level artist credits on different releases (e.g. one
        // candidate credited on a compilation, a different candidate credited
        // on the original studio album of the same recording). Confirmation
        // must be able to match against any of them.
        public List<string> ReleaseAlbumArtistMbids { get; set; } = new();

        // Names parallel to ReleaseAlbumArtistMbids (same index order), for
        // debug-log readability only. Confirmation matching uses
        // ReleaseAlbumArtistMbids exclusively -- never these names.
        public List<string> ReleaseAlbumArtistNames { get; set; } = new();
    }
}
