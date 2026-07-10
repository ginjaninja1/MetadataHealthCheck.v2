using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// Buckets an artist's tracks by role, AlbumArtist -> Artist -> Composer
    /// (§5's priority order, highest signal first), and orders each bucket per
    /// §5.5.1's distance-seeking rules.
    ///
    /// Two of the four §5.5.1 rules are implemented faithfully with data already
    /// on EmbyTrackCredit: different-album-first (rule 1) and different-track-title-
    /// first (rule 3). The other two -- single-credit-tracks-first (rule 2) and
    /// shorter-albums-first (rule 4) -- need data this model doesn't carry yet:
    /// rule 2 needs the *full* credit list for a track (how many other Artists/
    /// AlbumArtists share it), not just this artist's own credit; rule 4 needs a
    /// true per-album track count, not just how many of an album's tracks this
    /// artist happens to be credited on. Left as a documented gap rather than
    /// faked with a misleading proxy -- revisit once EmbyLibraryReader's E2 read
    /// is widened to carry that data (§21 phase 2/3).
    /// </summary>
    public class EmbyArtistObservationUnitProvider : IObservationUnitProvider<EmbyArtist>
    {
        private static readonly string[] BucketOrder = { "AlbumArtist", "Artist", "Composer" };

        public IEnumerable<IEnumerable<IObservationUnit>> GetOrderedBuckets(EmbyArtist source, ResolutionContext context)
        {
            foreach (var bucket in BucketOrder)
            {
                var tracksInBucket = source.Tracks.Where(t => string.Equals(t.Role, bucket, StringComparison.OrdinalIgnoreCase));
                yield return OrderByDistanceSeeking(tracksInBucket).Select(t => (IObservationUnit)new EmbyTrackObservationUnit(t));
            }
        }

        private static List<EmbyTrackCredit> OrderByDistanceSeeking(IEnumerable<EmbyTrackCredit> tracks)
        {
            var remaining = tracks.ToList();
            var ordered = new List<EmbyTrackCredit>(remaining.Count);
            var seenAlbums = new HashSet<string>();
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (remaining.Count > 0)
            {
                // Prefer a track that's new on both axes, then new on either axis,
                // then whatever's left -- rules 1 and 3, in that priority order.
                var next = remaining.FirstOrDefault(t => !seenAlbums.Contains(t.AlbumId) && !seenTitles.Contains(t.TrackName))
                        ?? remaining.FirstOrDefault(t => !seenAlbums.Contains(t.AlbumId))
                        ?? remaining.FirstOrDefault(t => !seenTitles.Contains(t.TrackName))
                        ?? remaining[0];

                ordered.Add(next);
                remaining.Remove(next);
                seenAlbums.Add(next.AlbumId);
                seenTitles.Add(next.TrackName);
            }

            return ordered;
        }
    }
}