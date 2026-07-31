namespace MetadataHealthCheck.v2.Core.Interfaces
{
    /// <summary>
    /// One unit the sequential sampler can draw an observation from. Opaque to
    /// Core: a resolver defines what a unit represents and what its BucketKey means.
    /// </summary>
    public interface IObservationUnit
    {
        string BucketKey { get; }

        string Describe();
    }
}
