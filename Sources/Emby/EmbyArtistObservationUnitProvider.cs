using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Sources.Emby
{
    /// <summary>
    /// Buckets an artist's tracks by role, AlbumArtist -> Artist -> Composer
    /// (§5.3's priority order, highest signal first), and orders each bucket per
    /// §5.3.1's five distance-seeking rules.
    ///
    /// 3 of the 5 §5.3.1 rules are implemented with data already on
    /// EmbyTrackCredit: different-track-title-first (rule 3), different-album-first
    /// (rule 1), and longer-track-title-first (rule 4). Rules 1 and 3 are NOT
    /// independent, equally-weighted axes -- title-newness dominates album-newness
    /// when they conflict. A repeated track title on a new album (a reissue,
    /// compilation, or remaster of the same song) is very likely the same
    /// underlying MusicBrainz recording/work under a different release -- low
    /// independent signal even though the album differs. A new title is a
    /// genuinely different recording regardless of which album it's packaged on.
    /// So the four possible combinations collapse into one ordered tie-break, not
    /// two peer rules run in sequence:
    ///   Tier 1: new title, new album   (best -- different work, different packaging)
    ///   Tier 2: new title, old album   (still a different work)
    ///   Tier 3: old title, new album   (same work, repackaged -- last resort)
    ///   Tier 4: old title, old album   (true duplicate -- should rarely occur)
    /// Rule 4 (longer-title-first) is a tie-break *within* each of the above tiers,
    /// not a fifth tier of its own -- a longer title gives a MusicBrainz recording
    /// search more words to match on, so it's less likely to return noise when few
    /// corroborating data points are available in the lookup.
    /// This ordering is bucket-agnostic and applies the same way whether the
    /// bucket being ordered is AlbumArtist, Artist, or Composer.
    ///
    /// The remaining 2 of the 5 §5.3.1 rules are NOT implemented -- do not assume
    /// otherwise from earlier comments in this file's history, which only
    /// accounted for 4 rules total and were themselves incomplete against the
    /// spec:
    ///   - Rule 2, single-credit-tracks-first: needs the *full* credit list for a
    ///     track (how many other Artists/AlbumArtists share it), not just this
    ///     artist's own credit -- EmbyTrackCredit doesn't carry that yet. Not
    ///     confirmed necessary -- parked pending evidence it's needed.
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
                // Title-newness dominates album-newness in both directions (rules 1
                // and 3 collapsed into one ordered tie-break -- see the class-level
                // comment for the four-tier breakdown). Rule 4 (longer-title-first)
                // then breaks ties *within* each tier via OrderByDescending on title
                // length, rather than introducing a tier of its own.
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