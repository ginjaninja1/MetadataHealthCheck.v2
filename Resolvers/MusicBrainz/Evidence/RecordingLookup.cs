using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// Which rung of the fallback ladder actually produced a trustworthy hit for a
    /// given (candidate, track) lookup.
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

        // Added 2026-07-13: whether this hit matched the candidate's primary MB name
        // (false) or only a registered alias (true), per
        // NameDistanceEvidenceCollector.EvaluateRecordingMatch. Drives
        // EvidenceRecord.MatchedViaAlias -> ScoringConfig.NameMatchWeight/
        // AliasMatchWeight at scoring time (§5.3/§6.3). Meaningless when Recording is
        // null (defaults false).
        public bool MatchedViaAlias { get; set; }
    }

    /// <summary>
    /// Shared, memoized per-(candidate, track) recording lookup (§7.2 C3/C4).
    /// Used both by SoftBucketStrategy (Stage 2 per-candidate confirmation, §5.3) and
    /// by per-observation evidence collectors (WorkRelationship, CorroborationTier)
    /// that need to confirm a candidate against a specific track.
    ///
    /// Fallback ladder (§7.2/§5.4): track+artist+album -> track+album -> track alone.
    /// At each rung, a hit is only accepted if
    /// NameDistanceEvidenceCollector.EvaluateRecordingMatch judges the recording's
    /// artist-credit text trustworthy against this candidate's name/aliases — this is
    /// the real safety net against a wrong-album/wrong-artist-text fallback rung
    /// producing a false positive (§5.4). A hit judged TooPoorToTrust at one rung is
    /// treated as a miss and the ladder continues to the next rung, not returned.
    ///
    /// Memoization is per RecordingLookup instance, per (candidateMbid, trackId)
    /// pair, for the lifetime of one shared instance — constructed once in
    /// MusicBrainzArtistResolverPlugin and passed to every collector that needs it.
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
            if (artistName != null)
            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, track.AlbumName, artistName));
                var evaluated = EvaluateHit(candidateMbid, rec, RecordingLookupRung.TrackArtistAlbum);
                if (evaluated != null) return evaluated;
            }

            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, track.AlbumName, null));
                var evaluated = EvaluateHit(candidateMbid, rec, RecordingLookupRung.TrackAlbum);
                if (evaluated != null) return evaluated;
            }

            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, null, null));
                var evaluated = EvaluateHit(candidateMbid, rec, RecordingLookupRung.TrackOnly);
                if (evaluated != null) return evaluated;
            }

            return new RecordingLookupResult { Recording = null, RungReached = RecordingLookupRung.NotFound };
        }

        // Returns null if there's no recording at this rung at all, OR if there is one
        // but it's judged too poor a name match to trust (§5.4) — both cases mean
        // "keep falling through the ladder", which is why this doesn't distinguish
        // them to the caller.
        private RecordingLookupResult? EvaluateHit(string candidateMbid, MbRecordingResult? rec, RecordingLookupRung rung)
        {
            if (rec == null)
                return null;

            var candidateName = _client.GetArtistDisplayName(candidateMbid);
            var candidateAliases = _client.GetArtistAliases(candidateMbid);
            var outcome = NameDistanceEvidenceCollector.EvaluateRecordingMatch(candidateName, candidateAliases, rec.ArtistCreditText);

            if (outcome == NameMatchOutcome.TooPoorToTrust)
                return null;

            return new RecordingLookupResult
            {
                Recording = rec,
                RungReached = rung,
                MatchedViaAlias = outcome == NameMatchOutcome.MatchedViaAlias,
            };
        }

        private static MbRecordingResult? FindForCandidate(string candidateMbid, IReadOnlyList<MbRecordingResult> recordings)
            => recordings.FirstOrDefault(r => r.ArtistMbid == candidateMbid);
    }
}