using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence
{
    /// <summary>
    /// SUPERSEDES CorroborationTierEvidenceCollector, WorkRelationshipEvidenceCollector,
    /// and RecordingRelationshipEvidenceCollector (built 2026-07-17, per direct
    /// instruction after a design conversation). Those three files are left in the
    /// repo unmodified but are no longer wired into MusicBrainzArtistResolverPlugin —
    /// not deleted, in case anchoring/composer-tier work later needs to reference
    /// their logic, but they should be treated as dead code until a decision is made
    /// to revive one of them.
    ///
    /// WHY THIS COLLAPSE HAPPENED: the three collectors above each independently
    /// decided how to call the shared RecordingLookup for the same (candidate, track)
    /// pair -- two passed artistName: source.DisplayName, one passed artistName: null,
    /// and all three branched into a separate LookupComposerTier() ladder for
    /// Composer-role tracks. Because RecordingLookup's cache was keyed only on
    /// (candidateMbid, TrackId) -- with no distinction for which method or which
    /// arguments produced the cached answer -- whichever collector ran first for a
    /// given (candidate, track) silently decided the answer for the other two, and
    /// Lookup() / LookupComposerTier() could stomp on each other's cache entries for
    /// the very same key. This was a real correctness bug, not just a style
    /// inconsistency (see "AlbumArtist observation starts without artist" in the
    /// Project Log).
    ///
    /// SETTLED DIRECTIVE (2026-07-17): "is this recording confirmed for this
    /// candidate" is ONE factual question per (candidate, observation), not one per
    /// collector. So there is now exactly one RecordingLookup.Lookup() call per
    /// Collect() invocation, always with artistName: source.DisplayName, for EVERY
    /// observation Role (AlbumArtist/Artist/Composer alike) -- no branching into
    /// LookupComposerTier().
    ///
    /// UPDATED 2026-07-18: Lookup() itself was widened so this single call now also
    /// confirms via relationship-scan (candidate's MBID/RelationshipMbids found
    /// anywhere in a recording's own relationship data), not just via
    /// performer-identity. So a Composer-role observation for a candidate who is
    /// never a recording's performer is no longer guaranteed to come back NotFound --
    /// it can now be confirmed directly, which is the intended fix for composer-only
    /// candidates (Gus Black/Del Serino real-world cases) producing no evidence at
    /// all. LookupComposerTier (and the borrowed-name rung it alone provided) was
    /// removed from RecordingLookup.cs 2026-07-19, confirmed to have zero callers
    /// anywhere in the repo. The borrowed-name idea itself was NOT folded into the
    /// unified ladder before that deletion and remains a real, not-yet-decided
    /// question -- flagged in RecordingLookup.cs's own comment where the dead code
    /// used to live, not lost along with it.
    ///
    /// REMOVED 2026-07-18 (Nick's explicit instruction): this collector used to also
    /// emit WorkRelationship.*/RecordingRelationship.* evidence (Contributing=false)
    /// from a SECOND GetRelationships call made after confirmation, purely to log
    /// "would this have mattered" data. That block is gone. It was: (a) genuinely
    /// vestigial noise once ConfirmAtRung started doing its own relationship scan as
    /// part of confirmation itself, and (b) actively misleading -- its
    /// "---- opportunistic lookup ... not required for the decision ----" log wrapper
    /// was flat-out false whenever ConfirmedViaRelationship was true, since in that
    /// case the relationship data WAS the decision. See RecordingLookup.cs's
    /// ConfirmingRelationship/ConfirmAtRung for where relationship-scan confirmation
    /// now lives, and its own "MATCH:" log line for the authorized version of what this
    /// block used to (re-)derive.
    ///
    /// This collector now emits exactly one evidence family, CorroborationTier.*, but
    /// its rationale honestly states whether confirmation came via performer-credit or
    /// via a relationship (and if the latter, which type/level) -- see the comment at
    /// that yield return below.
    /// </summary>
    public class RecordingCorroborationEvidenceCollector : IObservationEvidenceCollector<EmbyArtist>
    {
        private readonly IMusicBrainzApiClient _client;
        private readonly RecordingLookup _recordingLookup;
        private readonly MetadataHealthCheck.v2.Diagnostics.StructuredLogger? _logger;

        // _client and _logger are no longer called directly from Collect() as of
        // 2026-07-18 (removal of the vestigial post-confirmation relationship-scan
        // block -- see class doc comment). Left as constructor params/fields rather
        // than removed: RecordingLookup itself now owns all relationship-scan calls
        // and their logging (ConfirmAtRung), and MusicBrainzArtistResolverPlugin's
        // wiring still constructs this collector with both -- removing them would be
        // a bigger, separate wiring change for no present benefit, and a future
        // decision to add collector-level logging/relationship calls back would want
        // them anyway.

        public RecordingCorroborationEvidenceCollector(IMusicBrainzApiClient client, RecordingLookup recordingLookup, MetadataHealthCheck.v2.Diagnostics.StructuredLogger? logger = null)
        {
            _client = client;
            _recordingLookup = recordingLookup;
            _logger = logger;
        }

        // Reports CorroborationTier.* only, as of the 2026-07-18 vestigial-block
        // removal (see class doc comment) -- this property is descriptive only.
        public string EvidenceType => "RecordingCorroboration";

        // The literal set this collector currently emits (see the tier-classification
        // switch in Collect() below). NOTE 2026-07-19: this list reflects CURRENT code,
        // which has a known, separately-flagged bug -- the switch was never updated
        // when the TrackArtist/TrackDuration rungs were added, so both fall into the
        // Tier3 catch-all rather than getting their own weight. Update this list
        // in lockstep with that switch once the tier-count/weighting question is
        // decided, or EvidenceConfigValidator will only be checking a stale picture.
        public IReadOnlyList<string> PossibleWeightedEvidenceTypes => new[]
        {
            "CorroborationTier.Tier1",
            "CorroborationTier.Tier2",
            "CorroborationTier.Tier3",
        };

        public IEnumerable<EvidenceRecord> Collect(EmbyArtist source, Candidate candidate, IObservationUnit unit, ResolutionContext context)
        {
            if (unit is not EmbyTrackObservationUnit trackUnit) yield break;
            var track = trackUnit.Track;

            // ONE lookup, same call, every Role (AlbumArtist/Artist/Composer alike).
            // See class doc comment: no LookupComposerTier branching here by settled
            // directive. Widened 2026-07-18: Lookup() itself now also confirms via
            // relationship-scan (candidate.RelationshipMbids), so a Composer-role
            // observation for a composer-only candidate can be confirmed here directly
            // -- rec is no longer necessarily null for that case the way it used to be.
            //
            // REWRITTEN 2026-07-18 (rung-1 search text): previously passed
            // source.DisplayName (the CANDIDATE's own name) unconditionally. That was
            // never really "the candidate's name" doing useful work -- it was only ever
            // correct because, for AlbumArtist/Artist-role observations, the candidate's
            // name IS the track's own recorded performer credit (that's how the Role got
            // derived in the first place). For a Composer-role observation the candidate
            // is essentially never the recording's performer, so passing their own name
            // guaranteed a wasted, unwinnable rung-1 API call every time. The fix isn't a
            // Role-conditional branch (that's the same category error with an if-guard
            // added) -- it's using the right INPUT throughout: rung 1's search text
            // should always be the track's own recorded performer credit (an unconfirmed
            // fact already sitting on the observation, used purely to narrow the query),
            // never the candidate's identity. This generalizes cleanly to every Role,
            // present and future, with no per-role special-casing at all.
            var recordedPerformerName = track.AlbumArtists.FirstOrDefault()?.Name ?? track.Artists.FirstOrDefault()?.Name;
            var lookup = _recordingLookup.Lookup(candidate.TargetId, track, artistName: recordedPerformerName, relationshipMbids: candidate.RelationshipMbids);
            var rec = lookup.Recording;
            if (rec == null) yield break;

            // --- Corroboration Tier (§6.1/§6.3) ---
            // REWRITTEN 2026-07-18 (second pass, same day): tier is now derived from
            // lookup.RungReached, NOT from (rec.TrackTitleMatches, rec.ReleaseTitleMatches).
            // Real data caught the bug in the first rewrite: a recording confirmed at
            // the full TrackArtistAlbum rung (MusicBrainz's own fuzzy/tokenized search
            // already matched track+album+artist together to return it) was getting
            // DEMOTED to Tier3 because our own separate, strict, case-sensitive-modulo-case
            // string-equality recheck (TrackTitleMatches) came back false over an
            // apostrophe-style punctuation variance between the Emby tag and MusicBrainz's
            // stored title -- "That's It, I Quit, I'm Moving On" vs whatever exact
            // characters MB stores. That recheck was answering a different, blunter
            // question than the rung already answers: the rung tells you how much of the
            // triple MusicBrainz's OWN fuzzy matching used to find this recording at all;
            // an exact-string recheck afterward adds no real safety, it just reintroduces
            // brittleness MusicBrainz's search already solved. So: rung decides tier
            // (TrackArtistAlbum -> Tier1, TrackAlbum -> Tier2, TrackOnly -> Tier3);
            // TrackTitleMatches/ReleaseTitleMatches/Score are kept in RawValue/Rationale
            // as informational context for a human reviewing borderline cases, NOT as a
            // second gate that can override the rung's own classification. Whether MB's
            // Score should ever feed the algorithm itself remains an open, undecided
            // question -- flagged, not resolved, since a single 772-recording sample
            // showed it wasn't a reliable discriminator on its own, but that's one sample.
            string tier = lookup.RungReached switch
            {
                RecordingLookupRung.TrackArtistAlbum => "CorroborationTier.Tier1",
                RecordingLookupRung.TrackAlbum => "CorroborationTier.Tier2",
                _ => "CorroborationTier.Tier3",
            };
            string tierDescription = tier switch
            {
                "CorroborationTier.Tier1" => "full-triple (track+artist+album search)",
                "CorroborationTier.Tier2" => "track+album search, no artist filter",
                _ => "track-only search",
            };
            string aliasNote = lookup.MatchedViaAlias ? " (matched via a registered alias)" : "";

            string confirmationNote;
            bool matchedViaRelationship = lookup.ConfirmedViaRelationship;
            string? relationshipTypeForRecord = null;
            if (lookup.ConfirmedViaRelationship && lookup.ConfirmingRelationship != null)
            {
                var rel = lookup.ConfirmingRelationship;
                relationshipTypeForRecord = rel.RelationshipType;
                bool viaRelationshipMbid = rel.ArtistMbid != candidate.TargetId;
                confirmationNote = viaRelationshipMbid
                    ? $" -- confirmed via a related artist identity's {rel.RelationshipType} relationship ({rel.Level})"
                    : $" -- confirmed via this artist's own {rel.RelationshipType} relationship ({rel.Level}), not performer-credit";
            }
            else
            {
                confirmationNote = " -- confirmed via performer-credit";
            }

            yield return new EvidenceRecord
            {
                CandidateId = candidate.Id,
                EvidenceType = tier,
                RawValue = $"rung={lookup.RungReached} mbScore={rec.Score} exactTitleMatch={rec.TrackTitleMatches} exactAlbumMatch={rec.ReleaseTitleMatches} viaRelationship={matchedViaRelationship}",
                Role = track.Role,
                SourceTrackId = track.TrackId,
                AlbumId = track.AlbumId,
                MatchedViaAlias = lookup.MatchedViaAlias,
                MatchedViaRelationship = matchedViaRelationship,
                RelationshipType = relationshipTypeForRecord,
                Rationale = $"MusicBrainz {tierDescription} corroboration for \"{track.TrackName}\"{aliasNote}{confirmationNote} (rung={lookup.RungReached}, mbScore={rec.Score}).",
            };
        }
    }
}