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

        // Composer-tier relationship-scan ladder (§5.1's Composer-tier variant, built
        // 2026-07-15 to close Project Log Outstanding item A). Distinct rung values
        // from the name-bearing ladder above, purely so diagnostic output honestly
        // shows which mechanism actually produced a hit -- these never filter by
        // ArtistMbid==candidate (the candidate isn't the recording's artist-credit by
        // definition); confirmation instead comes from scanning GetRelationships.
        //
        // ComposerBorrowedName is a real addition beyond §5.1's own text (which only
        // specifies track+album -> track-alone for Composer-tier): search using a
        // co-credited name already observed on the same track (e.g. the real
        // performing artist) as the search-text artist field, purely to narrow
        // MusicBrainz's own search -- NOT anchoring (that would mean trusting an
        // ALREADY-CONFIRMED identity; this is just an unconfirmed name used as query
        // text, same as the ordinary ladder already does with the source artist's own
        // name). Confirmed as in-scope, not parked, per direct instruction.
        ComposerBorrowedNameTrackAlbum = 4,
        ComposerTrackAlbum = 5,
        ComposerTrackOnly = 6,
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
        private readonly MetadataHealthCheck.v2.Diagnostics.StructuredLogger? _logger;
        private readonly Dictionary<(string CandidateMbid, string TrackId), RecordingLookupResult> _cache = new();

        // logger is optional (nullable) rather than required, 2026-07-16 -- this class
        // predates logger threading through the plugin constructor and existing
        // callers/tests shouldn't be forced to supply one just to keep compiling.
        public RecordingLookup(IMusicBrainzApiClient client, MetadataHealthCheck.v2.Diagnostics.StructuredLogger? logger = null)
        {
            _client = client;
            _logger = logger;
        }

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
            {
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- cache hit (rung already resolved: {2}), no new API call.", candidateMbid, track.TrackName, cached.RungReached);
                return cached;
            }

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
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track+artist+album).", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtistAlbum);
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track+artist+album) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtistAlbum);
            }

            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, track.AlbumName, null));
                var evaluated = EvaluateHit(candidateMbid, rec, RecordingLookupRung.TrackAlbum);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track+album).", candidateMbid, track.TrackName, RecordingLookupRung.TrackAlbum);
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track+album) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackAlbum);
            }

            {
                var rec = FindForCandidate(candidateMbid, _client.SearchRecording(track.TrackName, null, null));
                var evaluated = EvaluateHit(candidateMbid, rec, RecordingLookupRung.TrackOnly);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track alone).", candidateMbid, track.TrackName, RecordingLookupRung.TrackOnly);
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track alone) missed.", candidateMbid, track.TrackName, RecordingLookupRung.TrackOnly);
            }

            _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- NOT CONFIRMED at any rung of the ladder.", candidateMbid, track.TrackName);
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

        /// <summary>
        /// Composer-tier confirmation (§5.1's Composer-tier variant; built 2026-07-15
        /// to close Project Log Outstanding item A). The candidate is not the
        /// recording's performing artist, so it cannot be found by filtering
        /// candidate recordings on ArtistMbid==candidate the way the name-bearing
        /// Lookup() above does. Instead: find the recording by other means, then scan
        /// ITS relationship data (GetRelationships) for the candidate's MBID anywhere
        /// in it (work-level for composer/writer, recording-level for
        /// producer/arranger -- this method doesn't discriminate by level itself,
        /// since a track's participants can genuinely appear at either level; the
        /// evidence collectors that call this are what care which level a given hit
        /// came from).
        ///
        /// Ladder: borrowed-name (track+album+each known co-credited name, most
        /// specific first) -> track+album -> track alone. Per-recording relationship
        /// calls are themselves memoized by the underlying IMusicBrainzApiClient
        /// implementation's own concerns, not here -- this method's cache is still the
        /// shared per-(candidate,track) one used by the name-bearing path.
        ///
        /// Unlike the name-bearing ladder, no NameDistanceEvidenceCollector
        /// trustworthiness check runs here -- the recording's ArtistCreditText is the
        /// PERFORMER's name, not the composer-candidate's, so that check isn't
        /// meaningful for this path. A confirmed relationship type IS the safety net.
        /// </summary>
        /// <param name="coCreditNames">
        /// Other names already observed on this same track (e.g. AlbumArtist/Artist
        /// credits) to try as search-text before falling back to track+album/track-
        /// alone. Plain, unconfirmed search text -- not anchoring (§5.1 remains
        /// parked); see the enum comment above.
        /// </param>
        public RecordingLookupResult LookupComposerTier(string candidateMbid, EmbyTrackCredit track, IEnumerable<string> coCreditNames)
        {
            var key = (candidateMbid, track.TrackId);
            if (_cache.TryGetValue(key, out var cached))
            {
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" (composer-tier) -- cache hit (rung already resolved: {2}), no new API call.", candidateMbid, track.TrackName, cached.RungReached);
                return cached;
            }

            var result = ResolveComposerTier(candidateMbid, track, coCreditNames);
            _cache[key] = result;
            return result;
        }

        private RecordingLookupResult ResolveComposerTier(string candidateMbid, EmbyTrackCredit track, IEnumerable<string> coCreditNames)
        {
            foreach (var name in coCreditNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var rec = FindConfirmedByRelationship(candidateMbid, _client.SearchRecording(track.TrackName, track.AlbumName, name));
                if (rec != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" (composer-tier) -- CONFIRMED via relationship scan, borrowed-name rung (co-credit=\"{2}\").", candidateMbid, track.TrackName, name);
                    return new RecordingLookupResult { Recording = rec, RungReached = RecordingLookupRung.ComposerBorrowedNameTrackAlbum };
                }
            }

            {
                var rec = FindConfirmedByRelationship(candidateMbid, _client.SearchRecording(track.TrackName, track.AlbumName, null));
                if (rec != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" (composer-tier) -- CONFIRMED via relationship scan, track+album rung.", candidateMbid, track.TrackName);
                    return new RecordingLookupResult { Recording = rec, RungReached = RecordingLookupRung.ComposerTrackAlbum };
                }
            }

            {
                var rec = FindConfirmedByRelationship(candidateMbid, _client.SearchRecording(track.TrackName, null, null));
                if (rec != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" (composer-tier) -- CONFIRMED via relationship scan, track-alone rung.", candidateMbid, track.TrackName);
                    return new RecordingLookupResult { Recording = rec, RungReached = RecordingLookupRung.ComposerTrackOnly };
                }
            }

            _logger?.Info("RecordingLookup", "[{0}] \"{1}\" (composer-tier) -- NOT CONFIRMED at any rung.", candidateMbid, track.TrackName);
            return new RecordingLookupResult { Recording = null, RungReached = RecordingLookupRung.NotFound };
        }

        // Unlike FindForCandidate (name-bearing ladder), does NOT filter by
        // ArtistMbid==candidate -- checks every returned recording's relationship
        // data for the candidate's MBID appearing anywhere, since a composer
        // candidate is never the artist-credit itself.
        private MbRecordingResult? FindConfirmedByRelationship(string candidateMbid, IReadOnlyList<MbRecordingResult> recordings)
        {
            foreach (var rec in recordings)
            {
                var rels = _client.GetRelationships(rec.RecordingId);
                if (rels.Any(r => r.ArtistMbid == candidateMbid))
                    return rec;
            }
            return null;
        }
    }
}