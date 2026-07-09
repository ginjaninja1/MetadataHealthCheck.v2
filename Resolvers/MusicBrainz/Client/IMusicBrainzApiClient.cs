namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client
{
    public class MbArtistResult
    {
        public string Mbid { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Disambiguation { get; set; }
        public int Score { get; set; }               // MB's own text-relevance score — pool-admission filter ONLY, §5.4
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
        IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle);     // C3/C4
        IReadOnlyList<MbWorkRelationship> GetWorkRelationships(string recordingId);                   // C5 (subset)

        // Phase 1 addition, not literally in §7.2's catalog: NameDistanceEvidenceCollector
        // needs the candidate's MB display name to compare against the source name. In the
        // full build this rides along on C2's artist lookup; pulled out separately here
        // since C2's other fields (aliases/tags/url-rels) aren't implemented until Phase 2.
        string GetArtistDisplayName(string artistMbid);
    }
}
