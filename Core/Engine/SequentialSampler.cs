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
                    LogEvidence(candidate.TargetId, record, config, prefix: "static ", indent: "");
                }
                if (opportunistic.Count > 0)
                {
                    _logger.Debug("Sampler", "[{0}] ---- opportunistic evidence below (not scored, informational only) ----", candidate.TargetId);
                    foreach (var record in opportunistic)
                    {
                        LogEvidence(candidate.TargetId, record, config, prefix: "static ", indent: "");
                    }
                    _logger.Debug("Sampler", "[{0}] ---- end opportunistic evidence ----", candidate.TargetId);
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
            if (_unitProvider != null && _observationCollectors.Any())
            {
                foreach (var bucket in _unitProvider.GetOrderedBuckets(source, context))
                {
                    int drawn = 0;
                    foreach (var unit in bucket)
                    {
                        int ceiling = config.BucketCeiling.TryGetValue(unit.BucketKey, out var c) ? c : int.MaxValue;
                        if (drawn >= ceiling) break; // bucket's budget exhausted -- escalate to next bucket

                        Banner($"OBSERVATION #{drawn + 1} ({unit.BucketKey} bucket): {unit.Describe()}");

                        foreach (var candidate in candidates)
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
                                LogEvidence(candidate.TargetId, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);
                            }
                            if (opportunistic.Count > 0)
                            {
                                _logger.Debug("Sampler", "  [{0}] ---- opportunistic evidence below (not scored, informational only) ----", candidate.TargetId);
                                foreach (var record in opportunistic)
                                {
                                    LogEvidence(candidate.TargetId, record, config, prefix: "", indent: "  ", bucketKey: unit.BucketKey);
                                }
                                _logger.Debug("Sampler", "  [{0}] ---- end opportunistic evidence ----", candidate.TargetId);
                            }
                        }
                        drawn++;
                        Banner($"END OBSERVATION #{drawn}");

                        decision = ScoreAndDecide(source, candidates, evidenceByCandidate, config);
                        if (decision.Status != "needs_review")
                        {
                            // Deliberately doesn't state decision.Status/Confidence here
                            // (2026-07-17) -- StructuredLogger has no level filtering, so
                            // "Debug" doesn't actually suppress a line, it only relabels
                            // it. The real fix is not stating the outcome at all until
                            // SmokeTest/Program.cs's own dedicated "STAGE: decision" ->
                            // PrintDecision step. This line only reports the mechanical
                            // fact of when/why sampling stopped.
                            _logger.Debug("Sampler", "{0}: stopped sampling after {1} observation(s) in bucket {2} (a decision threshold was crossed).", source.DisplayName, drawn, unit.BucketKey);
                            return decision;
                        }
                    }
                }
            }

            _logger.Debug("Sampler", "{0}: exhausted all bucket budgets without crossing any accept/reject threshold.", source.DisplayName);
            return decision;
        }

        // Visual banner around each phase/observation so the eye can find phase
        // boundaries in a long log without parsing text. Added 2026-07-17 per
        // direct instruction.
        private void Banner(string label)
        {
            _logger.Info("Sampler", "================================================================");
            _logger.Info("Sampler", label);
            _logger.Info("Sampler", "================================================================");
        }

        // A single evidence line, clearly marked [opportunistic - not scored] when
        // Contributing is false (2026-07-17 directive: opportunistic evidence is
        // logged for later "does it add value" analysis, but must never be
        // mistaken for something that influenced the decision).
        private void LogEvidence(string candidateId, EvidenceRecord record, ScoringConfig config, string prefix, string indent, string? bucketKey = null)
        {
            var weight = config.EvidenceWeights.TryGetValue(record.EvidenceType, out var w) ? w.ToString("F2") : "n/a";
            var bucketPart = bucketKey != null ? $" {bucketKey}" : "";
            if (record.Contributing)
            {
                _logger.Debug("Sampler", "{0}[{1}] {2}{3} (weight={4}){5} :: {6}", indent, candidateId, prefix, record.EvidenceType, weight, bucketPart, record.Rationale);
            }
            else
            {
                _logger.Debug("Sampler", "{0}[{1}] {2}{3} [opportunistic - not scored]{4} :: {5}", indent, candidateId, prefix, record.EvidenceType, bucketPart, record.Rationale);
            }
        }

        // Final, unambiguous statement of what actually drove the decision --
        // contributing evidence only, opportunistic evidence deliberately omitted
        // here (it's already visible inline, tagged, above). 2026-07-17 directive.
        // NOTE 2026-07-17: an internal LogDecisionSummary used to fire here, inline,
        // the moment Resolve() finished. Removed -- it duplicated and, worse,
        // pre-empted SmokeTest/Program.cs's own "STAGE: artist evidence summary" ->
        // "STAGE: decision" sequence, making the decision appear to print BEFORE the
        // evidence summary in the smoke test output. The decision-after-evidence
        // summary now lives solely in SmokeTest/Program.cs (PrintScoreboard then
        // PrintDecision), which already had the ordering right.

        private MatchResult ScoreAndDecide(TSourceEntity source, List<Candidate> candidates, Dictionary<string, List<EvidenceRecord>> evidenceByCandidate, ScoringConfig config)
        {
            // Contributing=false (opportunistic) evidence is logged and saved for
            // later "does it add value" analysis but must NEVER affect the actual
            // decision -- see EvidenceRecord.Contributing, 2026-07-17 directive.
            var scored = candidates.Select(c => _scorer.Score(c, evidenceByCandidate[c.Id].Where(e => e.Contributing), config)).ToList();
            return _decisionGate.Decide(scored, config, source.SourceSystem, source.SourceId);
        }
    }
}