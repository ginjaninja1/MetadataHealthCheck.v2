using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;

namespace MetadataHealthCheck.v2.Fixtures
{
    /// <summary>
    /// Real recorded-response stand-in per §17.1 — no live network calls.
    /// Reproduces §18's worked example: MBID X (correct Sarah Vaughan) and
    /// MBID Y (a different, less-famous same-named artist).
    /// </summary>
    public class FixtureMusicBrainzApiClient : IMusicBrainzApiClient
    {
        public const string MbidX = "mbid-sarah-vaughan-correct";
        public const string MbidY = "mbid-sarah-vaughan-other";

        public IReadOnlyList<MbArtistResult> SearchArtist(string name)
        {
            if (!name.Equals("Sarah Vaughan", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<MbArtistResult>();

            return new List<MbArtistResult>
            {
                new MbArtistResult { Mbid = MbidX, Name = "Sarah Vaughan", Score = 100 },
                new MbArtistResult { Mbid = MbidY, Name = "Sarah Vaughan", Disambiguation = "different, lesser-known artist", Score = 90 },
            };
        }

        public IReadOnlyList<MbAlbumTitle> GetReleaseGroupTitles(string artistMbid)
        {
            if (artistMbid == MbidX)
                return new List<MbAlbumTitle> { new MbAlbumTitle { Title = "Crazy and Mixed Up", IsDistinctive = true } };

            return new List<MbAlbumTitle>(); // Y has no overlapping albums
        }

        public IReadOnlyList<MbRecordingResult> SearchRecording(string trackTitle, string? albumTitle)
        {
            var results = new List<MbRecordingResult>
            {
                new MbRecordingResult
                {
                    RecordingId = "rec-autumn-leaves-X",
                    ArtistMbid = MbidX,
                    TrackTitle = "Autumn Leaves",
                    ReleaseTitle = "Crazy and Mixed Up",
                    TrackTitleMatches = trackTitle.Equals("Autumn Leaves", StringComparison.OrdinalIgnoreCase),
                    ReleaseTitleMatches = albumTitle != null && albumTitle.Equals("Crazy and Mixed Up", StringComparison.OrdinalIgnoreCase),
                },
                new MbRecordingResult
                {
                    RecordingId = "rec-autumn-leaves-Y",
                    ArtistMbid = MbidY,
                    TrackTitle = "Autumn Leaves (a different recording)",
                    ReleaseTitle = "Some Other Album",
                    TrackTitleMatches = false,
                    ReleaseTitleMatches = false,
                },
            };
            return results;
        }

        public IReadOnlyList<MbWorkRelationship> GetWorkRelationships(string recordingId)
        {
            if (recordingId == "rec-autumn-leaves-X")
                return new List<MbWorkRelationship> { new MbWorkRelationship { RelationshipType = "writer", ArtistMbid = MbidX } };

            return Array.Empty<MbWorkRelationship>();
        }

        public string GetArtistDisplayName(string artistMbid)
        {
            // X is an exact name match; Y is a deliberately near-but-not-exact name,
            // standing in for the "different, same-named artist" case. Note: a true
            // byte-for-byte same-name collision (as in §18's literal worked example)
            // cannot be disambiguated by Phase 1's two evidence types alone — that
            // needs the corroboration-tier/album-match evidence added in Phase 2.
            // This is flagged in the Project Log rather than silently smoothed over.
            return artistMbid == MbidX ? "Sarah Vaughan" : "Sara Vaughn";
        }
    }
}
