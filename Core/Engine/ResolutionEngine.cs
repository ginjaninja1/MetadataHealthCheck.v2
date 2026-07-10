using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Core.Engine
{
    /// <summary>
    /// §3.2's pipeline: identity cache check → candidate generation (Strategy A
    /// then B, priority order) → the Sequential Sampler (§5.5, Core/Engine/
    /// SequentialSampler.cs) — adaptive, early-stopping evidence collection and
    /// scoring, replacing Phase 1's "collect everything, then score once" loop
    /// — → repository writes.
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

            foreach (var candidate in candidates)
                _repository.SaveCandidate(candidate);

            var sampler = new SequentialSampler<Sources.Emby.EmbyArtist>(
                _plugin.EvidenceCollectors,
                _plugin.ObservationEvidenceCollectors,
                _plugin.ObservationUnitProvider,
                _plugin.Scorer,
                _plugin.DecisionGate,
                _logger);

            var decision = sampler.Resolve(artist, candidates, _scoringConfig, _repository, context);
            _repository.SaveMatchResult(decision);

            if (decision.Status == "auto_accept")
                _identityCache.Set(artist.SourceSystem, artist.SourceId, decision.TargetSystem, decision.TargetId, decision.Confidence);

            _logger.Info("Engine", "{0} -> {1} [{2}], confidence={3:F2}", artist.DisplayName, decision.TargetId, decision.Status, decision.Confidence);
            return decision;
        }
    }
}