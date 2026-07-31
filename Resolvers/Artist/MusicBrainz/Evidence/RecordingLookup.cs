using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client.Model;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence
{
    /// <summary>
    /// Multi-candidate, round-based recording lookup: checks every still-pending
    /// candidate jointly, one rung at a time, stopping the moment a caller-side
    /// decision gate is satisfied. `yield return`-based throughout its private
    /// helpers -- a caller that stops enumerating early (foreach+break) means the
    /// next recording's GetRelationships call genuinely never fires, not merely
    /// that its result is discarded.
    ///
    /// Fallback ladder, most specific first: track+artist+album -> track+artist
    /// -> track+album -> title+qdur (TrackDuration) -> track alone. Only advances
    /// to the next rung's query when the current rung's raw search returns zero
    /// recordings -- if MusicBrainz returned any recording, the search succeeded,
    /// so any pending candidate this rung's rounds don't confirm is a genuine
    /// non-match, not a reason to try a looser query.
    ///
    /// A recording is confirmed for a candidate if either (a) the candidate is
    /// one of the recording's credited artists (track-level artist credit or
    /// release-level "album artist" credit, mined across every release the
    /// recording appears on), or (b) the candidate's MBID or one of its
    /// RelationshipMbids appears in the recording's own relationship data
    /// (GetRelationships) -- an equally strict, exact-MBID second path for
    /// candidates (e.g. composer-only artists) who structurally can never
    /// satisfy (a). Which of these two checks runs for a given observation is
    /// decided by ConfirmationMode, passed in by the caller from the
    /// observation's bucket.
    ///
    /// Before either check runs, candidates at a rung are gated on recording
    /// length vs. the observed track's own duration (missing duration data does
    /// not gate a candidate out, only a confirmed mismatch does -- see
    /// RecordingRichnessRanking.ApplyDurationGate), then sorted by richness
    /// heuristics that decide walk order only, never correctness (see
    /// RecordingRichnessRanking.RichnessRank). The walk stops at the first
    /// confirmed match.
    ///
    /// Memoization is per RecordingLookup instance, for the lifetime of one
    /// shared instance constructed once and passed to every collector that
    /// needs it.
    /// </summary>
    public class RecordingLookup
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly ArtistMusicBrainzConfig _config;
        private readonly StructuredLogger? _logger;
        private readonly RecordingDurationLookup _durationLookup;

        // The name/album rungs' raw search results depend only on the track and
        // which rung is being tried, never on which candidate is being confirmed
        // against them -- shared across candidates so two same-named candidates
        // don't each trigger an identical HTTP call per rung.
        private readonly Dictionary<(string TrackId, RecordingLookupRung Rung), IReadOnlyList<MbRecordingResult>> _nameRungSearchCache = new();

        public RecordingLookup(IMusicBrainzApiClient client, ArtistMusicBrainzConfig config, StructuredLogger? logger = null)
        {
            _client = client;
            _config = config;
            _logger = logger;
            _durationLookup = new RecordingDurationLookup(client, config, logger);
        }

        public IEnumerable<RecordingLookupRoundResult> LookupRounds(IReadOnlyList<string> candidateMbids, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, EmbyTrackCredit track, IEnumerable<string>? artistNames, ConfirmationMode mode)
        {
            var names = (artistNames ?? Enumerable.Empty<string>()).ToList();
            var pending = new HashSet<string>(candidateMbids);

            if (names.Count > 0)
            {
                var recordings = SearchRecordingCached(track, track.AlbumName, names, RecordingLookupRung.TrackArtistAlbum);
                if (recordings.Count > 0)
                {
                    foreach (var round in RoundsForRung(RecordingLookupRung.TrackArtistAlbum, recordings, track, pending, relationshipMbidsByCandidate, mode))
                    {
                        yield return round;
                        if (pending.Count == 0) yield break;
                    }
                    yield break;
                }
                _logger?.Debug("RecordingLookup", "[TrackArtistAlbum] \"{0}\" -- returned zero recordings, falling through.", track.TrackName);
            }

            if (names.Count > 0)
            {
                var recordings = SearchRecordingCached(track, null, names, RecordingLookupRung.TrackArtist);
                if (recordings.Count > 0)
                {
                    foreach (var round in RoundsForRung(RecordingLookupRung.TrackArtist, recordings, track, pending, relationshipMbidsByCandidate, mode))
                    {
                        yield return round;
                        if (pending.Count == 0) yield break;
                    }
                    yield break;
                }
                _logger?.Debug("RecordingLookup", "[TrackArtist] \"{0}\" -- returned zero recordings, falling through.", track.TrackName);
            }

            {
                var recordings = SearchRecordingCached(track, track.AlbumName, null, RecordingLookupRung.TrackAlbum);
                if (recordings.Count > 0)
                {
                    foreach (var round in RoundsForRung(RecordingLookupRung.TrackAlbum, recordings, track, pending, relationshipMbidsByCandidate, mode))
                    {
                        yield return round;
                        if (pending.Count == 0) yield break;
                    }
                    yield break;
                }
                _logger?.Debug("RecordingLookup", "[TrackAlbum] \"{0}\" -- returned zero recordings, falling through.", track.TrackName);
            }

            if (track.Duration.HasValue)
            {
                var data = _durationLookup.GetOrBuild(track);
                if (data.Recordings.Count > 0)
                {
                    foreach (var round in RoundsForDurationRung(track, pending, relationshipMbidsByCandidate, mode))
                    {
                        yield return round;
                        if (pending.Count == 0) yield break;
                    }
                    yield break;
                }
                _logger?.Debug("RecordingLookup", "[TrackDuration] \"{0}\" -- returned zero recordings, falling through.", track.TrackName);
            }

            foreach (var round in RoundsForRung(RecordingLookupRung.TrackOnly, SearchRecordingCached(track, null, null, RecordingLookupRung.TrackOnly), track, pending, relationshipMbidsByCandidate, mode))
            {
                yield return round;
                if (pending.Count == 0) yield break;
            }

            // Any candidate still in `pending` here was never confirmed at any
            // rung -- the caller should treat any candidateMbid with no
            // NewlyConfirmed entry across every yielded round as RecordingLookupRung.NotFound.
        }

        // One rung's worth of rounds for the name/album rungs: gate and
        // richness-order the raw results, then hand off to the shared
        // confirmation walk.
        private IEnumerable<RecordingLookupRoundResult> RoundsForRung(RecordingLookupRung rung, IReadOnlyList<MbRecordingResult> recordings, EmbyTrackCredit track, HashSet<string> pending, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, ConfirmationMode mode)
        {
            var survivors = RecordingRichnessRanking.ApplyDurationGate(recordings, track.Duration, _config)
                .OrderBy(RecordingRichnessRanking.RichnessRank)
                .ToList();

            foreach (var round in ConfirmPendingAgainstOrderedRecordings(rung, survivors, pending, relationshipMbidsByCandidate, mode))
                yield return round;
        }

        // TrackDuration's rung: order by artist-recording-frequency within the
        // qdur-narrowed result set (falling back to richness order when the
        // lead isn't meaningful), since duration alone can't disambiguate
        // correctness the way a name/album field can -- frequency (which
        // artist has the most recordings clustered at this title+duration)
        // stands in for it. Confirmation itself is identical to every other
        // rung; see ConfirmPendingAgainstOrderedRecordings.
        private IEnumerable<RecordingLookupRoundResult> RoundsForDurationRung(EmbyTrackCredit track, HashSet<string> pending, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, ConfirmationMode mode)
        {
            var data = _durationLookup.GetOrBuild(track);

            bool leadIsMeaningful = data.RankedArtists.Count >= 1 &&
                (data.RankedArtists.Count == 1 || data.RankedArtists[0].Count - data.RankedArtists[1].Count >= _config.TrackDurationMinArtistLead);

            var rankPosition = data.RankedArtists
                .Select((x, i) => (x.ArtistMbid, Rank: i))
                .ToDictionary(x => x.ArtistMbid, x => x.Rank);

            var survivors = data.Recordings
                .OrderBy(r => leadIsMeaningful && rankPosition.TryGetValue(r.ArtistMbid, out var rank) ? rank : int.MaxValue)
                .ThenBy(RecordingRichnessRanking.RichnessRank)
                .ToList();

            foreach (var round in ConfirmPendingAgainstOrderedRecordings(RecordingLookupRung.TrackDuration, survivors, pending, relationshipMbidsByCandidate, mode))
                yield return round;
        }

        // The confirmation walk shared by every rung: a single cheap round
        // (performer-credit, no API call) covering every survivor at once,
        // then one expensive round per surviving recording (relationship
        // scan), each checked against every still-pending candidate. Which of
        // the two rounds runs is decided by mode: PerformerOnly runs the cheap
        // round only and never calls GetRelationships; RelationshipOnly skips
        // the cheap round entirely and goes straight to the scan. The only
        // thing that varies between rungs is the order `orderedSurvivors`
        // arrives in -- the confirmation logic itself never changes.
        private IEnumerable<RecordingLookupRoundResult> ConfirmPendingAgainstOrderedRecordings(RecordingLookupRung rung, IReadOnlyList<MbRecordingResult> orderedSurvivors, HashSet<string> pending, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, ConfirmationMode mode)
        {
            if (mode == ConfirmationMode.PerformerOnly)
            {
                var cheapConfirmed = new Dictionary<string, RecordingLookupResult>();
                foreach (var candidateMbid in pending.ToList())
                {
                    foreach (var rec in orderedSurvivors)
                    {
                        // Match either the recording's own (track-level) artist
                        // credit -- any credited artist, not just the
                        // first-listed one -- or any release-level "album
                        // artist" credit this recording carries across all its
                        // releases.
                        bool matchedViaTrackArtist = rec.ArtistMbids.Contains(candidateMbid);
                        bool matchedViaReleaseAlbumArtist = !matchedViaTrackArtist
                            && rec.ReleaseAlbumArtistMbids.Contains(candidateMbid);
                        if (!matchedViaTrackArtist && !matchedViaReleaseAlbumArtist) continue;

                        cheapConfirmed[candidateMbid] = new RecordingLookupResult
                        {
                            Recording = rec,
                            RungReached = rung,
                            MatchedViaAlias = false,
                            ConfirmedViaRelationship = false,
                        };
                        break;
                    }
                }
                foreach (var mbid in cheapConfirmed.Keys) pending.Remove(mbid);
                if (cheapConfirmed.Count > 0)
                    yield return new RecordingLookupRoundResult { NewlyConfirmed = cheapConfirmed, RoundDescription = $"{rung} (performer-credit, no API call)" };

                if (pending.Count == 0) yield break;
            }

            if (mode != ConfirmationMode.RelationshipOnly) yield break;

            foreach (var rec in orderedSurvivors)
            {
                if (pending.Count == 0) yield break;

                _logger?.Info("RecordingLookup", "recordingId={0} -- relationship scan for {1} remaining candidate(s) (rung={2}).", rec.RecordingId, pending.Count, rung);
                var rels = _client.GetRelationships(rec.RecordingId);

                var confirmedThisRecording = new Dictionary<string, RecordingLookupResult>();
                foreach (var candidateMbid in pending.ToList())
                {
                    var relIds = relationshipMbidsByCandidate.TryGetValue(candidateMbid, out var r) ? r : Array.Empty<string>();
                    var confirming = rels.FirstOrDefault(rr => rr.ArtistMbid == candidateMbid || relIds.Contains(rr.ArtistMbid));
                    if (confirming == null) continue;

                    confirmedThisRecording[candidateMbid] = new RecordingLookupResult
                    {
                        Recording = rec,
                        RungReached = rung,
                        MatchedViaAlias = false,
                        ConfirmedViaRelationship = true,
                        ConfirmingRelationship = confirming,
                    };
                }
                foreach (var mbid in confirmedThisRecording.Keys) pending.Remove(mbid);
                if (confirmedThisRecording.Count > 0)
                {
                    yield return new RecordingLookupRoundResult { NewlyConfirmed = confirmedThisRecording, RoundDescription = $"{rung} (relationship scan, recordingId={rec.RecordingId})" };

                    // Stop here rather than advancing to the next recording.
                    // Every still-pending candidate was already checked against
                    // this recording (for free, same GetRelationships call) and
                    // did not confirm -- a real "no match at this rung" result,
                    // not "not yet tried". A candidate that only matched a
                    // later recording in richness order would be matching a
                    // different recording than the one that already confirmed
                    // someone else -- a false-positive risk (cover version,
                    // reissue with different personnel), not corroboration of
                    // the same fact. Remaining pending candidates get another
                    // chance from the top rung on the next observation.
                    yield break;
                }
            }
        }

        private IReadOnlyList<MbRecordingResult> SearchRecordingCached(EmbyTrackCredit track, string? albumTitle, IEnumerable<string>? artistNames, RecordingLookupRung rung)
        {
            var key = (track.TrackId, rung);
            if (_nameRungSearchCache.TryGetValue(key, out var cached))
            {
                var describedUrl = _client.DescribeSearchRecordingUrl(track.TrackName, albumTitle, artistNames);
                _logger?.Info("RecordingLookup", "[{0}] \"{1}\"", rung, track.TrackName);
                _logger?.Info("RecordingLookup", "  GET https://musicbrainz.emby.tv/ws/2/{0}", describedUrl);
                _logger?.Debug("RecordingLookup", "  -> rung query cache hit, no new API call.");
                return cached;
            }

            var results = _client.SearchRecording(track.TrackName, albumTitle, artistNames);
            _nameRungSearchCache[key] = results;
            return results;
        }
    }
}
