using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// Buckets an artist's tracks by role, AlbumArtist -> Artist -> Composer
    /// (highest signal first), and orders each bucket to seek maximum distance
    /// between successive observations.
    ///
    /// Within a bucket, title-newness dominates album-newness when they
    /// conflict: a repeated track title on a new album (a reissue, compilation,
    /// or remaster) is very likely the same underlying MusicBrainz recording
    /// under a different release -- low independent signal even though the
    /// album differs. A new title is a genuinely different recording regardless
    /// of which album it's packaged on. So distance-seeking collapses to one
    /// ordered tie-break, applied identically to every bucket:
    ///   Tier 1: new title, new album   (different work, different packaging)
    ///   Tier 2: new title, old album   (still a different work)
    ///   Tier 3: old title, new album   (same work, repackaged -- last resort)
    ///   Tier 4: old title, old album   (true duplicate -- should rarely occur)
    /// Longer-title-first is then a tie-break within each tier: a longer title
    /// gives a MusicBrainz recording search more words to match on, so it's less
    /// likely to return noise when few corroborating data points exist yet.
    ///
    /// Not yet implemented: preferring single-credit tracks first, and
    /// preferring tracks from shorter albums first. Both need track-level credit
    /// and album-size data that EmbyTrackCredit doesn't currently carry.
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
                var next = remaining.Where(t => !seenTitles.Contains(t.TrackName) && !seenAlbums.Contains(t.AlbumId))
                                    .OrderByDescending(t => t.TrackName.Length).FirstOrDefault()
                        ?? remaining.Where(t => !seenTitles.Contains(t.TrackName))
                                    .OrderByDescending(t => t.TrackName.Length).FirstOrDefault()
                        ?? remaining.Where(t => !seenAlbums.Contains(t.AlbumId))
                                    .OrderByDescending(t => t.TrackName.Length).FirstOrDefault()
                        ?? remaining.OrderByDescending(t => t.TrackName.Length).First();

                ordered.Add(next);
                remaining.Remove(next);
                seenAlbums.Add(next.AlbumId);
                seenTitles.Add(next.TrackName);
            }

            return ordered;
        }
    }
}
