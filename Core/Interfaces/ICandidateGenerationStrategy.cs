using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface ICandidateGenerationStrategy<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        string StrategyName { get; }
        int Priority { get; }
        IEnumerable<Candidate> GenerateCandidates(TSourceEntity source, ResolutionContext context);
    }
}
