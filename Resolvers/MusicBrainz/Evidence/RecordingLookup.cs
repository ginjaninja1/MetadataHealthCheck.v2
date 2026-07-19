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
    /// Fallback ladder (§7.2/§5.4): track+artist+album -> track+album -> track alone.
    /// This ladder is UNCHANGED by the 2026-07-18 widening below -- it was never
    /// performer-specific to begin with (plain track/album text search), so there was
    /// nothing to loosen there. What changed is the CONFIRMATION criterion applied to
    /// whatever a rung returns.
    ///
    /// CONFIRMATION (widened 2026-07-18, settled directive): a recording returned at a
    /// rung is confirmed for a candidate if EITHER (a) the candidate is the recording's
    /// performer (ArtistMbid==candidate, as before -- subject to the existing
    /// NameDistanceEvidenceCollector trust check), OR (b) the candidate's MBID or one
    /// of its RelationshipMbids appears anywhere in the recording's own relationship
    /// data (GetRelationships) -- an exact-MBID match, not a fuzzy one, so this isn't
    /// looser than performer-matching, it's a second equally-strict path for
    /// candidates (e.g. composer-only artists) who structurally can never satisfy (a).
    /// This is why LookupComposerTier below is now largely superseded rather than
    /// still required -- see its own doc comment.
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
        /// <param name="artistName">
        /// The Emby-tagged display name for this credit, used on rung 1 only. Pass
        /// null if unavailable — rung 1 is then equivalent to rung 2.
        /// </param>
        /// <param name="relationshipMbids">
        /// Added 2026-07-18: the candidate's own RelationshipMbids (performs-as/is-person
        /// identities picked up at the artist-search stage). Checked, alongside
        /// candidateMbid itself, during the relationship-scan confirmation path -- see
        /// class doc comment. Optional/empty for callers that don't have this (e.g.
        /// existing tests), in which case relationship-scan confirmation only ever
        /// matches candidateMbid directly.
        /// </param>
        public RecordingLookupResult Lookup(string candidateMbid, EmbyTrackCredit track, string? artistName, IEnumerable<string>? relationshipMbids = null)
        {
            var key = (candidateMbid, track.TrackId);
            if (_cache.TryGetValue(key, out var cached))
            {
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- cache hit (rung already resolved: {2}), no new API call.", candidateMbid, track.TrackName, cached.RungReached);
                return cached;
            }

            var result = Resolve(candidateMbid, track, artistName, (relationshipMbids ?? Enumerable.Empty<string>()).ToList());
            _cache[key] = result;
            return result;
        }

        private RecordingLookupResult Resolve(string candidateMbid, EmbyTrackCredit track, string? artistName, IReadOnlyList<string> relationshipMbids)
        {
            if (artistName != null)
            {
                var evaluated = ConfirmAtRung(candidateMbid, relationshipMbids, _client.SearchRecording(track.TrackName, track.AlbumName, artistName), track.Duration, RecordingLookupRung.TrackArtistAlbum);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track+artist+album){3}.", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtistAlbum, ConfirmationSuffix(evaluated));
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track+artist+album) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackArtistAlbum);
            }

            {
                var evaluated = ConfirmAtRung(candidateMbid, relationshipMbids, _client.SearchRecording(track.TrackName, track.AlbumName, null), track.Duration, RecordingLookupRung.TrackAlbum);
                if (evaluated != null)
                {
                    _logger?.Info("RecordingLookup", "[{0}] \"{1}\" -- CONFIRMED at rung {2} (track+album){3}.", candidateMbid, track.TrackName, RecordingLookupRung.TrackAlbum, ConfirmationSuffix(evaluated));
                    return evaluated;
                }
                _logger?.Debug("RecordingLookup", "[{0}] \"{1}\" -- rung {2} (track+album) missed, falling through.", candidateMbid, track.TrackName, RecordingLookupRung.TrackAlbum);
            }

            {
                var evaluated = ConfirmAtRung(candidateMbid, relationshipMbids, _client.SearchRecording(track.TrackName, null, null), track.Duration, RecordingLookupRung.TrackOnly);
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

        /// <summary>
        /// SUPERSEDED 2026-07-18 by the widened Lookup() above -- flagged, not deleted
        /// (Nick's stated preference: implement/flag contradictions, don't silently
        /// resolve them). Lookup() now performs relationship-scan confirmation at
        /// EVERY rung, for every candidate, so composer-only candidates already get
        /// confirmed by the ordinary Lookup() call without any Composer-specific
        /// branching. The ONE thing this method still adds beyond that is the
        /// "borrowed-name" rung (trying a co-credited real performer's name as search
        /// text before falling back to track+album/track-alone) -- that idea hasn't
        /// been folded into Lookup() and remains a real, not-yet-decided design
        /// question: is a borrowed-name rung worth adding to the unified ladder, or
        /// does duration-gating + richness-ordering already make it unnecessary. Left
        /// dormant pending that decision; do not wire this back in without one.
        ///
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
        /// NOTE: unlike Lookup()'s new ConfirmAtRung, this method's own
        /// FindConfirmedByRelationship below does NOT apply the duration gate or
        /// richness-ordered walk added 2026-07-18 -- it predates both and was not
        /// updated since it's dormant. If this is ever revived, it should be
        /// rewritten to share ConfirmAtRung's logic rather than duplicating an
        /// unwidened version of it.
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