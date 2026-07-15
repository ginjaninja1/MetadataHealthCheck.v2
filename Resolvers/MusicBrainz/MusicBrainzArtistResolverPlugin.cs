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
        public MusicBrainzArtistResolverPlugin(IMusicBrainzApiClient client, IIdentityCache identityCache, ScoringConfig scoringConfig)
        {
            // Shared across every collector that needs to confirm a candidate against a
            // specific track (§7.2 C3/C4). Now also used by SoftBucketStrategy itself for
            // per-candidate confirmation (2026-07-13, artist-search-first rewrite), not just
            // by evidence collectors — so its per-(candidate,track) memoization pays off
            // across candidate generation AND scoring within one resolution run.
            var recordingLookup = new RecordingLookup(client);

            Strategies = new ICandidateGenerationStrategy<EmbyArtist>[]
            {
                new SoftBucketStrategy(client, recordingLookup, scoringConfig), // artist-search-first generation + confirmation, spec §5.1
                // AnchoredRecordingStrategy.cs is retained in the repo but deliberately not
                // registered here. Anchoring is a parked concept (spec §5.1/§10.1) — wiring
                // it in alongside SoftBucketStrategy risked duplicate candidates with split
                // evidence pools across two generation paths. Do not re-add without an
                // explicit decision to un-park anchoring.
            };

            EvidenceCollectors = new IEvidenceCollector<EmbyArtist>[]
            {
                new NameDistanceEvidenceCollector(client),
                new AlbumMatchEvidenceCollector(client), // §5.2 precursor
            };

            ObservationEvidenceCollectors = new IObservationEvidenceCollector<EmbyArtist>[]
            {
                new ProviderIdEvidenceCollector(), // Tier 0, §6.1 -- built 2026-07-15
                new WorkRelationshipEvidenceCollector(client, recordingLookup),
                new RecordingRelationshipEvidenceCollector(client, recordingLookup),
                new CorroborationTierEvidenceCollector(recordingLookup),
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