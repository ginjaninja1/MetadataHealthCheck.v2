using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// The TrackDuration rung's data: a title+qdur search plus the
    /// artist-recording-frequency tally built from it. Depends only on the
    /// track (title + duration), never on which candidate is being confirmed,
    /// so it's fetched and memoized once per track regardless of how many
    /// candidates ask for it.
    ///
    /// Frequency is tallied by first-credited artist MBID only -- a known
    /// simplification (a genuine duet recording's second artist is invisible
    /// to this tally). Subgroup-relationship folding (e.g. "Queen + Paul
    /// Rodgers" -> "Queen") is deliberately not done here: it would require an
    /// extra GetArtistRelationships call per distinct artist MBID in the
    /// result set, for artists that aren't yet even candidates -- a real
    /// cost/design question left for a follow-up decision.
    /// </summary>
    internal class RecordingDurationLookup
    {
        public class Result
        {
            public IReadOnlyList<MbRecordingResult> Recordings { get; set; } = Array.Empty<MbRecordingResult>();
            public IReadOnlyList<(string ArtistMbid, int Count)> RankedArtists { get; set; } = Array.Empty<(string, int)>();
        }

        private readonly IMusicBrainzApiClient _client;
        private readonly ArtistMusicBrainzConfig _config;
        private readonly StructuredLogger? _logger;
        private readonly Dictionary<string, Result> _cache = new();

        public RecordingDurationLookup(IMusicBrainzApiClient client, ArtistMusicBrainzConfig config, StructuredLogger? logger)
        {
            _client = client;
            _config = config;
            _logger = logger;
        }

        public Result GetOrBuild(EmbyTrackCredit track)
        {
            if (_cache.TryGetValue(track.TrackId, out var cached))
            {
                int observedMsForLog = (int)Math.Round(track.Duration!.Value.TotalMilliseconds);
                var describedUrl = _client.DescribeSearchRecordingByTitleAndDurationUrl(track.TrackName, observedMsForLog, _config.QdurToleranceBuckets);
                _logger?.Info("RecordingLookup", "[TrackDuration] \"{0}\"", track.TrackName);
                _logger?.Info("RecordingLookup", "  GET https://musicbrainz.emby.tv/ws/2/{0}", describedUrl);
                _logger?.Debug("RecordingLookup", "  -> qdur query cache hit, no new API call.");
                return cached;
            }

            int observedMs = (int)Math.Round(track.Duration!.Value.TotalMilliseconds);
            var recordings = _client.SearchRecordingByTitleAndDuration(track.TrackName, observedMs, _config.QdurToleranceBuckets);

            var ranked = recordings
                .Where(r => !string.IsNullOrEmpty(r.ArtistMbid))
                .GroupBy(r => r.ArtistMbid)
                .Select(g => (ArtistMbid: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();

            var result = new Result { Recordings = recordings, RankedArtists = ranked };
            _cache[track.TrackId] = result;

            _logger?.Info("RecordingLookup", "[TrackDuration] \"{0}\" -- {1} recording(s) within qdur window, {2} distinct artist(s), leader={3} (count={4}).",
                track.TrackName, recordings.Count, ranked.Count,
                ranked.Count > 0 ? ranked[0].ArtistMbid : "(none)",
                ranked.Count > 0 ? ranked[0].Count : 0);

            return result;
        }
    }
}
