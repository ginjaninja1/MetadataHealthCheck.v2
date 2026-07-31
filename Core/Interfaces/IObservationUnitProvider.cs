using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    // Optional -- entity types with no natural observation/role concept simply
    // don't provide one; IResolverPlugin.ObservationUnitProvider is nullable for
    // exactly this reason.
    public interface IObservationUnitProvider<TSourceEntity> where TSourceEntity : ISourceEntity
    {
        // Outer sequence: buckets, in priority sampling order. Inner sequence:
        // units within that bucket, already in distance-seeking sample order.
        // The sequential sampler consumes both orderings as given -- it does not
        // re-sort either one itself.
        IEnumerable<IEnumerable<IObservationUnit>> GetOrderedBuckets(TSourceEntity source, ResolutionContext context);
    }
}
