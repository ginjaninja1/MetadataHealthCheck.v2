using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Core.Model;

namespace MetadataHealthCheck.v2.Storage
{
    /// <summary>
    /// Phase 1 stand-in for §9's identity_cache table. Real persistence lands
    /// with Phase 4 (§21: "Active learning + calibration ... identity cache").
    /// Needed now only so Strategy A (AnchoredRecordingStrategy) has something
    /// to check — on a first run there's nothing cached, so Strategy A
    /// correctly falls through to Strategy B, which is exactly what Phase 1's
    /// skeleton test exercises.
    /// </summary>
    public class InMemoryIdentityCache : IIdentityCache
    {
        private readonly Dictionary<string, MatchResult> _store = new();

        private static string Key(string sourceSystem, string sourceId, string targetSystem) => $"{sourceSystem}|{sourceId}|{targetSystem}";

        public MatchResult? Get(string sourceSystem, string sourceId, string targetSystem)
            => _store.TryGetValue(Key(sourceSystem, sourceId, targetSystem), out var v) ? v : null;

        public void Set(string sourceSystem, string sourceId, string targetSystem, string targetId, double confidence)
        {
            _store[Key(sourceSystem, sourceId, targetSystem)] = new MatchResult
            {
                SourceSystem = sourceSystem,
                SourceId = sourceId,
                TargetSystem = targetSystem,
                TargetId = targetId,
                Confidence = confidence,
                Status = "human_confirmed",
                DecidedAt = DateTime.UtcNow,
            };
        }
    }
}
