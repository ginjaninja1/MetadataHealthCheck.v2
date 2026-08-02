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
    //   3. Gather evidence, a bit at a time                   -- the three
    //      *EvidenceCollectors members below
    //   4. Keep score as evidence comes in, stop early when   -- Scorer +
    //      confident                                            DecisionGate
    //      (both driven by Core's SequentialSampler -- you never write this part)
    //   5. Record the decision                                 -- IMatchRepository
    //      (also entirely Core's job, not the resolver's)
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

        // Stage 3, timing option A: called once per candidate, before any
        // observation sampling starts, no observation unit involved at all.
        // Use for anything computable from the candidate alone (e.g. name
        // similarity). Empty array if you have none.
        IEnumerable<ICandidateEvidenceCollector<TSourceEntity>> CandidateEvidenceCollectors { get; }

        // Stage 3, timing option B: called once per (candidate, observation
        // unit) pair, independently, as the sampler draws units. Use when
        // each candidate needs its own separate lookup per unit. Empty array
        // if your entity type has no per-unit evidence at all.
        IEnumerable<IPerUnitEvidenceCollector<TSourceEntity>> PerUnitEvidenceCollectors { get; }

        // Stage 3, timing option C: called once per observation unit, given
        // ALL live candidates together, so one shared lookup can be checked
        // against every candidate at once instead of repeating it per
        // candidate. Empty array if you have no collectors of this shape.
        IEnumerable<IJointCandidateEvidenceCollector<TSourceEntity>> JointCandidateEvidenceCollectors { get; }

        // Supplies the observation units (stage 3's "one bit of evidence at a
        // time" unit -- a track, a cast credit, whatever your domain's
        // natural unit is), grouped into priority-ordered buckets. Null if
        // your entity type has no observation/unit concept at all -- the
        // sampler then scores stage-3-option-A evidence once and that's the
        // final answer, with options B and C never invoked.
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
    }
}