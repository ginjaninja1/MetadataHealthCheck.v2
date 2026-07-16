using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Core.Engine
{
    /// <summary>
    /// §5.5's adaptive, one-observation-at-a-time evidence loop. Runs across all
    /// live candidates jointly, not one candidate to completion in isolation --
    /// §18's worked example requires the margin between the top candidate and its
    /// runner-up to be checked after every single new observation, which only
    /// makes sense if every live candidate is scored in lockstep each round.
    ///
    /// Generic over TSourceEntity and has no knowledge of "tracks" or "AlbumArtist/
    /// Artist/Composer" -- that's entirely IObservationUnitProvider's business
    /// (§11.4's extensibility requirement). An entity type with no observation
    /// concept at all (ObservationUnitProvider == null) still gets a valid result:
    /// static evidence is scored once and that's the final answer, identical to
    /// Phase 1's behavior before this file existed.
    /// </summary>
    public class SequentialSampler<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        private readonly IEnumerable<IEvidenceCollector<TSourceEntity>> _staticCollectors;
        private readonly IEnumerable<IObservationEvidenceCollector<TSourceEntity>> _observationCollectors;
        private readonly IObservationUnitProvider<TSourceEntity>? _unitProvider;
        private readonly IBeliefScorer _scorer;
        private readonly IDecisionGate _decisionGate;
        private readonly StructuredLogger _logger;

        public SequentialSampler(
            IEnumerable<IEvidenceCollector<TSourceEntity>> staticCollectors,
            IEnumerable<IObservationEvidenceCollector<TSourceEntity>> observationCollectors,
            IObservationUnitProvider<TSourceEntity>? unitProvider,
            IBeliefScorer scorer,
            IDecisionGate decisionGate,
            StructuredLogger logger)
        {
            _staticCollectors = staticCollectors;
            _observationCollectors = observationCollectors;
            _unitProvider = unitProvider;
            _scorer = scorer;
            _decisionGate = decisionGate;
            _logger = logger;
        }

        public MatchResult Resolve(TSourceEntity source, List<Candidate> candidates, ScoringConfig config, IMatchRepository repository, ResolutionContext context)
        {
            var evidenceByCandidate = candidates.ToDictionary(c => c.Id, c => new List<EvidenceRecord>());

            // Step 1: static, candidate-pair-level evidence -- once per candidate,
            // regardless of what follows (§5.4). This also covers §5.2's album-match
            // precursor once that collector exists: if it alone crosses a bound, no
            // track-level sampling ever runs (§18's worked example).
            foreach (var candidate in candidates)
            {
                foreach (var collector in _staticCollectors)
                {
                    var record = collector.Collect(source, candidate, context);
                    if (record == null) continue;
                    evidenceByCandidate[candidate.Id].Add(record);
                    repository.SaveEvidence(record);
                    var weight = config.EvidenceWeights.TryGetValue(record.EvidenceType, out var w) ? w.ToString("F2") : "n/a";
                    _logger.Debug("Sampler", "[{0}] static {1} (weight={2}) :: {3}", candidate.TargetId, record.EvidenceType, weight, record.Rationale);
                }
            }

            var decision = ScoreAndDecide(source, candidates, evidenceByCandidate, config);
            if (decision.Status != "needs_review")
            {
                _logger.Debug("Sampler", "Resolved from static evidence alone for {0}; no track-level sampling needed.", source.DisplayName);
                return decision;
            }

            // Step 2: per-observation sampling, bucket by bucket (highest signal
            // first), unit by unit within a bucket, stopping the instant any bound
            // is crossed. BucketCeiling is a safety cap on grinding through a low-
            // signal bucket forever, not a target to reach (§5.5).
            if (_unitProvider != null && _observationCollectors.Any())
            {
                foreach (var bucket in _unitProvider.GetOrderedBuckets(source, context))
                {
                    int drawn = 0;
                    foreach (var unit in bucket)
                    {
                        int ceiling = config.BucketCeiling.TryGetValue(unit.BucketKey, out var c) ? c : int.MaxValue;
                        if (drawn >= ceiling) break; // bucket's budget exhausted -- escalate to next bucket

                        _logger.Info("Sampler", "\n-- Observation #{0} ({1} bucket): {2}", drawn + 1, unit.BucketKey, unit.Describe());

                        foreach (var candidate in candidates)
                        {
                            foreach (var collector in _observationCollectors)
                            {
                                var record = collector.Collect(source, candidate, unit, context);
                                if (record == null) continue;
                                evidenceByCandidate[candidate.Id].Add(record);
                                repository.SaveEvidence(record);
                                var weight = config.EvidenceWeights.TryGetValue(record.EvidenceType, out var w) ? w.ToString("F2") : "n/a";
                                _logger.Debug("Sampler", "  [{0}] {1} (weight={2}) {3} :: {4}", candidate.TargetId, record.EvidenceType, weight, unit.BucketKey, record.Rationale);
                            }
                        }
                        drawn++;

                        decision = ScoreAndDecide(source, candidates, evidenceByCandidate, config);
                        if (decision.Status != "needs_review")
                        {
                            _logger.Info("Sampler", "{0} resolved after {1} observation(s) in bucket {2}: {3}.", source.DisplayName, drawn, unit.BucketKey, decision.Status);
                            return decision;
                        }
                    }
                }
            }

            _logger.Info("Sampler", "{0}: exhausted all bucket budgets without crossing accept/reject thresholds -> needs_review.", source.DisplayName);
            return decision;
        }

        private MatchResult ScoreAndDecide(TSourceEntity source, List<Candidate> candidates, Dictionary<string, List<EvidenceRecord>> evidenceByCandidate, ScoringConfig config)
        {
            var scored = candidates.Select(c => _scorer.Score(c, evidenceByCandidate[c.Id], config)).ToList();
            return _decisionGate.Decide(scored, config, source.SourceSystem, source.SourceId);
        }
    }
}