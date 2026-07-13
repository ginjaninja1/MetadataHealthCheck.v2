using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Client;
using MetadataHealthCheck.v2.Resolvers.MusicBrainz.Evidence;
using MetadataHealthCheck.v2.Sources.Emby;

namespace MetadataHealthCheck.v2.Resolvers.MusicBrainz.Strategies
{
    /// <summary>
    /// Strategy B (§5.3): used when no own anchor (Strategy A) and, in later
    /// phases, no borrowed anchor (Strategy C) is available.
    ///
    /// REWRITTEN 2026-07-13 to be artist-search-first, per the settled
    /// architectural decision recorded in the Project Log (supersedes the old
    /// recording-search-first approach, now in the spec's Appendix A):
    ///
    ///   1. C1: SearchArtist(source.DisplayName) -- an artist-name search, which
    ///      returns candidates plus their inline registered aliases in one call
    ///      (no extra inc= parameter needed, confirmed against real MusicBrainz
    ///      data).
    ///   2. Admission gate: a result is only considered further if (a) MB's own
    ///      text-relevance Score clears ScoringConfig.ArtistCandidateMinScore,
    ///      and (b) source.DisplayName is a sufficiently close match to either
    ///      the result's own Name or one of its Aliases (reusing
    ///      NameDistanceEvidenceCollector's distance metric -- one calibrated
    ///      closeness definition, not two).
    ///   3. Per-candidate confirmation: RecordingLookup is reused here, not to
    ///      generate candidates, but to confirm that this admitted candidate
    ///      actually appears on at least one of this artist's real tracks. This
    ///      is what keeps a same-named-but-actually-different MB artist (or an
    ///      artist whose only real presence here is as a work-level credit, e.g.
    ///      Gus Black/Del Serino) from generating a candidate with zero real
    ///      corroboration behind it.
    /// </summary>
    public class SoftBucketStrategy : ICandidateGenerationStrategy<EmbyArtist>
    {
        // Admission-gate closeness floor for name-or-alias matching (§5.3). Reuses
        // NameDistanceEvidenceCollector's own similarity metric and its existing
        // Poor/Neutral boundary (0.7) rather than inventing a second calibration --
        // anything that collector would bucket as Poor is not admitted here either.
        private const double NameOrAliasAdmissionThreshold = 0.7;

        private readonly IMusicBrainzApiClient _client;
        private readonly RecordingLookup _recordingLookup;
        private readonly ScoringConfig _config;

        public SoftBucketStrategy(IMusicBrainzApiClient client, RecordingLookup recordingLookup, ScoringConfig config)
        {
            _client = client;
            _recordingLookup = recordingLookup;
            _config = config;
        }

        public string StrategyName => "B";
        public int Priority => 30; // tried after A (and, later, C) — §5.3

        public IEnumerable<Candidate> GenerateCandidates(EmbyArtist source, ResolutionContext context)
        {
            var artistResults = _client.SearchArtist(source.DisplayName);
            var seen = new HashSet<string>();

            foreach (var result in artistResults)
            {
                if (!seen.Add(result.Mbid))
                    continue;

                if (result.Score < _config.ArtistCandidateMinScore)
                    continue; // MB's own text-relevance score too low to consider, §5.4

                if (!IsNameOrAliasCloseEnough(source.DisplayName, result))
                    continue; // admission gate: name-or-alias closeness, §5.3

                if (!IsConfirmedByAnyTrack(result.Mbid, source))
                    continue; // per-candidate confirmation via RecordingLookup, §5.3

                yield return new Candidate
                {
                    SourceEntityId = source.SourceId,
                    TargetSystem = "MusicBrainz",
                    TargetEntityType = "Artist",
                    TargetId = result.Mbid,
                    GenerationStrategy = StrategyName,
                    GenerationQuery = $"artist:\"{source.DisplayName}\"",
                    CreatedAt = DateTime.UtcNow,
                };
            }
        }

        private bool IsConfirmedByAnyTrack(string candidateMbid, EmbyArtist source)
        {
            foreach (var track in source.Tracks)
            {
                var lookup = _recordingLookup.Lookup(candidateMbid, track, source.DisplayName);
                if (lookup.Recording != null)
                    return true;
            }
            return false;
        }

        private static bool IsNameOrAliasCloseEnough(string sourceName, MbArtistResult result)
        {
            if (NameDistanceEvidenceCollector.NormalizedSimilarity(sourceName, result.Name) >= NameOrAliasAdmissionThreshold)
                return true;

            foreach (var alias in result.Aliases)
            {
                if (NameDistanceEvidenceCollector.NormalizedSimilarity(sourceName, alias) >= NameOrAliasAdmissionThreshold)
                    return true;
            }

            return false;
        }
    }
}