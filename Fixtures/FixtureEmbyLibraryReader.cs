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
                        }
                    }
                }
            };
        }
    }
}
