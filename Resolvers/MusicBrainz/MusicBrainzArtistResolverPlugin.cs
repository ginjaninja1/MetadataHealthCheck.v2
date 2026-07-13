using MetadataHealthCheck.v2.Core.Interfaces;
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

        public MusicBrainzArtistResolverPlugin(IMusicBrainzApiClient client, IIdentityCache identityCache)
        {
            // Shared across every collector that needs to confirm a candidate against a
            // specific track (§7.2 C3/C4), added 2026-07-12 alongside RecordingLookup.cs.
            // Constructed once per resolver-plugin instance so its per-(candidate,track)
            // memoization actually pays off once more than one collector shares it.
            var recordingLookup = new RecordingLookup(client);

            Strategies = new ICandidateGenerationStrategy<EmbyArtist>[]
            {
                new AnchoredRecordingStrategy(client, identityCache),  // Strategy A, priority 10
                new SoftBucketStrategy(client),                        // Strategy B, priority 30
                // Strategy C (borrowed anchor) arrives in Phase 3 alongside co-occurrence graph, §21
            };

            EvidenceCollectors = new IEvidenceCollector<EmbyArtist>[]
            {
                new NameDistanceEvidenceCollector(client),
            };

            ObservationEvidenceCollectors = new IObservationEvidenceCollector<EmbyArtist>[]
            {
                new WorkRelationshipEvidenceCollector(client, recordingLookup),
                // AliasEvidenceCollector, RecordingRelationshipEvidenceCollector,
                // AlbumMatchEvidenceCollector, CorroborationTierEvidenceCollector
                // arrive alongside BayesianBeliefScorer as a follow-up step, §21. None of
                // these three exist in the repo yet as of this commit (ground-truth
                // verified 2026-07-12) — an earlier log entry claiming otherwise was wrong.
            };

            ObservationUnitProvider = new EmbyArtistObservationUnitProvider();

            Scorer = new SimpleWeightedSumScorer();     // Bayesian scorer arrives as a follow-up step, §21
            DecisionGate = new ThresholdDecisionGate();
        }
    }
}