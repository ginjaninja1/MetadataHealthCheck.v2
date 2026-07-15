using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// Buckets an artist's tracks by role, AlbumArtist -> Artist -> Composer
    /// (§5.3's priority order, highest signal first), and orders each bucket per
    /// §5.3.1's five distance-seeking rules.
    ///
    /// 2 of the 5 §5.3.1 rules are implemented faithfully with data already on
    /// EmbyTrackCredit: different-album-first (rule 1) and different-track-title-
    /// first (rule 3).
    ///
    /// The other 3 are NOT implemented -- do not assume otherwise from earlier
    /// comments in this file's history, which only accounted for 4 rules total
    /// and were themselves incomplete against the spec:
    ///   - Rule 2, single-credit-tracks-first: needs the *full* credit list for a
    ///     track (how many other Artists/AlbumArtists share it), not just this
    ///     artist's own credit -- EmbyTrackCredit doesn't carry that yet.
    ///   - Rule 4, longer-track-titles-first: not implemented and not previously
    ///     even accounted for in this file's comments. No known data gap --
    ///     TrackName is already on EmbyTrackCredit, so this one is likely a small
    ///     addition once picked up, not a model change.
    ///   - Rule 5, shorter-albums-first (&lt;20 tracks): needs a true per-album
    ///     track count, not just how many of an album's tracks this artist
    ///     happens to be credited on -- EmbyTrackCredit doesn't carry that yet.
    ///
    /// Left as a documented gap rather than faked with a misleading proxy.
    /// Tracked as outstanding in the Project Log, §2 item H.
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