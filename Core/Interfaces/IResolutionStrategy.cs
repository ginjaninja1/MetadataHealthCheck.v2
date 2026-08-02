using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // The resolver's own chosen procedure for turning a candidate list into a
    // MatchResult. Engine has no opinion on how this works internally --
    // sequential sampling with early stopping (SequentialSampler) is one
    // implementation a resolver may choose; a resolver with no observation-
    // unit or round concept at all may supply a simpler one-shot
    // implementation instead. Engine depends only on this contract, never on
    // SequentialSampler directly. See Architecture-Layers.md.
    public interface IResolutionStrategy<TSourceEntity, TConfig>
        where TSourceEntity : ISourceEntity
        where TConfig : IScoringConfig
    {
        MatchResult Resolve(TSourceEntity source, List<Candidate> candidates, TConfig config, IMatchRepository repository, ResolutionContext context);
    }
}