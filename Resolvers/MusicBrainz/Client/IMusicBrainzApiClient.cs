namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client
{
    public class MbArtistResult
    {
        public string Mbid { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Disambiguation { get; set; }
        public int Score { get; set; }               // MB's own text-relevance score — pool-admission filter ONLY, §5.4

        // Added 2026-07-12 (coding checklist item 1): real ws/2/artist search responses
        // return registered aliases inline on each result, no extra inc= parameter
        // needed (confirmed against real MusicBrainz data). Needed for Stage 1's
        // admission-gate name-or-alias matching (§5.3) once SoftBucketStrategy is
        // reworked onto artist-search-first — not yet consumed by any strategy as of
        // this commit, but the model gap had to close before that work could start.
        public List<string> Aliases { get; set; } = new();
    }

    public class MbAlbumTitle
    {
        public string Title { get; set; } = "";
        public bool IsDistinctive { get; set; } = true;  // false for "Greatest Hits"/self-titled-style generic titles, §6.1
    }

    public class MbRecordingResult
    {
        public string RecordingId { get; set; } = "";
        public string ArtistMbid { get; set; } = "";
        public string TrackTitle { get; set; } = "";
        public string ReleaseTitle { get; set; } = "";
        public bool TrackTitleMatches { get; set; }
        public bool ReleaseTitleMatches { get; set; }
    }

    public class MbWorkRelationship
    {
        public string RelationshipType { get; set; } = "";   // writer|composer|lyricist|producer|arranger|...
        public string ArtistMbid { get; set; } = "";
    }

    /// <summary>
    /// Abstraction over §7.2's call catalog (C1-C6). Live implementation would hit
    /// musicbrainz.org, which is unreachable from this build sandbox (not on the
    /// allowed-domains list) — this is expected for a plugin engine and is not a
    /// build blocker, per §17.1's own fixture-based testing strategy. Only
    /// C1/C4-equivalent (artist search, recording search) and C5-equivalent (work
    /// relationships) are needed for Phase 1's two evidence types.
    /// </summary>
    public interface IMusicBrainzApiClient
    {
        IReadOnlyList<MbArtistResult> SearchArtist(string name);                                  // C1
        IReadOnlyList<MbAlbumTitle> GetReleaseGroupTitles(string artistMbid);                       // C2 (subset)

        // Extended 2026-07-12 with an artistName parameter (nullable, defaults to null so
        // pre-existing call sites don't need every caller touched at once) to support
        // RecordingLookup.cs's three-rung fallback ladder: track+artist+album -> track+album
        // -> track alone. Existing callers (AnchoredRecordingStrategy, SoftBucketStrategy)
        // still pass null for artistName for now — widening candidate-generation strategies
        // onto the ladder themselves is separate follow-up work, out of this unit's scope.
        IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle, string? artistName = null);     // C3/C4

        IReadOnlyList<MbWorkRelationship> GetWorkRelationships(string recordingId);                   // C5 (subset)

        // Phase 1 addition, not literally in §7.2's catalog: NameDistanceEvidenceCollector
        // needs the candidate's MB display name to compare against the source name. In the
        // full build this rides along on C2's artist lookup; pulled out separately here
        // since C2's other fields (aliases/tags/url-rels) aren't implemented until Phase 2.
        string GetArtistDisplayName(string artistMbid);
    }
}