using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Core.Engine
{
    /// <summary>
    /// Phase 1 slice of §3.2's pipeline: identity cache check → candidate
    /// generation (Strategy A then B, priority order) → evidence collection
    /// (all evidence collectors run to completion, no early-stop) → simple
    /// weighted-sum scoring → decision gate → repository writes.
    ///
    /// The Sequential Sampler (§5.5) — one-observation-at-a-time with early
    /// stopping — is a Phase 2 addition (§21). This engine collects all
    /// available evidence per candidate before scoring, which is a valid but
    /// less API-efficient predecessor behavior, consistent with "Goal: one
    /// artist resolved end-to-end, logged, stored" for Phase 1.
    /// </summary>
    public class ResolutionEngine
    {
        private readonly IResolverPlugin<Sources.Emby.EmbyArtist> _plugin;
        private readonly IMatchRepository _repository;
        private readonly IIdentityCache _identityCache;
        private readonly ScoringConfig _scoringConfig;
        private readonly StructuredLogger _logger;

        public ResolutionEngine(
            IResolverPlugin<Sources.Emby.EmbyArtist> plugin,
            IMatchRepository repository,
            IIdentityCache identityCache,
            ScoringConfig scoringConfig,
            StructuredLogger logger)
        {
            _plugin = plugin;
            _repository = repository;
            _identityCache = identityCache;
            _scoringConfig = scoringConfig;
            _logger = logger;
        }

        public MatchResult ResolveOne(Sources.Emby.EmbyArtist artist, ResolutionContext context)
        {
            var cached = _identityCache.Get(artist.SourceSystem, artist.SourceId, _plugin.TargetSystem);
            if (cached != null)
            {
                _logger.Info("Engine", "Identity cache hit for {0} -> {1}, reusing.", artist.DisplayName, cached.TargetId);
                return cached;
            }

            var candidates = _plugin.Strategies
                .OrderBy(s => s.Priority)
                .SelectMany(s => s.GenerateCandidates(artist, context))
                .ToList();

            _logger.Debug("CandidateGen", "{0} candidates generated for {1}.", candidates.Count, artist.DisplayName);

            var scored = new List<ScoredCandidate>();
            foreach (var candidate in candidates)
            {
                _repository.SaveCandidate(candidate);

                var evidence = new List<EvidenceRecord>();
                foreach (var collector in _plugin.EvidenceCollectors)
                {
                    var record = collector.Collect(artist, candidate, context);
                    if (record == null) continue;
                    evidence.Add(record);
                    _repository.SaveEvidence(record);
                    _logger.Debug("Evidence", "[{0}] {1} :: {2}", candidate.TargetId, record.EvidenceType, record.Rationale);
                }

                var result = _plugin.Scorer.Score(candidate, evidence, _scoringConfig);
                scored.Add(result);
                _logger.Debug("Scorer", "Candidate {0} running_llr={1:F2} confidence={2:F2}", candidate.TargetId, result.RunningLlr, result.Confidence);
            }

            var decision = _plugin.DecisionGate.Decide(scored, _scoringConfig, artist.SourceSystem, artist.SourceId);
            _repository.SaveMatchResult(decision);

            if (decision.Status == "auto_accept")
                _identityCache.Set(artist.SourceSystem, artist.SourceId, decision.TargetSystem, decision.TargetId, decision.Confidence);

            _logger.Info("Engine", "{0} -> {1} [{2}], confidence={3:F2}", artist.DisplayName, decision.TargetId, decision.Status, decision.Confidence);
            return decision;
        }
    }
}
