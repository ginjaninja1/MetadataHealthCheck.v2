using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies;
using MetadataHealthCheck.v2.Scoring;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz
{
    public class MusicBrainzArtistResolverPlugin : IResolverPlugin<EmbyArtist>
    {
        public string TargetSystem => "MusicBrainz";
        public string TargetEntityType => "Artist";

        public IEnumerable<ICandidateGenerationStrategy<EmbyArtist>> Strategies { get; }
        // Static, candidate-pair-level evidence -- name similarity only for now.
        public IEnumerable<IEvidenceCollector<EmbyArtist>> EvidenceCollectors { get; }
        // Per-observation (per-track) evidence, sampled by SequentialSampler.
        public IEnumerable<IObservationEvidenceCollector<EmbyArtist>> ObservationEvidenceCollectors { get; }
        public IObservationUnitProvider<EmbyArtist>? ObservationUnitProvider { get; }
        public IBeliefScorer Scorer { get; }
        public IDecisionGate DecisionGate { get; }

        // identityCache is currently unused in this constructor body: its only consumer,
        // AnchoredRecordingStrategy, was unwired below (anchoring parked, spec §5.1/§10.1).
        // Left in the signature deliberately rather than changed blind — this repo can't be
        // built/verified in the sandbox, so a signature change risks silently breaking the
        // composition root (§12.3) wiring without any way to catch it here. Flagged as a
        // small next-session cleanup, not fixed in this pass.
        //
        // logger ADDED 2026-07-16 (Nick's explicit request): candidate generation
        // (ArtistStrategy's admit/drop decisions) and RecordingLookup's entire
        // confirmation ladder were completely invisible before this -- this constructor
        // had no way to give them one. Optional (nullable) so any other caller that
        // doesn't have a logger handy isn't forced to break. Composition root callers
        // (SmokeTest/Program.cs, eventually the real Emby host wiring) should pass one.
        public MusicBrainzArtistResolverPlugin(IMusicBrainzApiClient client, IIdentityCache identityCache, ScoringConfig scoringConfig, MetadataHealthCheck.v2.Diagnostics.StructuredLogger? logger = null)
        {
            // Shared across every per-observation collector that needs to confirm a
            // candidate against a specific track (§7.2 C3/C4): WorkRelationshipEvidenceCollector,
            // RecordingRelationshipEvidenceCollector, CorroborationTierEvidenceCollector below.
            // NOT used by ArtistStrategy any more (2026-07-16) -- candidate generation used
            // to also do its own separate recording-lookup confirmation pass here ("Phase 2"),
            // bypassing the Track Observation Feeder entirely; removed as architecturally wrong
            // (see ArtistStrategy.cs's own doc comment). Recording lookups now happen
            // exclusively inside the Engine's Feeder-ordered per-observation loop, via these
            // collectors -- one shared instance, so its per-(candidate,track) memoization still
            // pays off across every collector that touches it within one resolution run.
            var recordingLookup = new RecordingLookup(client, scoringConfig, logger);

            Strategies = new ICandidateGenerationStrategy<EmbyArtist>[]
            {
                new ArtistStrategy(client, scoringConfig, logger), // artist-search-first generation, spec §5.1 -- name/alias filtering only, no recording lookups; confirmation happens entirely inside the Engine's per-observation loop now (2026-07-16)
                // AnchoredRecordingStrategy.cs is retained in the repo but deliberately not
                // registered here. Anchoring is a parked concept (spec §5.1/§10.1) — wiring
                // it in alongside ArtistStrategy risked duplicate candidates with split
                // evidence pools across two generation paths. Do not re-add without an
                // explicit decision to un-park anchoring.
            };

            EvidenceCollectors = new IEvidenceCollector<EmbyArtist>[]
            {
                new NameDistanceEvidenceCollector(client),
                // AlbumMatchEvidenceCollector parked 2026-07-17 (unwired, not deleted).
                // §5.2 static evidence, confirmed deliberate rather than a bug, but its
                // cost (a full GetReleaseGroupTitles call per candidate before any
                // observation sampling even starts) was flagged as an open question.
                // Re-add only if evidence from testing shows it's actually needed.
                // new AlbumMatchEvidenceCollector(client),
            };

            ObservationEvidenceCollectors = new IObservationEvidenceCollector<EmbyArtist>[]
            {
                new ProviderIdEvidenceCollector(), // Tier 0, §6.1 -- built 2026-07-15
                // WorkRelationshipEvidenceCollector, RecordingRelationshipEvidenceCollector,
                // and CorroborationTierEvidenceCollector were collapsed into this single
                // collector 2026-07-17 -- see RecordingCorroborationEvidenceCollector.cs's
                // doc comment for why (three collectors were each independently deciding
                // how to call the shared RecordingLookup, causing real cache-collision
                // bugs). The three old files remain in the repo, unwired, not deleted.
                new RecordingCorroborationEvidenceCollector(client, recordingLookup, logger),
                // NOT built: AliasEvidenceCollector.cs, which §11.2's file tree still
                // lists as planned. Left out deliberately, not just not-yet-started —
                // §6.1/§5.3's own retirement notes say alias-match is no longer a
                // standalone evidence type at all; its function is now fully absorbed
                // into Stage 1 (admission gate, zero LLR) and Stage 2
                // (MatchedViaAlias -> NameMatchWeight/AliasMatchWeight multiplier on
                // Corroboration Tier evidence, both built this session). Building a
                // separate AliasEvidenceCollector alongside that would double-count or
                // contradict it. Flagged as an open spec contradiction, not resolved
                // silently either way — worth a direct answer before touching §11.2's
                // file tree.
            };

            ObservationUnitProvider = new EmbyArtistObservationUnitProvider();

            Scorer = new SimpleWeightedSumScorer();     // Bayesian scorer arrives as a follow-up step, §21
            DecisionGate = new ThresholdDecisionGate();
        }
    }
}