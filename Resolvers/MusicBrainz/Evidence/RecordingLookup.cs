using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Which rung of the fallback ladder actually produced a hit for a given
    /// (candidate, track) lookup. Diagnostic-only for now, per the Project Log's
    /// own honest-gap note: not yet used to drive any scoring decision, and until
    /// this file existed, no fixture case varied its result by the artistName
    /// parameter, so RungReached could never be observed as anything but
    /// TrackArtistAlbum or NotFound. FixtureMusicBrainzApiClient's "Ladder
    /// Fallback Case" (added alongside this file) exercises TrackOnly directly.
    /// </summary>
    public enum RecordingLookupRung
    {
        NotFound = 0,
        TrackArtistAlbum = 1,
        TrackAlbum = 2,
        TrackOnly = 3,
    }

    public class RecordingLookupResult
    {
        public MbRecordingResult? Recording { get; set; }
        public RecordingLookupRung RungReached { get; set; } = RecordingLookupRung.NotFound;
    }

    /// <summary>
    /// Shared, memoized per-(candidate, track) recording lookup (§7.2 C3/C4).
    /// Built 2026-07-12 per the coding checklist in V2_Project_Log.md, replacing
    /// what would otherwise be an independent SearchRecording call in every
    /// evidence collector that needs to confirm a candidate against a specific
    /// track. WorkRelationshipEvidenceCollector is refactored onto this in the
    /// same unit of work. CorroborationTierEvidenceCollector,
    /// RecordingRelationshipEvidenceCollector, and AlbumMatchEvidenceCollector are
    /// NOT built yet (an earlier log entry claimed otherwise; ground-truth
    /// verification on 2026-07-12 found none of the three exist in the repo) —
    /// they remain separate, not-yet-started follow-up work, but should take this
    /// same shared instance once they exist rather than each rolling their own
    /// SearchRecording call.
    ///
    /// Fallback ladder (§7.2, settled 2026-07-12): track+artist+album ->
    /// track+album -> track alone. There is no way to fully avoid the risk that a
    /// wrong album or wrong artist-name text degrades a fallback rung's result
    /// quality — this is a known, accepted, low-probability-in-practice risk, not
    /// solved here. The real safety net is NameDistanceEvidenceCollector's
    /// downstream rejection of poor name matches, already proven against exactly
    /// this shape of risk by the Gus Black/Del Serino cases (a spurious candidate
    /// with genuine Tier 1 corroboration under the wrong MBID is still correctly
    /// rejected on name grounds).
    ///
    /// Memoization is per RecordingLookup instance, per (candidateMbid, trackId)
    /// pair, for the lifetime of one shared instance — constructed once in
    /// MusicBrainzArtistResolverPlugin and passed to every collector that needs it,
    /// so only the first collector to ask about a given (candidate, track) pair in
    /// a sampler round actually triggers a MusicBrainz call.
    /// </summary>
    public class RecordingLookup
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly Dictionary<(string CandidateMbid, string TrackId), RecordingLookupResult> _cache = new();

        public RecordingLookup(IMusicBrainzApiClient client) => _client = client;

        /// <param name="candidateMbid">The candidate's MBID being confirmed.</param>
        /// <param name="track">The observed track (carries TrackName/AlbumName/TrackId).</param>
        /// <param name="artistName">
        /// The Emby-tagged display name for this credit, used on rung 1 only. Pass
        /// null if unavailable — rung 1 is then equivalent to rung 2.
        /// </param>
        public RecordingLookupResult Lookup(string candidateMbid, EmbyTrackCredit track, string? artistName)
        {
            var key = (candidateMbid, track.TrackId);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var result = Resolve(candidateMbid, track, artistName);
            _cache[key] = result;
            return result;
        }

        private RecordingLookupResult Resolve(string candidateMbid, EmbyTrackCredit track, string? artistName)
        {
            // Rung 1: track + artist + album — the tightest, most specific query.
            if (artistName != null)
            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, track.AlbumName, artistName));
                if (rec != null)
                    return new RecordingLookupResult { Recording = rec, RungReached = RecordingLookupRung.TrackArtistAlbum };
            }

            // Rung 2: track + album (drop artist name).
            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, track.AlbumName, null));
                if (rec != null)
                    return new RecordingLookupResult { Recording = rec, RungReached = RecordingLookupRung.TrackAlbum };
            }

            // Rung 3: track title alone (drop both artist and album).
            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, null, null));
                if (rec != null)
                    return new RecordingLookupResult { Recording = rec, RungReached = RecordingLookupRung.TrackOnly };
            }

            return new RecordingLookupResult { Recording = null, RungReached = RecordingLookupRung.NotFound };
        }

        private static MbRecordingResult? FindForCandidate(string candidateMbid, IReadOnlyList<MbRecordingResult> recordings)
            => recordings.FirstOrDefault(r => r.ArtistMbid == candidateMbid);
    }
}