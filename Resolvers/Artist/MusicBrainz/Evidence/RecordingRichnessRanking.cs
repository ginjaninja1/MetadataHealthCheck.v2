using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// Pure, stateless recording gating/ranking used by RecordingLookup: a
    /// duration pre-filter, and a walk-order heuristic among survivors.
    /// Neither ever affects correctness -- ApplyDurationGate only excludes on
    /// a confirmed mismatch (missing duration data is not a disqualification),
    /// and RichnessRank only decides which recording is worth the cost of the
    /// first relationship-fetch call, not whether a candidate is correct.
    /// Country and release date are deliberately excluded from both -- real
    /// bias risk against non-Anglophone/older-catalog entries at scale, with
    /// no gain in match accuracy.
    /// </summary>
    internal static class RecordingRichnessRanking
    {
        // MusicBrainz's canonical "Various Artists" artist MBID -- fixed across
        // the whole database, not a per-install config value.
        private const string VariousArtistsMbid = "89ad4ac3-39f7-470e-963a-56509c546377";

        public static IEnumerable<MbRecordingResult> ApplyDurationGate(IReadOnlyList<MbRecordingResult> recordings, TimeSpan? observedDuration, ArtistMusicBrainzConfig config)
        {
            if (!config.EnableRecordingDurationGate)
                return recordings;

            if (!observedDuration.HasValue)
                return recordings;

            var observedMs = observedDuration.Value.TotalMilliseconds;
            return recordings.Where(r =>
            {
                if (!r.LengthMs.HasValue)
                    return !config.ExcludeRecordingsWithMissingDuration;

                var diffMs = Math.Abs(r.LengthMs.Value - observedMs);
                return diffMs <= observedMs * config.DurationGateTolerancePercent;
            });
        }

        public static int RichnessRank(MbRecordingResult r)
        {
            int statusRank = r.ReleaseStatus switch
            {
                "Official" => 0,
                "Promotion" => 1,
                "Bootleg" => 2,
                _ => 1, // unknown status: treated as middle-of-the-road, not penalized to the back
            };

            int typeRank = r.ReleaseGroupPrimaryType switch
            {
                "Album" => 0,
                "EP" => 1,
                "Single" => 2,
                _ => 3, // unknown/other primary type
            };

            // Secondary types (Live/Compilation) push an otherwise studio-looking
            // release further back. Compilation is penalized only when the
            // release's own artist credit is actually "Various Artists" -- an
            // artist's own compilation/anthology is just as likely to be
            // well-indexed as a studio album, so only genuine Various-Artists
            // compilations carry the richness penalty.
            bool isVariousArtistsCompilation =
                r.ReleaseGroupSecondaryTypes.Contains("Compilation", StringComparer.OrdinalIgnoreCase)
                && (r.ReleaseAlbumArtistMbid == VariousArtistsMbid
                    || (r.ReleaseAlbumArtistCreditText ?? "").Trim().Equals("Various Artists", StringComparison.OrdinalIgnoreCase));

            bool isLive = r.ReleaseGroupSecondaryTypes.Contains("Live", StringComparer.OrdinalIgnoreCase);

            if (isVariousArtistsCompilation || isLive)
                typeRank += 2;

            // Status dominates, then type, then (inverted, since higher release
            // count is better) release count, capped so it can never outrank a
            // status/type difference.
            int releaseCountRank = Math.Max(0, 100 - r.ReleaseCount);
            return statusRank * 10000 + typeRank * 1000 + releaseCountRank;
        }
    }
}
