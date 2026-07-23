using MetadataHealthCheck.v2.Core.Model;
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

        // Added 2026-07-19: title+artist, no album. Sits above TrackAlbum -- when the
        // ALBUM string is the doozie (e.g. a radio-countdown compilation title that
        // will never match a real MB release) rather than the artist string, this
        // rung rescues the observation without needing to fall all the way to
        // TrackAlbum/TrackOnly. Deliberately does NOT replace TrackAlbum in the
        // ladder -- the reverse case (artist string is the doozie, album is real)
        // is exactly as real and exactly as unaddressed by this rung; both stay in
        // the ladder rather than picking one over the other from a single example.
        TrackArtist = 2,
        TrackAlbum = 3,

        // Added 2026-07-19: title + MusicBrainz's own qdur field, tried once BOTH
        // TrackArtist and TrackAlbum have failed -- i.e. once the observation has
        // given up on album and artist strings both being trustworthy narrowing
        // fields. See ConfirmAtRungByFrequency below: unlike every other rung, this
        // one's confirmation walk is ordered by artist-recording-frequency within
        // the qdur-narrowed result set, not richness -- duration alone can't
        // disambiguate correctness the way a name/album field can, so frequency
        // (which artist has the most recordings clustered at this title+duration)
        // stands in for it.
        TrackDuration = 4,
        TrackOnly = 5,

        // Composer-tier relationship-scan ladder (§5.1's Composer-tier variant, built
        // 2026-07-15 to close Project Log Outstanding item A) REMOVED 2026-07-19:
        // confirmed dormant (LookupComposerTier had zero callers anywhere in the
        // repo -- RecordingCorroborationEvidenceCollector settled on routing
        // Composer-bucket observations through the same unified Lookup() ladder
        // instead, per its own class doc comment) and FindConfirmedByRelationship
        // was used only by that dead path. Removed as one clean, isolated deletion
        // rather than left flagged, since both zero-caller checks came back clean.
        // Composer-bucket observations' relationship confirmation is handled the
        // same way as every other bucket's: inline GetRelationships scanning inside
        // ConfirmAtRung / ConfirmAtRungByFrequency, within rungs 1-5 above.
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
        // null (defaults false). Only ever true when ConfirmedViaRelationship is
        // false -- the two confirmation paths are mutually exclusive per recording.
        public bool MatchedViaAlias { get; set; }

        // Added 2026-07-18: true when this recording was confirmed via the
        // relationship-scan path (candidate's MBID or a RelationshipMbid found
        // anywhere in the recording's own relationship data) rather than via
        // performer-identity (ArtistMbid==candidate). See class doc comment for the
        // full confirmation-widening rationale.
        public bool ConfirmedViaRelationship { get; set; }

        // Added 2026-07-18: the SPECIFIC relationship entry that confirmed the
        // candidate, when ConfirmedViaRelationship is true (null otherwise). Carried
        // here, on the confirmation result itself, so callers (RecordingCorroboration-
        // EvidenceCollector) don't need a second GetRelationships call/scan just to
        // find out what already confirmed the candidate inside ConfirmAtRung -- that
        // duplication is exactly the vestigial-opportunistic-block confusion flagged
        // 2026-07-18 (a second scan re-deriving what the authorized confirmation path
        // already knew, then mislabeling it as "not required for the decision" when it
        // plainly was). RelationshipType/Level name WHAT kind of relationship and
        // WHERE it lives (Work vs Recording); MatchedViaRelationshipMbid (as opposed
        // to the candidate's own TargetId) is the relationship-evidence equivalent of
        // MatchedViaAlias above -- same "which identity actually matched" question,
        // different mechanism.
        public MbRelationship? ConfirmingRelationship { get; set; }
    }

    /// <summary>
    /// Shared, memoized per-(candidate, track) recording lookup (§7.2 C3/C4).
    /// Used both by SoftBucketStrategy (Stage 2 per-candidate confirmation, §5.3) and
    /// by per-observation evidence collectors (WorkRelationship, CorroborationTier)
    /// that need to confirm a candidate against a specific track.
    ///
    /// Fallback ladder (§7.2/§5.4), as of 2026-07-19: track+artist+album ->
    /// track+artist -> track+album -> title+qdur -> track alone. TrackArtist and
    /// TrackDuration were added 2026-07-19 (see their own rung-enum comments) --
    /// TrackArtist rescues observations where the ALBUM string is unusable (e.g. a
    /// radio-countdown compilation title with no real MB release) while the artist
    /// string is fine; TrackDuration rescues observations where BOTH artist and
    /// album have failed, using MusicBrainz's qdur field plus artist-recording-
    /// frequency ranking in place of a name-based narrowing field. Both are real,
    /// separate rescues -- neither replaces the older rungs, since either the album
    /// or the artist string can independently be the "doozie" in a given
    /// observation. The 2026-07-18 widening below (performer-identity OR
    /// relationship-scan confirmation) applies uniformly across every rung in this
    /// ladder, including the two new ones.
    ///
    /// CONFIRMATION (widened 2026-07-18, settled directive): a recording returned at a
    /// rung is confirmed for a candidate if EITHER (a) the candidate is the recording's
    /// performer (ArtistMbid==candidate, as before -- subject to the existing
    /// NameDistanceEvidenceCollector trust check), OR (b) the candidate's MBID or one
    /// of its RelationshipMbids appears anywhere in the recording's own relationship
    /// data (GetRelationships) -- an exact-MBID match, not a fuzzy one, so this isn't
    /// looser than performer-matching, it's a second equally-strict path for
    /// candidates (e.g. composer-only artists) who structurally can never satisfy (a).
    /// This is why the separate Composer-tier ladder that used to live below (rungs
    /// ComposerBorrowedNameTrackAlbum/ComposerTrackAlbum/ComposerTrackOnly,
    /// LookupComposerTier/ResolveComposerTier) was removed 2026-07-19 -- confirmed
    /// dormant (zero callers anywhere in the repo) once this widened confirmation
    /// check made it fully redundant rather than merely superseded.
    ///
    /// Before either confirmation check runs, candidates at a rung are: (1) GATED on
    /// recording-length vs the observed track's own Duration (a free signal already
    /// present in the search response -- a real 772-recording same-title sample
    /// showed MusicBrainz's own relevance score giving zero disambiguation power, so
    /// duration became the primary identity check instead); missing duration data
    /// does NOT gate a candidate out, only a confirmed mismatch does
    /// (ScoringConfig.DurationGateTolerancePercent/ExcludeRecordingsWithMissingDuration).
    /// Then (2) SORTED by richness heuristics only (Official > Promotion > Bootleg;
    /// studio album preferred over EP/single/compilation/live/bootleg-live; higher
    /// release count) -- richness decides WALK ORDER (which candidate is worth the
    /// cost of a GetRelationships call first), never correctness. The walk stops at
    /// the first confirmed match (early stop, no reason to keep scanning once proven).
    /// Country and release date were considered and deliberately excluded from both
    /// the gate and the sort -- real bias risk against non-Anglophone/older-catalog
    /// entries at 70k-artist scale, with no actual gain in match accuracy.
    ///
    /// Memoization is per RecordingLookup instance, per (candidateMbid, trackId)
    /// pair, for the lifetime of one shared instance — constructed once in
    /// MusicBrainzArtistResolverPlugin and passed to every collector that needs it.
    /// </summary>
    public class RecordingLookup
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly ScoringConfig _config;
        private readonly MetadataHealthCheck.v2.Diagnostics.StructuredLogger? _logger;
        private readonly Dictionary<(string CandidateMbid, string TrackId), RecordingLookupResult> _cache = new();

        // new field, added next to _durationRungCache:
        // Raw-search cache, independent of candidateMbid: the query text for the
        // name/album rungs depends only on the track and which rung is being tried,
        // never on which candidate is being confirmed against the results (see
        // RecordingCorroborationEvidenceCollector's doc comment on rung-1 search
        // text). Two same-named candidates (e.g. two different MBIDs both named
        // "Queen") were each triggering an identical HTTP call per rung before this
        // -- this cache means the second candidate re-evaluates the first's already-
        // fetched result set instead of re-querying MusicBrainz.
        private readonly Dictionary<(string TrackId, RecordingLookupRung Rung), IReadOnlyList<MbRecordingResult>> _nameRungSearchCache = new();
        // Added 2026-07-19: the TrackDuration rung's query (title+qdur) and the
        // artist-frequency tally built from it depend ONLY on the track (title +
        // duration), never on which candidate is being confirmed -- unlike the rest
        // of this class's per-(candidate,track) cache, re-issuing this query once per
        // candidate would be the exact "TrackOnly re-run 25 times, once per
        // candidate" waste flagged earlier in the same investigation. Keyed on
        // TrackId, memoized for the lifetime of this shared instance.
        private readonly Dictionary<string, TrackDurationLookupResult> _durationRungCache = new();

        // logger is optional (nullable) rather than required, 2026-07-16 -- this class
        // predates logger threading through the plugin constructor and existing
        // callers/tests shouldn't be forced to supply one just to keep compiling.
        // scoringConfig ADDED 2026-07-18 (required, not optional): the duration gate
        // and richness sort both need real tunable numbers, and defaulting silently
        // to some hardcoded tolerance here would hide a "suck it and see" knob that
        // belongs in ScoringConfig where the rest of the tunables already live.
        public RecordingLookup(IMusicBrainzApiClient client, ScoringConfig scoringConfig, MetadataHealthCheck.v2.Diagnostics.StructuredLogger? logger = null)
        {
            _client = client;
            _config = scoringConfig;
            _logger = logger;
        }

        /// <param name="candidateMbid">The candidate's MBID being confirmed.</param>
        /// <param name="track">The observed track (carries TrackName/AlbumName/TrackId/Duration).</param>
        /// <param name="artistNames">
        /// The Emby-tagged credited performer name(s) for this credit, used on rungs 1
        /// and 2 only as an OR-group, never reduced to a single name -- a multi-artist
        /// track's credits are all tried together in one query. Pass null/empty if
        /// unavailable -- rungs 1/2 are then skipped, equivalent to starting at rung 3
        /// (track+album).
        /// </param>
        /// <param name="relationshipMbids">
        /// Added 2026-07-18: the candidate's own RelationshipMbids (performs-as/is-person
        /// identities picked up at the artist-search stage). Checked, alongside
        /// candidateMbid itself, during the relationship-scan confirmation path -- see
        /// class doc comment. Optional/empty for callers that don't have this (e.g.
        /// existing tests), in which case relationship-scan confirmation only ever
        /// matches candidateMbid directly.
        /// </param>
        public RecordingLookupResult Lookup(string candidateMbid, EmbyTrackCredit track, IEnumerable<string>? artistNames, IEnumerable<string>? relationshipMbids = null)
        {
            var key = (candidateMbid, track.TrackId);
            if (_cache.TryGetValue(key, out var cached))
            {
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- cache hit (rung already resolved: {2}), no new API call.", candidateMbid, track.TrackName, cached.RungReached);
                return cached;
            }

            var names = (artistNames ?? Enumerable.Empty<string>()).ToList();
            var result = Resolve(candidateMbid, track, names, (relationshipMbids ?? Enumerable.Empty<string>()).ToList());
            _cache[key] = result;
            return result;
        }

        private RecordingLookupResult Resolve(string candidateMbid, EmbyTrackCredit track, IReadOnlyList<string> artistNames, IReadOnlyList<string> relationshipMbids)
        {
            if (artistNames.Count > 0)
            {
                var evaluated = ConfirmAtRung(candidateMbid, relationshipMbids, SearchRecordingCached(track, track.AlbumName, artistNames, RecordingLookupRung.TrackArtistAlbum), track.Duration, RecordingLookupRung.TrackArtistAlbum);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track+artist+album){3}.", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtistAlbum, ConfirmationSuffix(evaluated));
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track+artist+album) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtistAlbum);
            }

            if (artistNames.Count > 0)
            {
                // Added 2026-07-19: rescues observations ...
                var evaluated = ConfirmAtRung(candidateMbid, relationshipMbids, SearchRecordingCached(track, null, artistNames, RecordingLookupRung.TrackArtist), track.Duration, RecordingLookupRung.TrackArtist);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track+artist, no album){3}.", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtist, ConfirmationSuffix(evaluated));
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track+artist, no album) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtist);
            }

            {
                var evaluated = ConfirmAtRung(candidateMbid, relationshipMbids, SearchRecordingCached(track, track.AlbumName, null, RecordingLookupRung.TrackAlbum), track.Duration, RecordingLookupRung.TrackAlbum);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track+album){3}.", candidateMbid, track.TrackName, RecordingLookupRung.TrackAlbum, ConfirmationSuffix(evaluated));
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track+album) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackAlbum);
            }

            if (track.Duration.HasValue)
            {
                // Added 2026-07-19: title+qdur, tried once both name-based rungs
                // (TrackArtist, TrackAlbum) have failed -- i.e. neither the artist
                // nor the album string was usable. Ordered by artist-recording-
                // frequency within the qdur-narrowed set, not richness -- see
                // ConfirmAtRungByFrequency's own doc comment.
                var evaluated = ConfirmAtRungByFrequency(candidateMbid, relationshipMbids, track);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (title+qdur){3}.", candidateMbid, track.TrackName, RecordingLookupRung.TrackDuration, ConfirmationSuffix(evaluated));
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (title+qdur) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackDuration);
            }

            {
                var evaluated = ConfirmAtRung(candidateMbid, relationshipMbids, SearchRecordingCached(track, null, null, RecordingLookupRung.TrackOnly), track.Duration, RecordingLookupRung.TrackOnly);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track alone){3}.", candidateMbid, track.TrackName, RecordingLookupRung.TrackOnly, ConfirmationSuffix(evaluated));
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track alone) missed.", candidateMbid, track.TrackName, RecordingLookupRung.TrackOnly);
            }

            _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- NOT CONFIRMED at any rung of the ladder.", candidateMbid, track.TrackName);
            return new RecordingLookupResult { Recording = null, RungReached = RecordingLookupRung.NotFound };
        }

        // Added 2026-07-18: makes the top-level "CONFIRMED at rung X" line honest about
        // WHICH path actually won (performer-identity vs relationship-scan), and if
        // relationship, what type/level -- this is the single line meant to settle
        // "did the relationship-confirmation path actually fire" at a glance, without
        // cross-referencing the more detailed per-recording relationship-scan log
        // further down (see ConfirmAtRung's own "MATCH:" line for the specific
        // matching MBID and whether it was the candidate's own vs. a RelationshipMbid).
        private static string ConfirmationSuffix(RecordingLookupResult result)
        {
            if (!result.ConfirmedViaRelationship || result.ConfirmingRelationship == null)
                return "";

            var rel = result.ConfirmingRelationship;
            return $" via relationship scan: {rel.RelationshipType}({rel.Level}) -- not performer-identity";
        }

        // Replaces the old FindForCandidate (first-match-by-ArtistMbid) + EvaluateHit
        // pair (2026-07-18 widening). For one rung's raw recording list: gate on
        // duration, sort survivors by richness, then walk in that order checking EACH
        // recording for performer-identity confirmation (as before, still subject to
        // the NameDistance trust check) OR relationship-scan confirmation (new) --
        // stopping at the first one either check confirms. Walking every
        // duration-plausible recording (not just the first, as the old
        // ArtistMbid==candidate filter effectively did) is necessary now: without a
        // performer-name filter narrowing the set, the first recording returned is no
        // longer reliably the right one, so a richness-first walk order plus an
        // early stop on confirmation is what keeps this both accurate and cheap.
        private RecordingLookupResult? ConfirmAtRung(string candidateMbid, IReadOnlyList<string> relationshipMbids, IReadOnlyList<MbRecordingResult> recordings, TimeSpan? observedDuration, RecordingLookupRung rung)
        {
            var survivors = ApplyDurationGate(recordings, observedDuration)
                .OrderBy(r => RichnessRank(r))
                .ToList();

            foreach (var rec in survivors)
            {
                if (rec.ArtistMbid == candidateMbid)
                {
                    var candidateName = _client.GetArtistDisplayName(candidateMbid);
                    var candidateAliases = _client.GetArtistAliases(candidateMbid);
                    var outcome = NameDistanceEvidenceCollector.EvaluateRecordingMatch(candidateName, candidateAliases, rec.ArtistCreditText);
                    if (outcome == NameMatchOutcome.TooPoorToTrust)
                        continue; // not this recording -- keep walking the rung, don't abandon it

                    return new RecordingLookupResult
                    {
                        Recording = rec,
                        RungReached = rung,
                        MatchedViaAlias = outcome == NameMatchOutcome.MatchedViaAlias,
                        ConfirmedViaRelationship = false,
                    };
                }

                // Relationship-scan confirmation (2026-07-18): the candidate isn't the
                // performer on this recording, but may still be confirmed via an exact
                // MBID match anywhere in the recording's own relationship data. No
                // NameDistance trust check here -- ArtistCreditText is the PERFORMER's
                // name, not the candidate's, so that check isn't meaningful for this
                // path; an exact relationship-MBID match is its own safety net.
                _logger?.Info("RecordingLookup", "[{0}] recordingId={1} -- relationship scan for candidate confirmation (rung={2}).", candidateMbid, rec.RecordingId, rung);
                var rels = _client.GetRelationships(rec.RecordingId);
                var confirming = rels.FirstOrDefault(r => r.ArtistMbid == candidateMbid || relationshipMbids.Contains(r.ArtistMbid));
                if (confirming != null)
                {
                    bool viaRelationshipMbid = confirming.ArtistMbid != candidateMbid;
                    _logger?.Info("RecordingLookup", "[{0}] recordingId={1} -- MATCH: {2}({3}) = {4} ({5}).",
                        candidateMbid, rec.RecordingId, confirming.RelationshipType, confirming.Level, confirming.ArtistMbid,
                        viaRelationshipMbid ? "via candidate's RelationshipMbid" : "candidate's own MBID");

                    return new RecordingLookupResult
                    {
                        Recording = rec,
                        RungReached = rung,
                        MatchedViaAlias = false,
                        ConfirmedViaRelationship = true,
                        ConfirmingRelationship = confirming,
                    };
                }
            }

            return null;
        }

        // Cached per-track result of the qdur query: the raw duration-gate survivors
        // (there's no further gating to apply -- qdur already IS the duration
        // constraint) plus the artist-frequency tally built from them, computed once
        // regardless of how many candidates ask for this track.
        private class TrackDurationLookupResult
        {
            public IReadOnlyList<MbRecordingResult> Recordings { get; set; } = Array.Empty<MbRecordingResult>();
            public IReadOnlyList<(string ArtistMbid, int Count)> RankedArtists { get; set; } = Array.Empty<(string, int)>();
        }

        // Added 2026-07-23 (§ "Queen" high-collision-name investigation): result of one
        // ROUND within the multi-candidate confirmation ladder, where a round is either
        // (a) a cheap, no-API-call performer-credit check against every survivor at a
        // rung, run once for ALL still-pending candidates, or (b) one recording's
        // GetRelationships fetch, checked against all still-pending candidates at once.
        // NewlyConfirmed contains ONLY candidates that confirmed in THIS round -- never
        // a running total; the caller (RecordingCorroborationEvidenceCollector) accumulates.
        public class RecordingLookupRoundResult
        {
            public IReadOnlyDictionary<string, RecordingLookupResult> NewlyConfirmed { get; set; } = new Dictionary<string, RecordingLookupResult>();
            public string RoundDescription { get; set; } = "";
        }

        // Added 2026-07-23: multi-candidate, round-based entry point -- replaces calling
        // Lookup() once per candidate for callers (RecordingCorroborationEvidenceCollector)
        // that need every live candidate checked jointly, stopping the moment a caller-side
        // decision gate is satisfied. See IRoundBasedObservationEvidenceCollector's own doc
        // comment for the motivating problem: a per-candidate Lookup() loop was triggering
        // a full relationship-scan walk for EVERY candidate before any stopping decision
        // ran at all. This is `yield return`-based throughout its private helpers -- a
        // caller that stops enumerating early (foreach+break) means the next recording's
        // GetRelationships call genuinely never fires, not merely that its result is
        // discarded.
        //
        // Deliberately NOT memoized via _cache (that cache is single-candidate,
        // single-track keyed) -- multi-candidate round results aren't a natural fit for
        // that cache's shape, and this method's own per-rung raw-search calls already go
        // through SearchRecordingCached/_durationRungCache, which is where the real
        // duplicate-API-call saving lives.
        public IEnumerable<RecordingLookupRoundResult> LookupRounds(IReadOnlyList<string> candidateMbids, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, EmbyTrackCredit track, IEnumerable<string>? artistNames)
        {
            var names = (artistNames ?? Enumerable.Empty<string>()).ToList();
            var pending = new HashSet<string>(candidateMbids);

            if (names.Count > 0)
            {
                foreach (var round in RoundsForRung(RecordingLookupRung.TrackArtistAlbum, SearchRecordingCached(track, track.AlbumName, names, RecordingLookupRung.TrackArtistAlbum), track, pending, relationshipMbidsByCandidate))
                {
                    yield return round;
                    if (pending.Count == 0) yield break;
                }

                foreach (var round in RoundsForRung(RecordingLookupRung.TrackArtist, SearchRecordingCached(track, null, names, RecordingLookupRung.TrackArtist), track, pending, relationshipMbidsByCandidate))
                {
                    yield return round;
                    if (pending.Count == 0) yield break;
                }
            }

            foreach (var round in RoundsForRung(RecordingLookupRung.TrackAlbum, SearchRecordingCached(track, track.AlbumName, null, RecordingLookupRung.TrackAlbum), track, pending, relationshipMbidsByCandidate))
            {
                yield return round;
                if (pending.Count == 0) yield break;
            }

            if (track.Duration.HasValue)
            {
                foreach (var round in RoundsForDurationRung(track, pending, relationshipMbidsByCandidate))
                {
                    yield return round;
                    if (pending.Count == 0) yield break;
                }
            }

            foreach (var round in RoundsForRung(RecordingLookupRung.TrackOnly, SearchRecordingCached(track, null, null, RecordingLookupRung.TrackOnly), track, pending, relationshipMbidsByCandidate))
            {
                yield return round;
                if (pending.Count == 0) yield break;
            }

            // Any candidate still in `pending` here was never confirmed at any rung --
            // the caller should treat any candidateMbid with no NewlyConfirmed entry across
            // every yielded round as RecordingLookupRung.NotFound.
        }

        // One rung's worth of rounds: a single cheap round (performer-credit, no API
        // call) covering every survivor at once, then one expensive round per surviving
        // recording (relationship scan), in richness order, each checked against every
        // still-pending candidate. Mirrors ConfirmAtRung's gating/sorting/confirmation
        // logic exactly -- this is the same confirmation rule, just re-shaped so multiple
        // candidates share each API call instead of each candidate re-deriving it alone.
        private IEnumerable<RecordingLookupRoundResult> RoundsForRung(RecordingLookupRung rung, IReadOnlyList<MbRecordingResult> recordings, EmbyTrackCredit track, HashSet<string> pending, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate)
        {
            var survivors = ApplyDurationGate(recordings, track.Duration).OrderBy(r => RichnessRank(r)).ToList();

            var cheapConfirmed = new Dictionary<string, RecordingLookupResult>();
            foreach (var candidateMbid in pending.ToList())
            {
                foreach (var rec in survivors)
                {
                    if (rec.ArtistMbid != candidateMbid) continue;
                    var candidateName = _client.GetArtistDisplayName(candidateMbid);
                    var candidateAliases = _client.GetArtistAliases(candidateMbid);
                    var outcome = NameDistanceEvidenceCollector.EvaluateRecordingMatch(candidateName, candidateAliases, rec.ArtistCreditText);
                    if (outcome == NameMatchOutcome.TooPoorToTrust) continue;

                    cheapConfirmed[candidateMbid] = new RecordingLookupResult
                    {
                        Recording = rec,
                        RungReached = rung,
                        MatchedViaAlias = outcome == NameMatchOutcome.MatchedViaAlias,
                        ConfirmedViaRelationship = false,
                    };
                    break;
                }
            }
            foreach (var mbid in cheapConfirmed.Keys) pending.Remove(mbid);
            if (cheapConfirmed.Count > 0)
                yield return new RecordingLookupRoundResult { NewlyConfirmed = cheapConfirmed, RoundDescription = $"{rung} (performer-credit, no API call)" };

            if (pending.Count == 0) yield break;

            foreach (var rec in survivors)
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
                    yield return new RecordingLookupRoundResult { NewlyConfirmed = confirmedThisRecording, RoundDescription = $"{rung} (relationship scan, recordingId={rec.RecordingId})" };
            }
        }

        // TrackDuration's round-based variant -- same cheap-then-expensive shape as
        // RoundsForRung, but walked in artist-frequency order (falling back to richness
        // order when the lead isn't meaningful), mirroring ConfirmAtRungByFrequency.
        private IEnumerable<RecordingLookupRoundResult> RoundsForDurationRung(EmbyTrackCredit track, HashSet<string> pending, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate)
        {
            var data = GetOrBuildDurationRungData(track);

            bool leadIsMeaningful = data.RankedArtists.Count >= 1 &&
                (data.RankedArtists.Count == 1 || data.RankedArtists[0].Count - data.RankedArtists[1].Count >= _config.TrackDurationMinArtistLead);

            var rankPosition = data.RankedArtists
                .Select((x, i) => (x.ArtistMbid, Rank: i))
                .ToDictionary(x => x.ArtistMbid, x => x.Rank);

            var survivors = data.Recordings
                .OrderBy(r => leadIsMeaningful && rankPosition.TryGetValue(r.ArtistMbid, out var rank) ? rank : int.MaxValue)
                .ThenBy(r => RichnessRank(r))
                .ToList();

            var cheapConfirmed = new Dictionary<string, RecordingLookupResult>();
            foreach (var candidateMbid in pending.ToList())
            {
                foreach (var rec in survivors)
                {
                    if (rec.ArtistMbid != candidateMbid) continue;
                    var candidateName = _client.GetArtistDisplayName(candidateMbid);
                    var candidateAliases = _client.GetArtistAliases(candidateMbid);
                    var outcome = NameDistanceEvidenceCollector.EvaluateRecordingMatch(candidateName, candidateAliases, rec.ArtistCreditText);
                    if (outcome == NameMatchOutcome.TooPoorToTrust) continue;

                    cheapConfirmed[candidateMbid] = new RecordingLookupResult
                    {
                        Recording = rec,
                        RungReached = RecordingLookupRung.TrackDuration,
                        MatchedViaAlias = outcome == NameMatchOutcome.MatchedViaAlias,
                        ConfirmedViaRelationship = false,
                    };
                    break;
                }
            }
            foreach (var mbid in cheapConfirmed.Keys) pending.Remove(mbid);
            if (cheapConfirmed.Count > 0)
                yield return new RecordingLookupRoundResult { NewlyConfirmed = cheapConfirmed, RoundDescription = "TrackDuration (performer-credit, no API call)" };

            if (pending.Count == 0) yield break;

            foreach (var rec in survivors)
            {
                if (pending.Count == 0) yield break;

                _logger?.Info("RecordingLookup", "recordingId={0} -- relationship scan for {1} remaining candidate(s) (rung=TrackDuration, artist-frequency order).", rec.RecordingId, pending.Count);
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
                        RungReached = RecordingLookupRung.TrackDuration,
                        MatchedViaAlias = false,
                        ConfirmedViaRelationship = true,
                        ConfirmingRelationship = confirming,
                    };
                }
                foreach (var mbid in confirmedThisRecording.Keys) pending.Remove(mbid);
                if (confirmedThisRecording.Count > 0)
                    yield return new RecordingLookupRoundResult { NewlyConfirmed = confirmedThisRecording, RoundDescription = $"TrackDuration (relationship scan, recordingId={rec.RecordingId})" };
            }
        }

        private IReadOnlyList<MbRecordingResult> SearchRecordingCached(EmbyTrackCredit track, string? albumTitle, IEnumerable<string>? artistNames, RecordingLookupRung rung)
        {
            var key = (track.TrackId, rung);
            if (_nameRungSearchCache.TryGetValue(key, out var cached))
            {
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung query cache hit, no new API call.", rung, track.TrackName);
                return cached;
            }

            var results = _client.SearchRecording(track.TrackName, albumTitle, artistNames);
            _nameRungSearchCache[key] = results;
            return results;
        }


        private TrackDurationLookupResult GetOrBuildDurationRungData(EmbyTrackCredit track)
        {
            if (_durationRungCache.TryGetValue(track.TrackId, out var cached))
            {
                _logger?.Debug("RecordingLookup", "[TrackDuration] \"{0}\" -- qdur query cache hit, no new API call.", track.TrackName);
                return cached;
            }

            int observedMs = (int)Math.Round(track.Duration!.Value.TotalMilliseconds);
            var recordings = _client.SearchRecordingByTitleAndDuration(track.TrackName, observedMs, _config.QdurToleranceBuckets);

            // Group by artist MBID (NOT credit text -- see class/method doc comments
            // on why credit-text grouping would fragment real signal across aliases).
            // KNOWN SIMPLIFICATION (flagged, not silently accepted): multi-artist
            // credits only count their first-listed artist, per
            // SearchRecordingByTitleAndDuration's own doc comment -- a genuine duet
            // recording's second artist is invisible to this tally as implemented.
            // Subgroup-relationship folding (e.g. "Queen + Paul Rodgers" -> "Queen")
            // is DELIBERATELY NOT done here -- it would require an extra
            // GetArtistRelationships call per distinct artist MBID in the result set,
            // for artists that are not yet even candidates, which is a real cost/
            // design question left for a follow-up decision, not bundled into this
            // pass.
            var ranked = recordings
                .Where(r => !string.IsNullOrEmpty(r.ArtistMbid))
                .GroupBy(r => r.ArtistMbid)
                .Select(g => (ArtistMbid: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();

            var result = new TrackDurationLookupResult { Recordings = recordings, RankedArtists = ranked };
            _durationRungCache[track.TrackId] = result;

            _logger?.Info("RecordingLookup", "[TrackDuration] \"{0}\" -- {1} recording(s) within qdur window, {2} distinct artist(s), leader={3} (count={4}).",
                track.TrackName, recordings.Count, ranked.Count,
                ranked.Count > 0 ? ranked[0].ArtistMbid : "(none)",
                ranked.Count > 0 ? ranked[0].Count : 0);

            return result;
        }

        // The TrackDuration rung's confirmation walk (§7.2 "Bohemian Rhapsody" trace,
        // artist-frequency proposal): unlike ConfirmAtRung's richness-ordered walk,
        // this orders the duration-narrowed result set by artist-recording-frequency
        // (which artist has the most recordings clustered at this title+duration --
        // a real cover is typically a one-off, while the correct artist for a
        // well-covered song tends to have many: studio + live + reissues). Frequency
        // is only trusted as a signal if the leader clears
        // ScoringConfig.TrackDurationMinArtistLead over the second-place artist;
        // otherwise this rung falls through to TrackOnly exactly as if it had found
        // nothing, rather than acting on a meaningless 1-vs-0 "lead". Still routes
        // through the same ArtistMbid/relationship-scan confirmation checks as every
        // other rung -- frequency changes WALK ORDER only, never bypasses
        // confirmation itself.
        private RecordingLookupResult? ConfirmAtRungByFrequency(string candidateMbid, IReadOnlyList<string> relationshipMbids, EmbyTrackCredit track)
        {
            var data = GetOrBuildDurationRungData(track);

            bool leadIsMeaningful = data.RankedArtists.Count >= 1 &&
                (data.RankedArtists.Count == 1 || data.RankedArtists[0].Count - data.RankedArtists[1].Count >= _config.TrackDurationMinArtistLead);

            if (!leadIsMeaningful)
            {
                _logger?.Debug("RecordingLookup", "[TrackDuration] \"{0}\" -- artist-frequency lead below TrackDurationMinArtistLead ({1}), not trusting frequency ordering; falling through to richness order.",
                    track.TrackName, _config.TrackDurationMinArtistLead);
            }

            var rankPosition = data.RankedArtists
                .Select((x, i) => (x.ArtistMbid, Rank: i))
                .ToDictionary(x => x.ArtistMbid, x => x.Rank);

            var survivors = data.Recordings
                .OrderBy(r => leadIsMeaningful && rankPosition.TryGetValue(r.ArtistMbid, out var rank) ? rank : int.MaxValue)
                .ThenBy(r => RichnessRank(r))
                .ToList();

            foreach (var rec in survivors)
            {
                if (rec.ArtistMbid == candidateMbid)
                {
                    var candidateName = _client.GetArtistDisplayName(candidateMbid);
                    var candidateAliases = _client.GetArtistAliases(candidateMbid);
                    var outcome = NameDistanceEvidenceCollector.EvaluateRecordingMatch(candidateName, candidateAliases, rec.ArtistCreditText);
                    if (outcome == NameMatchOutcome.TooPoorToTrust)
                        continue;

                    return new RecordingLookupResult
                    {
                        Recording = rec,
                        RungReached = RecordingLookupRung.TrackDuration,
                        MatchedViaAlias = outcome == NameMatchOutcome.MatchedViaAlias,
                        ConfirmedViaRelationship = false,
                    };
                }

                _logger?.Info("RecordingLookup", "[{0}] recordingId={1} -- relationship scan for candidate confirmation (rung={2}, artist-frequency order).", candidateMbid, rec.RecordingId, RecordingLookupRung.TrackDuration);
                var rels = _client.GetRelationships(rec.RecordingId);
                var confirming = rels.FirstOrDefault(r => r.ArtistMbid == candidateMbid || relationshipMbids.Contains(r.ArtistMbid));
                if (confirming != null)
                {
                    return new RecordingLookupResult
                    {
                        Recording = rec,
                        RungReached = RecordingLookupRung.TrackDuration,
                        MatchedViaAlias = false,
                        ConfirmedViaRelationship = true,
                        ConfirmingRelationship = confirming,
                    };
                }
            }

            return null;
        }

        // Duration gate (§ settled directive 2026-07-18): keeps a recording if its own
        // length is unknown (missing data is NOT a disqualification) or within
        // ScoringConfig.DurationGateTolerancePercent of the observed track's Duration.
        // If the observed track itself has no known Duration, the gate can't do
        // anything meaningful -- every recording passes through unfiltered rather than
        // guessing.
        private IEnumerable<MbRecordingResult> ApplyDurationGate(IReadOnlyList<MbRecordingResult> recordings, TimeSpan? observedDuration)
        {
            if (!observedDuration.HasValue)
                return recordings;

            var observedMs = observedDuration.Value.TotalMilliseconds;
            return recordings.Where(r =>
            {
                if (!r.LengthMs.HasValue)
                    return !_config.ExcludeRecordingsWithMissingDuration;

                var diffMs = Math.Abs(r.LengthMs.Value - observedMs);
                return diffMs <= observedMs * _config.DurationGateTolerancePercent;
            });
        }

        // Richness ranking (§ settled directive 2026-07-18): WALK ORDER among
        // duration-gate survivors only -- decides which recording is worth spending
        // the first relationship-fetch call scanning, never correctness. Lower rank
        // sorts first. Deliberately excludes country and release date/age -- real bias
        // risk against non-Anglophone/older-catalog entries at 70k-artist scale, with
        // no gain in match accuracy (see class doc comment).
        private static int RichnessRank(MbRecordingResult r)
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
            // Secondary types (Live/Compilation/Bootleg-adjacent) push an otherwise
            // studio-looking release further back -- e.g. a "Live" Album is worth less
            // than a plain studio Album for relationship-data richness purposes.
            if (r.ReleaseGroupSecondaryTypes.Contains("Compilation", StringComparer.OrdinalIgnoreCase)
                || r.ReleaseGroupSecondaryTypes.Contains("Live", StringComparer.OrdinalIgnoreCase))
            {
                typeRank += 2;
            }

            // Combine into one sortable integer: status dominates, then type, then
            // (inverted, since higher release count is better) release count, capped
            // so it can't ever outrank a status/type difference.
            int releaseCountRank = Math.Max(0, 100 - r.ReleaseCount);
            return statusRank * 10000 + typeRank * 1000 + releaseCountRank;
        }
        // LookupComposerTier / ResolveComposerTier / FindConfirmedByRelationship
        // REMOVED 2026-07-19: confirmed zero callers anywhere in the repo
        // (RecordingCorroborationEvidenceCollector settled on routing Composer-bucket
        // observations through the unified Lookup() ladder instead -- see its own
        // class doc comment) and FindConfirmedByRelationship was used only by that
        // dead path. Composer-bucket relationship confirmation is handled the same
        // way as every other bucket's: inline GetRelationships scanning inside
        // ConfirmAtRung / ConfirmAtRungByFrequency above.
        //
        // The one thing this method used to add beyond the unified ladder -- a
        // "borrowed-name" rung (trying a co-credited real performer's name as search
        // text before falling back to track+album/track-alone) -- was NOT folded into
        // the unified ladder before this deletion, and remains a real, not-yet-decided
        // design question: is a borrowed-name rung worth adding there, or does
        // duration-gating + richness/frequency-ordering already make it unnecessary.
        // Flagging this explicitly so the idea isn't lost along with the dead code
        // that used to carry it.
    }
}