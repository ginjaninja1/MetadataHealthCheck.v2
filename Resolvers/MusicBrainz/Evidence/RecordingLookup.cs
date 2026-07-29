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
        // fields. See RoundsForDurationRung below: unlike every other rung, this
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
        // RoundsForRung / RoundsForDurationRung, within rungs 1-5 above.
    }

    /// <summary>
    /// Added 2026-07-27: which round-type(s) a rung's confirmation walk is allowed
    /// to run, decided by the caller (RecordingCorroborationEvidenceCollector) from
    /// the observation's bucket (AlbumArtist/Artist/Composer), NOT a per-track or
    /// per-candidate choice -- every rung in the ladder for a given observation runs
    /// under the same mode.
    ///
    /// Motivating problem: a single unified confirmation walk (cheap performer-
    /// credit round, then relationship-scan round, every rung, every bucket) meant
    /// a MusicBrainz-high-scoring PERFORMER candidate could confirm and stop the
    /// sampler before a genuinely correct COMPOSER candidate's relationship match
    /// was ever attempted for that same observation. Composer-bucket observations
    /// structurally can never confirm via performer-credit in the first place (a
    /// composer isn't the recording's performer) -- running that round for them was
    /// always wasted work, not a real chance at a hit.
    ///
    /// PerformerOnly: run the cheap performer-credit round only; never call
    /// GetRelationships for this observation. Used for AlbumArtist/Artist buckets.
    /// RelationshipOnly: skip the cheap round; go straight to the relationship-scan
    /// round for every rung. Used for the Composer bucket.
    ///
    /// The original combined behavior (both rounds, unconditionally) is walled off
    /// rather than deleted -- see RoundsForRung/RoundsForDurationRung's own comments
    /// -- since this split is explicitly experimental (per Nick, 2026-07-27) and the
    /// two-round shape may need to be recombined or re-tuned once real data comes in.
    /// </summary>
    public enum ConfirmationMode
    {
        PerformerOnly,
        RelationshipOnly,
    }

    public class RecordingLookupResult
    {
        public MbRecordingResult? Recording { get; set; }
        public RecordingLookupRung RungReached { get; set; } = RecordingLookupRung.NotFound;

        // Added 2026-07-13: originally set from NameDistanceEvidenceCollector.
        // EvaluateRecordingMatch to distinguish a match against the candidate's
        // primary MB name vs. a registered alias. Removed 2026-07-28 (settled
        // directive): confirmation is by MBID equality, which has no "primary name
        // vs. alias" distinction to report, so this is now always false for
        // performer-credit confirmations. Left in place (rather than deleted) since
        // ScoringConfig.NameMatchWeight/AliasMatchWeight still reference it at
        // scoring time (§5.3/§6.3) -- removing the field is a separate scoring-config
        // decision, not a side effect of this bugfix.
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
        // find out what already confirmed the candidate inside the confirmation walk -- that
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
    /// Shared recording lookup (§7.2 C3/C4) used by the multi-candidate, round-based
    /// entry point (LookupRounds) -- see its own doc comment further down. The
    /// original single-candidate entry point (Lookup/Resolve, one call per candidate)
    /// was removed 2026-07-26: its only remaining callers were the three dormant
    /// evidence collectors (WorkRelationship/CorroborationTier/RecordingRelationship),
    /// all excluded from the build, so it was dead code.
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
    /// performer -- ArtistMbid==candidate, OR (as of 2026-07-28) candidate found in
    /// ReleaseAlbumArtistMbids (release-level "album artist" credit, mined across ALL
    /// releases the recording appears on -- see MbRecordingResult doc comment). Both
    /// are exact MBID matches; the name-similarity trust check that used to gate this
    /// path was removed 2026-07-28 as redundant/harmful once confirmation is by MBID
    /// equality (see EvidenceRecord.MatchedViaAlias doc comment). OR (b) the
    /// candidate's MBID or one
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
    ///
    /// EXPERIMENTAL SPLIT, 2026-07-27: LookupRounds now takes a ConfirmationMode
    /// (see its own doc comment) that restricts EACH rung above to only one of the
    /// two round-types described above -- performer-credit-only for AlbumArtist/
    /// Artist bucket observations, relationship-scan-only for Composer bucket
    /// observations. The rung ladder, duration gate, richness/frequency ordering,
    /// and early-stop-on-confirmation behavior are identical either way; only which
    /// confirmation check(s) run at each rung changes. This is walled off behind the
    /// mode parameter rather than removed, since it's explicitly experimental.
    /// </summary>
    public class RecordingLookup
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly ScoringConfig _config;
        private readonly MetadataHealthCheck.v2.Diagnostics.StructuredLogger? _logger;
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

        // Added 2026-07-23: multi-candidate, round-based entry point -- checks every
        // still-pending candidate jointly, one rung at a time, stopping the moment a
        // caller-side decision gate is satisfied. See IRoundBasedObservationEvidence-
        // Collector's own doc comment for the motivating problem: an earlier
        // per-candidate lookup loop was triggering a full relationship-scan walk for
        // EVERY candidate before any stopping decision ran at all. This is
        // `yield return`-based throughout its private helpers -- a caller that stops
        // enumerating early (foreach+break) means the next recording's
        // GetRelationships call genuinely never fires, not merely that its result is
        // discarded.
        //
        // The single-candidate Lookup()/Resolve() path (and its per-(candidate,track)
        // cache) that predated this was removed 2026-07-26: its only remaining callers
        // were WorkRelationshipEvidenceCollector/CorroborationTierEvidenceCollector/
        // RecordingRelationshipEvidenceCollector, all three of which are excluded from
        // the build (`<Compile Remove>` in the .csproj), so it was genuinely dead code.
        // Not memoized -- this method's own per-rung raw-search calls already go
        // through SearchRecordingCached/_durationRungCache, which is where the real
        // duplicate-API-call saving lives.
        public IEnumerable<RecordingLookupRoundResult> LookupRounds(IReadOnlyList<string> candidateMbids, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, EmbyTrackCredit track, IEnumerable<string>? artistNames, ConfirmationMode mode)
        {
            var names = (artistNames ?? Enumerable.Empty<string>()).ToList();
            var pending = new HashSet<string>(candidateMbids);

            // Settled directive 2026-07-26 (same rule as the single-candidate Resolve()
            // path): only advance to the next rung's query when THIS rung's raw search
            // came back with zero recordings. If MusicBrainz returned recording(s) here,
            // the search succeeded -- any pending candidate this rung's rounds don't
            // confirm is a genuine non-match, not a reason to try a looser query, so we
            // stop the whole ladder rather than falling through.

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
                var data = GetOrBuildDurationRungData(track);
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

            // Any candidate still in `pending` here was never confirmed at any rung --
            // the caller should treat any candidateMbid with no NewlyConfirmed entry across
            // every yielded round as RecordingLookupRung.NotFound.
        }

        // One rung's worth of rounds: a single cheap round (performer-credit, no API
        // call) covering every survivor at once, then one expensive round per surviving
        // recording (relationship scan), in richness order, each checked against every
        // still-pending candidate. Same gating/sorting/confirmation rule as every other
        // rung, just re-shaped so multiple candidates share each API call instead of
        // each candidate re-deriving it alone.
        // ConfirmationMode parameter added 2026-07-27 (experimental AlbumArtist/Artist
        // vs Composer pathway split -- see ConfirmationMode's own doc comment). The
        // cheap performer-credit round and the relationship-scan round are both left
        // completely intact below, just each wrapped in a mode guard: WALLED OFF, not
        // removed, per explicit instruction, since this split may need to be undone or
        // re-tuned once real data comes in. PerformerOnly skips straight past the
        // relationship-scan loop (no GetRelationships call fires at all for this rung);
        // RelationshipOnly skips the cheap loop entirely and goes straight to the scan.
        private IEnumerable<RecordingLookupRoundResult> RoundsForRung(RecordingLookupRung rung, IReadOnlyList<MbRecordingResult> recordings, EmbyTrackCredit track, HashSet<string> pending, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, ConfirmationMode mode)
        {
            var survivors = ApplyDurationGate(recordings, track.Duration).OrderBy(r => RichnessRank(r)).ToList();

            // Added 2026-07-27, diagnostic only: confirms the actual scan order RichnessRank
            // produced, since the terse per-recording debug print elsewhere (Status/Type/
            // Releases) doesn't show ReleaseGroupSecondaryTypes, which also feeds the rank.
            // Remove once the ordering discrepancy under investigation is settled.
            if (survivors.Count > 1)
            {
                _logger?.Debug("RecordingLookup", "[{0}] richness order for {1} duration-gate survivor(s):", rung, survivors.Count);
                foreach (var s in survivors)
                {
                    _logger?.Debug("RecordingLookup", "     rank={0,4}  recordingId={1}  status={2}  type={3}  secondaryTypes=[{4}]  releases={5}  albumArtist={6}",
                        RichnessRank(s), s.RecordingId, s.ReleaseStatus ?? "(none)", s.ReleaseGroupPrimaryType ?? "(none)",
                        string.Join(", ", s.ReleaseGroupSecondaryTypes), s.ReleaseCount, s.ReleaseAlbumArtistCreditText ?? "(none)");
                }
            }

            if (mode == ConfirmationMode.PerformerOnly)
            {
                var cheapConfirmed = new Dictionary<string, RecordingLookupResult>();
                foreach (var candidateMbid in pending.ToList())
                {
                    foreach (var rec in survivors)
                    {
                        // Match either the recording's own (track-level) artist credit,
                        // or ANY release-level artist credit ("album artist") this
                        // recording carries across all its releases -- see
                        // MbRecordingResult.ReleaseAlbumArtistMbids doc comment. Fixes a
                        // bug where a candidate correctly credited as album artist on a
                        // non-representative release was never matched.
                        // Bugfix 2026-07-28: was checking rec.ArtistMbid (first-credited
                        // artist ONLY), which silently dropped any candidate credited
                        // 2nd/3rd/etc on a multi-artist recording (e.g. "Gorillaz feat.
                        // Mos Def & Bobby Womack" -- Mos Def and Bobby Womack were
                        // invisible to confirmation). Now checks ArtistMbids, which
                        // holds every credited artist's MBID.
                        bool matchedViaTrackArtist = rec.ArtistMbids.Contains(candidateMbid);
                        bool matchedViaReleaseAlbumArtist = !matchedViaTrackArtist
                            && rec.ReleaseAlbumArtistMbids.Contains(candidateMbid);
                        if (!matchedViaTrackArtist && !matchedViaReleaseAlbumArtist) continue;

                        // Removed 2026-07-28 (settled directive): a name-similarity check
                        // used to run here against the matched credit text. MBID equality
                        // above already IS the identity confirmation -- MBIDs are unique,
                        // stable identities, so there was no remaining "wrong artist"
                        // scenario left for a fuzzy text check to catch. In practice it
                        // only ever had one effect: silently rejecting a correct MBID
                        // match when the compared credit text happened to read
                        // differently (e.g. a candidate confirmed via release-level
                        // album-artist credit, checked against the unrelated
                        // recording-level credit text). MatchedViaAlias is therefore
                        // always false here now -- it described a property of the
                        // removed text check, not of ID-based confirmation.
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
                {
                    yield return new RecordingLookupRoundResult { NewlyConfirmed = confirmedThisRecording, RoundDescription = $"{rung} (relationship scan, recordingId={rec.RecordingId})" };

                    // Settled directive 2026-07-28: STOP here, do not advance to the next
                    // recording, even if candidates remain pending. Every still-pending
                    // candidate was already checked against THIS recording (for free,
                    // same GetRelationships call) and did not confirm -- that is a real
                    // "no match at this rung" result for them, not "not yet tried". Any
                    // candidate that only matched a LATER recording in richness order
                    // would be matching a different recording than the one that already
                    // confirmed someone else, which is a false-positive risk (cover
                    // version, reissue with different personnel), not corroboration of
                    // the same fact. Remaining pending candidates simply get another
                    // chance from the top rung on the next observation (next track).
                    yield break;
                }
            }
        }

        // TrackDuration's round-based variant -- same cheap-then-expensive shape as
        // RoundsForRung, but walked in artist-frequency order (falling back to richness
        // order when the lead isn't meaningful).
        // ConfirmationMode parameter added 2026-07-27 -- see RoundsForRung's comment
        // directly above; same walled-off gating applied here, not repeated in full.
        private IEnumerable<RecordingLookupRoundResult> RoundsForDurationRung(EmbyTrackCredit track, HashSet<string> pending, IReadOnlyDictionary<string, IReadOnlyList<string>> relationshipMbidsByCandidate, ConfirmationMode mode)
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

            if (mode == ConfirmationMode.PerformerOnly)
            {
                var cheapConfirmed = new Dictionary<string, RecordingLookupResult>();
                foreach (var candidateMbid in pending.ToList())
                {
                    foreach (var rec in survivors)
                    {
                        // Match either the recording's own (track-level) artist credit --
                        // ANY credited artist, per ArtistMbids, not just the first-listed
                        // one (bugfix 2026-07-28, see companion comment at the rung-based
                        // confirmation block above) -- or ANY release-level artist credit
                        // ("album artist") this recording carries across all its releases,
                        // see MbRecordingResult.ReleaseAlbumArtistMbids doc comment.
                        bool matchedViaTrackArtist = rec.ArtistMbids.Contains(candidateMbid);
                        bool matchedViaReleaseAlbumArtist = !matchedViaTrackArtist
                            && rec.ReleaseAlbumArtistMbids.Contains(candidateMbid);
                        if (!matchedViaTrackArtist && !matchedViaReleaseAlbumArtist) continue;

                        // Removed 2026-07-28 (settled directive): see companion comment
                        // at the rung-based confirmation block above -- the name-check
                        // that used to run here is gone; MBID equality above is the
                        // confirmation.
                        cheapConfirmed[candidateMbid] = new RecordingLookupResult
                        {
                            Recording = rec,
                            RungReached = RecordingLookupRung.TrackDuration,
                            MatchedViaAlias = false,
                            ConfirmedViaRelationship = false,
                        };
                        break;
                    }
                }
                foreach (var mbid in cheapConfirmed.Keys) pending.Remove(mbid);
                if (cheapConfirmed.Count > 0)
                    yield return new RecordingLookupRoundResult { NewlyConfirmed = cheapConfirmed, RoundDescription = "TrackDuration (performer-credit, no API call)" };

                if (pending.Count == 0) yield break;
            }

            if (mode != ConfirmationMode.RelationshipOnly) yield break;

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
                {
                    yield return new RecordingLookupRoundResult { NewlyConfirmed = confirmedThisRecording, RoundDescription = $"TrackDuration (relationship scan, recordingId={rec.RecordingId})" };

                    // Settled directive 2026-07-28 -- see companion comment on
                    // RoundsForRung's own relationship-scan loop above: stop the walk
                    // the moment any recording yields a confirmation, rather than
                    // advancing to the next recording for still-pending candidates.
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


        private TrackDurationLookupResult GetOrBuildDurationRungData(EmbyTrackCredit track)
        {
            if (_durationRungCache.TryGetValue(track.TrackId, out var cached))
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

        // Duration gate (§ settled directive 2026-07-18): keeps a recording if its own
        // length is unknown (missing data is NOT a disqualification) or within
        // ScoringConfig.DurationGateTolerancePercent of the observed track's Duration.
        // If the observed track itself has no known Duration, the gate can't do
        // anything meaningful -- every recording passes through unfiltered rather than
        // guessing.
        private IEnumerable<MbRecordingResult> ApplyDurationGate(IReadOnlyList<MbRecordingResult> recordings, TimeSpan? observedDuration)
        {
            if (!_config.EnableRecordingDurationGate)
                return recordings;

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

        // MusicBrainz's canonical "Various Artists" artist MBID -- fixed across the
        // whole database, not a per-install config value. Used to detect genuine
        // Various-Artists compilations (settled directive 2026-07-28, see RichnessRank).
        private const string VariousArtistsMbid = "89ad4ac3-39f7-470e-963a-56509c546377";

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
            // Compilation is the one exception (settled directive 2026-07-28): the
            // penalty exists because Various-Artists compilations were estimated to be
            // less likely to carry populated relationship data -- not because
            // "Compilation" itself implies a lesser release. An artist's OWN
            // compilation (e.g. a reissue/anthology) is just as likely to be
            // well-indexed as a studio album, so the penalty only applies when the
            // release's own artist credit is actually "Various Artists".
            bool isVariousArtistsCompilation =
                r.ReleaseGroupSecondaryTypes.Contains("Compilation", StringComparer.OrdinalIgnoreCase)
                && (r.ReleaseAlbumArtistMbid == VariousArtistsMbid
                    || (r.ReleaseAlbumArtistCreditText ?? "").Trim().Equals("Various Artists", StringComparison.OrdinalIgnoreCase));

            bool isLive = r.ReleaseGroupSecondaryTypes.Contains("Live", StringComparer.OrdinalIgnoreCase);

            if (isVariousArtistsCompilation || isLive)
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
        // RoundsForRung / RoundsForDurationRung above.
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