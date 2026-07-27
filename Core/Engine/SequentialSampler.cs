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
        private readonly IEnumerable<IRoundBasedObservationEvidenceCollector<TSourceEntity>> _roundBasedCollectors;
        private readonly IObservationUnitProvider<TSourceEntity>? _unitProvider;
        // Added 2026-07-27: optional, per-bucket candidate narrowing (e.g. Composer
        // bucket folding a Group candidate into its live Person band-member). Null means
        // no filtering anywhere -- every bucket's candidate list is exactly `candidates`,
        // identical to behavior before this field existed.
        private readonly IBucketCandidateFilter? _bucketCandidateFilter;
        private readonly IBeliefScorer _scorer;
        private readonly IDecisionGate _decisionGate;
        private readonly StructuredLogger _logger;

        public SequentialSampler(
            IEnumerable<IEvidenceCollector<TSourceEntity>> staticCollectors,
            IEnumerable<IObservationEvidenceCollector<TSourceEntity>> observationCollectors,
            IEnumerable<IRoundBasedObservationEvidenceCollector<TSourceEntity>> roundBasedCollectors,
            IObservationUnitProvider<TSourceEntity>? unitProvider,
            IBucketCandidateFilter? bucketCandidateFilter,
            IBeliefScorer scorer,
            IDecisionGate decisionGate,
            StructuredLogger logger)
        {
            _staticCollectors = staticCollectors;
            _observationCollectors = observationCollectors;
            _roundBasedCollectors = roundBasedCollectors;
            _unitProvider = unitProvider;
            _bucketCandidateFilter = bucketCandidateFilter;
            _scorer = scorer;
            _decisionGate = decisionGate;
            _logger = logger;
        }

        public MatchResult Resolve(TSourceEntity source, List<Candidate> candidates, ScoringConfig config, IMatchRepository repository, ResolutionContext context)
        {
            var evidenceByCandidate = candidates.ToDictionary(c => c.Id, c => new List<EvidenceRecord>());

            // Added 2026-07-27: candidate lookup + display-label resolution, purely for
            // logging. candidatesById exists because round-based collectors key their
            // per-round dictionaries by Candidate.Id (an internal GUID -- see
            // evidenceByCandidate above), so a round's log line needs to map back to the
            // actual Candidate to get its Name/TargetId. nameCounts drives the
            // "Name|MBID" vs bare "Name" choice below: only disambiguate with the MBID
            // when two live candidates actually share a display name.
            var candidatesById = candidates.ToDictionary(c => c.Id);
            var nameCounts = candidates
                .Select(c => string.IsNullOrEmpty(c.Name) ? c.TargetId : c.Name)
                .GroupBy(n => n)
                .ToDictionary(g => g.Key, g => g.Count());

            Banner($"BEGIN RESOLUTION: {source.DisplayName}");

            // Step 1: static, candidate-pair-level evidence -- once per candidate,
            // regardless of what follows (§5.4). This also covers §5.2's album-match
            // precursor once that collector exists: if it alone crosses a bound, no
            // track-level sampling ever runs (§18's worked example).
            Banner("STATIC EVIDENCE");
            foreach (var candidate in candidates)
            {
                var candidateRecords = new List<EvidenceRecord>();
                foreach (var collector in _staticCollectors)
                {
                    var record = collector.Collect(source, candidate, context);
                    if (record == null) continue;
                    evidenceByCandidate[candidate.Id].Add(record);
                    repository.SaveEvidence(record);
                    candidateRecords.Add(record);
                }
                if (candidateRecords.Count == 0) continue;

                var contributing = candidateRecords.Where(r => r.Contributing).ToList();
                var opportunistic = candidateRecords.Where(r => !r.Contributing).ToList();

                foreach (var record in contributing)
                {
                    LogEvidence(candidate, nameCounts, record, config, prefix: "static ", indent: "");
                }
                if (opportunistic.Count > 0)
                {
                    _logger.Debug("Sampler", "[{0}] ---- opportunistic evidence below (not scored, informational only) ----", FormatCandidateLabel(candidate, nameCounts));
                    foreach (var record in opportunistic)
                    {
                        LogEvidence(candidate, nameCounts, record, config, prefix: "static ", indent: "");
                    }
                    _logger.Debug("Sampler", "[{0}] ---- end opportunistic evidence ----", FormatCandidateLabel(candidate, nameCounts));
                }
            }
            Banner("END STATIC EVIDENCE");

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
            // new:
            if (_unitProvider != null && (_observationCollectors.Any() || _roundBasedCollectors.Any()))
            {
                foreach (var bucket in _unitProvider.GetOrderedBuckets(source, context))
                {
                    int drawn = 0;
                    foreach (var unit in bucket)
                    {
                        int ceiling = config.BucketCeiling.TryGetValue(unit.BucketKey, out var c) ? c : int.MaxValue;
                        if (drawn >= ceiling) break; // bucket's budget exhausted -- escalate to next bucket

                        Banner($"OBSERVATION #{drawn + 1} ({unit.BucketKey} bucket): {unit.Describe()}");

                        // Added 2026-07-27: narrow the live candidate set for THIS bucket only,
                        // if the plugin registered a filter. `candidates` (the full list used for
                        // evidenceByCandidate/scoring/nameCounts/candidatesById above) is untouched --
                        // a filtered-out candidate just doesn't get new evidence collected for it in
                        // this bucket; its running LLR from any other bucket stands as-is.
                        var bucketCandidates = _bucketCandidateFilter?.Filter(unit.BucketKey, candidates, context) ?? candidates;

                        foreach (var candidate in bucketCandidates)
                        {
                            var candidateRecords = new List<EvidenceRecord>();
                            foreach (var collector in _observationCollectors)
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
                            {
                                LogEvidence(candidate, nameCounts, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);
                            }
                            if (opportunistic.Count > 0)
                            {
                                _logger.Debug("Sampler", "  [{0}] ---- opportunistic evidence below (not scored, informational only) ----", FormatCandidateLabel(candidate, nameCounts));
                                foreach (var record in opportunistic)
                                {
                                    LogEvidence(candidate, nameCounts, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);
                                }
                                _logger.Debug("Sampler", "  [{0}] ---- end opportunistic evidence ----", FormatCandidateLabel(candidate, nameCounts));
                            }
                        }
                        // Added 2026-07-23: round-based collectors (currently just
                        // RecordingCorroborationEvidenceCollector) check ALL live candidates
                        // jointly per round, re-scoring and checking the decision gate after
                        // EVERY round (not just once per whole observation) -- because these
                        // helpers are yield-return-based, stopping here (foreach+break) means
                        // the next round's API call (e.g. the next recording's
                        // GetRelationships) genuinely never fires. See
                        // IRoundBasedObservationEvidenceCollector's own doc comment.
                        //
                        // NOTE 2026-07-27: round dictionaries are keyed by Candidate.Id (the
                        // internal GUID, matching evidenceByCandidate's key), NOT TargetId --
                        // candidatesById below maps that back to the real Candidate so the log
                        // line can show a name instead of a meaningless GUID.
                        bool stoppedMidObservation = false;
                        foreach (var collector in _roundBasedCollectors)
                        {
                            foreach (var round in collector.CollectRounds(source, bucketCandidates, unit, context))
                            {
                                foreach (var roundKvp in round)
                                {
                                    var candidateId = roundKvp.Key;
                                    var records = roundKvp.Value;
                                    var roundCandidate = candidatesById[candidateId];
                                    foreach (var record in records.Where(r => r.Contributing))
                                    {
                                        evidenceByCandidate[candidateId].Add(record);
                                        repository.SaveEvidence(record);
                                        LogEvidence(roundCandidate, nameCounts, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);
                                    }
                                    foreach (var record in records.Where(r => !r.Contributing))
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

                        // Unconditional recompute: guards against a stale `decision` from a
                        // PRIOR observation when this observation's round-based collectors
                        // produced zero rounds (e.g. nothing confirmed at all) -- without
                        // this, the check below could act on last observation's result.
                        // Harmless/idempotent when stoppedMidObservation is already true.
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

        // Added 2026-07-27: human-readable candidate label for logging only -- never
        // used for identity/matching decisions. Falls back to the MBID when Name is
        // unset (e.g. Strategy A candidates, which don't currently carry a name), and
        // only appends "|MBID" when two live candidates in THIS resolution share the
        // same display name.
        private static string FormatCandidateLabel(Candidate candidate, Dictionary<string, int> nameCounts)
        {
            var label = string.IsNullOrEmpty(candidate.Name) ? candidate.TargetId : candidate.Name;
            return nameCounts.TryGetValue(label, out var count) && count > 1
                ? $"{label}|{candidate.TargetId}"
                : label;
        }

        private void LogEvidence(Candidate candidate, Dictionary<string, int> nameCounts, EvidenceRecord record, ScoringConfig config, string prefix, string indent, string? bucketKey = null)
        {
            var weight = config.EvidenceWeights.TryGetValue(record.EvidenceType, out var w) ? w.ToString("F2") : "n/a";
            var bucketPart = bucketKey != null ? $" {bucketKey}" : "";
            var label = FormatCandidateLabel(candidate, nameCounts);
            if (record.Contributing)
            {
                _logger.Debug("Sampler", "{0}[{1}] {2}{3} (weight={4}){5} :: {6}", indent, label, prefix, record.EvidenceType, weight, bucketPart, record.Rationale);
            }
            else
            {
                _logger.Debug("Sampler", "{0}[{1}] {2}{3} [opportunistic - not scored]{4} :: {5}", indent, label, prefix, record.EvidenceType, bucketPart, record.Rationale);
            }
        }

        private MatchResult ScoreAndDecide(TSourceEntity source, List<Candidate> candidates, Dictionary<string, List<EvidenceRecord>> evidenceByCandidate, ScoringConfig config)
        {
            var scored = candidates.Select(c => _scorer.Score(c, evidenceByCandidate[c.Id].Where(e => e.Contributing), config)).ToList();
            return _decisionGate.Decide(scored, config, source.SourceSystem, source.SourceId);
        }
    }
}