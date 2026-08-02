using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Core.Engine
{
    /// <summary>
    /// The adaptive, one-observation-at-a-time evidence loop. Runs across all
    /// live candidates jointly, not one candidate to completion in isolation --
    /// the margin between the top candidate and its runner-up is checked after
    /// every single new observation, which only makes sense if every live
    /// candidate is scored in lockstep each round.
    ///
    /// Generic over TSourceEntity and TConfig, with no knowledge of any
    /// resolver's own bucket/observation-unit vocabulary -- that's entirely
    /// IObservationUnitProvider's business. An entity type with no observation
    /// concept at all (ObservationUnitProvider == null) still gets a valid
    /// result: static evidence is scored once and that's the final answer.
    ///
    /// This is one implementation of IResolutionStrategy -- the one a resolver
    /// chooses when sequential/adaptive sampling over observation units suits
    /// its domain. Engine depends only on IResolutionStrategy, never on this
    /// class directly. See Architecture-Layers.md.
    /// </summary>
    public class SequentialSampler<TSourceEntity, TConfig> : IResolutionStrategy<TSourceEntity, TConfig>
        where TSourceEntity : ISourceEntity
        where TConfig : IScoringConfig
    {
        private readonly IEnumerable<IPerUnitEvidenceCollector<TSourceEntity>> _perUnitCollectors;
        private readonly IEnumerable<IJointCandidateEvidenceCollector<TSourceEntity>> _jointCandidateCollectors;
        private readonly IObservationUnitProvider<TSourceEntity>? _unitProvider;

        // Optional, per-bucket candidate narrowing. Null means no filtering
        // anywhere -- every bucket's candidate list is exactly `candidates`.
        private readonly IBucketCandidateFilter? _bucketCandidateFilter;
        private readonly IBeliefScorer<TConfig> _scorer;
        private readonly IDecisionGate<TConfig> _decisionGate;
        private readonly StructuredLogger _logger;

        public SequentialSampler(
            IEnumerable<IPerUnitEvidenceCollector<TSourceEntity>> perUnitCollectors,
            IEnumerable<IJointCandidateEvidenceCollector<TSourceEntity>> jointCandidateCollectors,
            IObservationUnitProvider<TSourceEntity>? unitProvider,
            IBucketCandidateFilter? bucketCandidateFilter,
            IBeliefScorer<TConfig> scorer,
            IDecisionGate<TConfig> decisionGate,
            StructuredLogger logger)
        {
            _perUnitCollectors = perUnitCollectors;
            _jointCandidateCollectors = jointCandidateCollectors;
            _unitProvider = unitProvider;
            _bucketCandidateFilter = bucketCandidateFilter;
            _scorer = scorer;
            _decisionGate = decisionGate;
            _logger = logger;
        }

        public MatchResult Resolve(TSourceEntity source, List<Candidate> candidates, TConfig config, IMatchRepository repository, ResolutionContext context)
        {
            var evidenceByCandidate = candidates.ToDictionary(c => c.Id, c => new List<EvidenceRecord>());

            // candidatesById lets a round-based collector's per-round dictionary
            // (keyed by Candidate.Id) resolve back to the actual Candidate for
            // logging. nameCounts drives the "Name|MBID" vs bare "Name" choice in
            // FormatCandidateLabel: only disambiguate with the target id when two
            // live candidates actually share a display name.
            var candidatesById = candidates.ToDictionary(c => c.Id);
            var nameCounts = candidates
                .Select(c => string.IsNullOrEmpty(c.Name) ? c.TargetId : c.Name)
                .GroupBy(n => n)
                .ToDictionary(g => g.Key, g => g.Count());

            Banner($"BEGIN RESOLUTION: {source.DisplayName}");

            // `decision` covers the case of zero observations ever being drawn
            // (e.g. no buckets, or every bucket empty): scoring empty evidence
            // yields a needs_review result, which is then returned as-is below.
            var decision = ScoreAndDecide(source, candidates, evidenceByCandidate, config);

            // Per-observation sampling, bucket by bucket (highest signal first),
            // unit by unit within a bucket, stopping the instant any decision
            // threshold is crossed. BucketCeiling is a safety cap on grinding
            // through a low-signal bucket forever, not a target to reach.
            if (_unitProvider != null && (_perUnitCollectors.Any() || _jointCandidateCollectors.Any()))
            {
                foreach (var bucket in _unitProvider.GetOrderedBuckets(source, context))
                {
                    int drawn = 0;
                    foreach (var unit in bucket)
                    {
                        int ceiling = config.BucketCeiling.TryGetValue(unit.BucketKey, out var c) ? c : int.MaxValue;
                        if (drawn >= ceiling) break;

                        Banner($"OBSERVATION #{drawn + 1} ({unit.BucketKey} bucket): {unit.Describe()}");

                        // Narrow the live candidate set for THIS bucket only, if the
                        // plugin registered a filter. `candidates` (the full list used
                        // for evidenceByCandidate/scoring/nameCounts/candidatesById
                        // above) is untouched -- a filtered-out candidate just doesn't
                        // get new evidence collected for it in this bucket; its
                        // running LLR from any other bucket stands as-is.
                        var bucketCandidates = _bucketCandidateFilter?.Filter(unit.BucketKey, candidates, context) ?? candidates;

                        foreach (var candidate in bucketCandidates)
                        {
                            var candidateRecords = new List<EvidenceRecord>();
                            foreach (var collector in _perUnitCollectors)
                            {
                                foreach (var record in collector.Collect(source, candidate, unit, context))
                                {
                                    if (record == null) continue;
                                    evidenceByCandidate[candidate.Id].Add(record);
                                    repository.SaveEvidence(record);
                                    candidateRecords.Add(record);
                                }
                            }
                            if (candidateRecords.Count == 0) continue;

                            var contributing = candidateRecords.Where(r => r.Contributing).ToList();
                            var opportunistic = candidateRecords.Where(r => !r.Contributing).ToList();

                            foreach (var record in contributing)
                                LogEvidence(candidate, nameCounts, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);

                            if (opportunistic.Count > 0)
                            {
                                _logger.Debug("Sampler", "  [{0}] ---- opportunistic evidence below (not scored, informational only) ----", FormatCandidateLabel(candidate, nameCounts));
                                foreach (var record in opportunistic)
                                    LogEvidence(candidate, nameCounts, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);
                                _logger.Debug("Sampler", "  [{0}] ---- end opportunistic evidence ----", FormatCandidateLabel(candidate, nameCounts));
                            }
                        }

                        // Round-based collectors check ALL live candidates jointly per
                        // round, re-scoring and checking the decision gate after EVERY
                        // round (not just once per whole observation): because these
                        // collectors are yield-return-based, stopping here (break)
                        // means the next round's underlying lookup genuinely never
                        // fires. See IJointCandidateEvidenceCollector.
                        bool stoppedMidObservation = false;
                        foreach (var collector in _jointCandidateCollectors)
                        {
                            foreach (var round in collector.CollectRounds(source, bucketCandidates, unit, context))
                            {
                                foreach (var roundKvp in round)
                                {
                                    var candidateId = roundKvp.Key;
                                    var records = roundKvp.Value;
                                    var roundCandidate = candidatesById[candidateId];
                                    foreach (var record in records)
                                    {
                                        evidenceByCandidate[candidateId].Add(record);
                                        repository.SaveEvidence(record);
                                        LogEvidence(roundCandidate, nameCounts, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);
                                    }
                                }

                                decision = ScoreAndDecide(source, candidates, evidenceByCandidate, config);
                                if (decision.Status != "needs_review")
                                {
                                    stoppedMidObservation = true;
                                    break;
                                }
                            }
                            if (stoppedMidObservation) break;
                        }

                        // Unconditional recompute guards against a stale `decision`
                        // from a prior observation when this observation's round-based
                        // collectors produced zero rounds. Harmless/idempotent when
                        // stoppedMidObservation is already true.
                        decision = ScoreAndDecide(source, candidates, evidenceByCandidate, config);

                        drawn++;
                        Banner($"END OBSERVATION #{drawn}");

                        if (decision.Status != "needs_review")
                        {
                            _logger.Debug("Sampler", "{0}: stopped sampling after {1} observation(s) in bucket {2} (a decision threshold was crossed).", source.DisplayName, drawn, unit.BucketKey);
                            return decision;
                        }
                    }
                }
            }

            _logger.Debug("Sampler", "{0}: exhausted all bucket budgets without crossing any accept/reject threshold.", source.DisplayName);
            return decision;
        }

        private void Banner(string label)
        {
            _logger.Info("Sampler", "================================================================");
            _logger.Info("Sampler", label);
            _logger.Info("Sampler", "================================================================");
        }

        // Human-readable candidate label for logging only -- never used for
        // identity/matching decisions. Falls back to the target id when Name is
        // unset, and only appends "|target id" when two live candidates in this
        // resolution share the same display name.
        private static string FormatCandidateLabel(Candidate candidate, Dictionary<string, int> nameCounts)
        {
            var label = string.IsNullOrEmpty(candidate.Name) ? candidate.TargetId : candidate.Name;
            return nameCounts.TryGetValue(label, out var count) && count > 1
                ? $"{label}|{candidate.TargetId}"
                : label;
        }

        private void LogEvidence(Candidate candidate, Dictionary<string, int> nameCounts, EvidenceRecord record, TConfig config, string prefix, string indent, string? bucketKey = null)
        {
            var weight = config.EvidenceWeights.TryGetValue(record.EvidenceType, out var w) ? w.ToString("F2") : "n/a";
            var bucketPart = bucketKey != null ? $" {bucketKey}" : "";
            var label = FormatCandidateLabel(candidate, nameCounts);
            if (record.Contributing)
                _logger.Debug("Sampler", "{0}[{1}] {2}{3} (weight={4}){5} :: {6}", indent, label, prefix, record.EvidenceType, weight, bucketPart, record.Rationale);
            else
                _logger.Debug("Sampler", "{0}[{1}] {2}{3} [opportunistic - not scored]{4} :: {5}", indent, label, prefix, record.EvidenceType, bucketPart, record.Rationale);
        }

        private MatchResult ScoreAndDecide(TSourceEntity source, List<Candidate> candidates, Dictionary<string, List<EvidenceRecord>> evidenceByCandidate, TConfig config)
        {
            var scored = candidates.Select(c => _scorer.Score(c, evidenceByCandidate[c.Id].Where(e => e.Contributing), config)).ToList();
            return _decisionGate.Decide(scored, config, source.SourceSystem, source.SourceId);
        }
    }
}