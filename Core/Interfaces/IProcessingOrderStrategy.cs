using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Optional. A source entity type with no natural processing order returns
    // entities unchanged.
    public interface IProcessingOrderStrategy<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        IEnumerable<TSourceEntity> OrderForProcessing(IEnumerable<TSourceEntity> all);
    }
}
