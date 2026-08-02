using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // THE FRONT DOOR FOR A NEW RESOLVER.
    //
    // One implementation per (target system, target entity type) pair --
    // e.g. one for "Emby Artist -> MusicBrainz Artist", a separate one for
    // "Emby Artist -> Discogs Artist", a separate one again for "Emby Movie
    // -> TMDB Movie". Implementing this interface, and wiring its members
    // together in a constructor, is the entire job of building a resolver;
    // nothing else in Core needs touching.
    //
    // Every resolver follows the same five-stage recipe regardless of domain:
    //   1. Get the list of source entities to resolve       -- ISourceEntityProvider
    //   2. Guess candidates against the target system        -- Strategies
    //   3. Gather evidence, a bit at a time                   -- EvidenceCollectors
    //   4. Keep score as evidence comes in, stop early when   -- Scorer +
    //      confident                                            DecisionGate
    //      (both driven by whatever Strategy you supply -- see below)
    //   5. Record the decision                                 -- IMatchRepository
    //      (entirely Core's job, not the resolver's)
    //
    // TSourceEntity is your domain's "thing being resolved" (must implement
    // ISourceEntity). TConfig is your own tuning-config type -- Core only
    // ever requires it to satisfy IScoringConfig; add whatever further
    // tunables your resolver needs and only your own Scorer/DecisionGate/
    // strategies will ever see them.
    public interface IResolverPlugin<TSourceEntity, TConfig>
        where TSourceEntity : ISourceEntity
        where TConfig : IScoringConfig
    {
        // Identifies this resolver's (target system, target entity type) pair
        // for logging, the identity cache, and match-result records.
        string TargetSystem { get; }
        string TargetEntityType { get; }

        // Stage 2: how to guess candidates. Tried in Priority order; results
        // from every strategy are concatenated (not deduplicated by Core --
        // do that in your own strategy if it matters for your domain).
        IEnumerable<ICandidateGenerationStrategy<TSourceEntity>> Strategies { get; }

        // Stage 3: every evidence collector this resolver has, of any shape.
        // Each collector decides for itself when it has something to say --
        // some only respond to the one no-unit call made before observation
        // sampling starts (e.g. name similarity), others only respond to
        // per-unit calls (e.g. a track/episode/whatever this resolver's
        // observation units are). See IEvidenceCollector.
        IEnumerable<IEvidenceCollector<TSourceEntity>> EvidenceCollectors { get; }

        // Supplies the observation units (a track, a cast credit, whatever
        // your domain's natural unit is), grouped into priority-ordered
        // buckets. Null if your entity type has no observation/unit concept
        // at all -- the sampler then scores whatever the no-unit call
        // produced and that's the final answer.
        IObservationUnitProvider<TSourceEntity>? ObservationUnitProvider { get; }

        // Optional per-bucket candidate narrowing (e.g. dropping a candidate
        // from one bucket's evidence gathering because another signal already
        // rules it out for that bucket specifically). Null if your resolver
        // has no such rule for any bucket -- every bucket then sees the full
        // candidate list.
        IBucketCandidateFilter? BucketCandidateFilter { get; }

        // Stage 4: turns accumulated evidence into a running confidence score
        // per candidate, and decides accept/reject/needs_review from the
        // scored candidates. Every resolver must provide both; there's no
        // meaningful null case.
        IBeliefScorer<TConfig> Scorer { get; }
        IDecisionGate<TConfig> DecisionGate { get; }

        // The resolver's own chosen procedure for turning candidates into a
        // MatchResult. Engine calls only this -- it has no idea whether this
        // is sequential sampling, a one-shot scorer, or anything else. A
        // resolver typically builds this from its own Scorer/DecisionGate/
        // EvidenceCollectors/ObservationUnitProvider above (e.g. by
        // constructing a SequentialSampler), but Core never assumes that
        // shape. See Architecture-Layers.md.
        IResolutionStrategy<TSourceEntity, TConfig> Strategy { get; }
    }
}