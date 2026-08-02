using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Core.Engine
{
    /// <summary>
    /// Top-level resolution pipeline: identity cache check, candidate generation
    /// (strategies in priority order), delegation to the resolver's own
    /// IResolutionStrategy, then repository writes. Generic over both the
    /// source entity type and the resolver's own config type, so Core never
    /// references a specific resolver -- and has no knowledge of how the
    /// resolver's strategy works internally (sequential sampling, one-shot
    /// scoring, or anything else). See Architecture-Layers.md.
    /// </summary>
    public class ResolutionEngine<TSourceEntity, TConfig>
        where TSourceEntity : ISourceEntity
        where TConfig : IScoringConfig
    {
        private readonly IResolverPlugin<TSourceEntity, TConfig> _plugin;
        private readonly IMatchRepository _repository;
        private readonly IIdentityCache _identityCache;
        private readonly TConfig _scoringConfig;
        private readonly StructuredLogger _logger;

        public ResolutionEngine(
            IResolverPlugin<TSourceEntity, TConfig> plugin,
            IMatchRepository repository,
            IIdentityCache identityCache,
            TConfig scoringConfig,
            StructuredLogger logger)
        {
            _plugin = plugin;
            _repository = repository;
            _identityCache = identityCache;
            _scoringConfig = scoringConfig;
            _logger = logger;
        }

        public MatchResult ResolveOne(TSourceEntity source, ResolutionContext context)
        {
            var cached = _identityCache.Get(source.SourceSystem, source.SourceId, _plugin.TargetSystem);
            if (cached != null)
            {
                _logger.Info("Engine", "Identity cache hit for {0} -> {1}, reusing.", source.DisplayName, cached.TargetId);
                return cached;
            }

            var candidates = _plugin.Strategies
                .OrderBy(s => s.Priority)
                .SelectMany(s => s.GenerateCandidates(source, context))
                .ToList();

            _logger.Debug("CandidateGen", "{0} candidates generated for {1}.", candidates.Count, source.DisplayName);

            foreach (var candidate in candidates)
                _repository.SaveCandidate(candidate);

            var decision = _plugin.Strategy.Resolve(source, candidates, _scoringConfig, _repository, context);

            // Give any resolver-set ForcedReviewSignal the final say over the
            // decision gate's own output, checked before the auto-accept
            // identity-cache write below so a forced review is never cached as
            // confirmed. Core has no idea what triggered it (that's entirely
            // the resolver's business) -- it only knows the mechanism.
            var forcedReview = context.GetExtension<ForcedReviewSignal>();
            if (forcedReview != null && decision.Status != "needs_review")
            {
                _logger.Info("Engine",
                    "Overriding decision status '{0}' -> 'needs_review' for {1}: {2}. {3}",
                    decision.Status, source.DisplayName, forcedReview.Reason, string.Join(" ", forcedReview.Notes));
                decision.Status = "needs_review";
                decision.DecisionReason = forcedReview.Reason;
            }

            _repository.SaveMatchResult(decision);

            if (decision.Status == "auto_accept")
                _identityCache.Set(source.SourceSystem, source.SourceId, decision.TargetSystem, decision.TargetId, decision.Confidence);

            return decision;
        }
    }
}