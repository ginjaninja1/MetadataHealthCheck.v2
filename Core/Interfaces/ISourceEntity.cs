namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface ISourceEntity
    {
        string SourceSystem { get; }
        string EntityType { get; }
        string SourceId { get; }
        string DisplayName { get; }
    }
}
