using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface ISourceEntityProvider<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        IEnumerable<TSourceEntity> GetAll(ResolutionContext context);
    }
}
