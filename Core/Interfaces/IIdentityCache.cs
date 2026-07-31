using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Core.Interfaces
{
    public interface IIdentityCache
    {
        MatchResult? Get(string sourceSystem, string sourceId, string targetSystem);
        void Set(string sourceSystem, string sourceId, string targetSystem, string targetId, double confidence);
    }
}
