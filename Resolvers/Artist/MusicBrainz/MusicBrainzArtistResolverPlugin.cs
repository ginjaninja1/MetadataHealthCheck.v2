using MetadataHealthCheck.v2.Core.Engine;
using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Diagnostics;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Buckets;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.CandidateGeneration;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Config;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Evidence;
using MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz.Scoring;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.Artist.MusicBrainz
{
    /// <summary>
    /// Composition root for the Artist/MusicBrainz resolver: wires candidate
    /// generation, evidence collection, scoring, and the decision gate into one
    /// IResolverPlugin&lt;EmbyArtist, ArtistMusicBrainzConfig&gt;.
    /// </summary>
    public class MusicBrainzArtistResolverPlugin : IResolverPlugin<EmbyArtist, ArtistMusicBrainzConfig>
    {
        public string TargetSystem => "MusicBrainz";
        public string TargetEntityType => "Artist";

        public IEnumerable<ICandidateGenerationStrategy<EmbyArtist>> Strategies { get; }
        public IEnumerable<ICandidateEvidenceCollector<EmbyArtist>> CandidateEvidenceCollectors { get; }
        public IEnumerable<IPerUnitEvidenceCollector<EmbyArtist>> PerUnitEvidenceCollectors { get; }
        public IEnumerable<IJointCandidateEvidenceCollector<EmbyArtist>> JointCandidateEvidenceCollectors { get; }
        public IObservationUnitProvider<EmbyArtist>? ObservationUnitProvider { get; }
        public IBucketCandidateFilter? BucketCandidateFilter { get; }
        public IBeliefScorer<ArtistMusicBrainzConfig> Scorer { get; }
        public IDecisionGate<ArtistMusicBrainzConfig> DecisionGate { get; }
        public IResolutionStrategy<EmbyArtist, ArtistMusicBrainzConfig> Strategy { get; }

        public MusicBrainzArtistResolverPlugin(IMusicBrainzApiClient client, ArtistMusicBrainzConfig scoringConfig, StructuredLogger? logger = null)
        {
            // Shared across every per-observation collector that needs to
            // confirm a candidate against a specific track, so its
            // per-(candidate,track) memoization pays off across every
            // collector that touches it within one resolution run.
            var recordingLookup = new RecordingLookup(client, scoringConfig, logger);

            Strategies = new ICandidateGenerationStrategy<EmbyArtist>[]
            {
                new ArtistCandidateStrategy(client, scoringConfig, logger),
            };

            CandidateEvidenceCollectors = new ICandidateEvidenceCollector<EmbyArtist>[]
            {
                new NameDistanceEvidenceCollector(client),
            };

            PerUnitEvidenceCollectors = Array.Empty<IPerUnitEvidenceCollector<EmbyArtist>>();

            JointCandidateEvidenceCollectors = new IJointCandidateEvidenceCollector<EmbyArtist>[]
            {
                new RecordingCorroborationEvidenceCollector(recordingLookup),
            };

            ObservationUnitProvider = new EmbyArtistObservationUnitProvider();
            BucketCandidateFilter = new ComposerBucketCandidateFilter(logger);

            Scorer = new SimpleWeightedSumScorer();
            DecisionGate = new ThresholdDecisionGate();

            // This resolver's own choice of resolution procedure: sequential
            // sampling with early stopping. A future resolver with no
            // observation-unit concept is free to supply a different
            // IResolutionStrategy implementation instead -- Engine never sees
            // the difference. See Architecture-Layers.md.
            Strategy = new SequentialSampler<EmbyArtist, ArtistMusicBrainzConfig>(
                PerUnitEvidenceCollectors,
                JointCandidateEvidenceCollectors,
                ObservationUnitProvider,
                BucketCandidateFilter,
                Scorer,
                DecisionGate,
                logger);
        }
    }
}