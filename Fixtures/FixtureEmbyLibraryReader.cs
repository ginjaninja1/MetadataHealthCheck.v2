using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Fixtures
{
    /// <summary>
    /// Hardcoded in-memory stand-in for §8.2's E2 query, built directly from
    /// §18's worked example (Sarah Vaughan / "Crazy and Mixed Up"). Used for
    /// the Phase 1 end-to-end skeleton test and unit tests; replaced by the
    /// real ILibraryManager-backed reader once run against an actual Emby
    /// server install (see IEmbyLibraryReader.cs remarks).
    /// </summary>
    public class FixtureEmbyLibraryReader : IEmbyLibraryReader
    {
        public IReadOnlyList<EmbyArtist> ReadAllArtists()
        {
            return new List<EmbyArtist>
            {
                new EmbyArtist
                {
                    SourceId = "emby-artist-sarah-vaughan",
                    DisplayName = "Sarah Vaughan",
                    Tracks = new List<EmbyTrackCredit>
                    {
                        new EmbyTrackCredit
                        {
                            TrackId = "track-crazy-and-mixed-up-01",
                            TrackName = "Autumn Leaves",
                            AlbumName = "Crazy and Mixed Up",
                            AlbumId = "album-crazy-and-mixed-up",
                            Role = "AlbumArtist",
                            // Real duration (335240ms), confirmed 2026-07-12 against the actual
                            // MusicBrainz recording (id 5dbea991-e5e9-4489-81a2-d5e8e13f161a) --
                            // not a placeholder. AlbumArtists/Artists/Composers are populated
                            // uniformly regardless of tier (this artist's own AlbumArtist credit
                            // is the only real name known for this fixture; Emby id itself is a
                            // placeholder, since that's Emby-internal and wasn't part of the real
                            // MusicBrainz data pulled for this case).
                            Duration = TimeSpan.FromMilliseconds(335240),
                            AlbumArtists = new List<EmbyCreditedName>
                            {
                                new EmbyCreditedName { Name = "Sarah Vaughan", EmbyId = "emby-artist-sarah-vaughan" }
                            },
                        }
                    }
                }
            };
        }
    }
}