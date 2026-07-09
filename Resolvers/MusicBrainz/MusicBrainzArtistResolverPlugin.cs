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
        public IEnumerable<IEvidenceCollector<EmbyArtist>> EvidenceCollectors { get; }
        public IBeliefScorer Scorer { get; }
        public IDecisionGate DecisionGate { get; }

        public MusicBrainzArtistResolverPlugin(IMusicBrainzApiClient client, IIdentityCache identityCache)
        {
            Strategies = new ICandidateGenerationStrategy<EmbyArtist>[]
            {
                new AnchoredRecordingStrategy(client, identityCache),  // Strategy A, priority 10
                new SoftBucketStrategy(client),                        // Strategy B, priority 30
                // Strategy C (borrowed anchor) arrives in Phase 3 alongside co-occurrence graph, §21
            };

            EvidenceCollectors = new IEvidenceCollector<EmbyArtist>[]
            {
                new NameDistanceEvidenceCollector(client),
                new WorkRelationshipEvidenceCollector(client),
                // Remaining §6.1 collectors arrive in Phase 2's full evidence set, §21
            };

            Scorer = new SimpleWeightedSumScorer();     // Bayesian scorer arrives in Phase 2, §21
            DecisionGate = new ThresholdDecisionGate();
        }
    }
}
