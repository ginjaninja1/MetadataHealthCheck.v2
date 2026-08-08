using MetadataHealthCheck.v2.Core.Interfaces;

namespace MetadataHealthCheck.v2.Storage
{
    /// <summary>
    /// In-memory IApiResponseCache, scoped to this process's lifetime. Intended
    /// for sources whose data changes regularly within a single run (e.g. a
    /// future live Emby transport client) rather than MusicBrainz's
    /// calendar-TTL persistent case - see ApiResponseCacheRepository for that.
    ///
    /// ttl is deliberately ignored on Set/SetFailure: the cache's own lifetime
    /// (the run) already bounds staleness. SetTtl is a no-op for the same
    /// reason - there's no expiry to correct. A caller that knows a specific
    /// entry is stale mid-run should call Invalidate to force the next Get to
    /// miss, rather than relying on any time-based expiry.
    /// </summary>
    public class InMemoryApiResponseCache : IApiResponseCache
    {
        private readonly Dictionary<(string Table, string Url), (bool IsFailure, string Value)> _store = new();

        public ApiCacheLookupResult Get(string table, string url)
        {
            if (!_store.TryGetValue((table, url), out var entry))
                return ApiCacheLookupResult.Miss();

            return entry.IsFailure
                ? ApiCacheLookupResult.Failure(entry.Value)
                : ApiCacheLookupResult.Success(entry.Value);
        }

        public void Set(string table, string url, string response, TimeSpan? ttl)
            => _store[(table, url)] = (false, response);

        public void SetFailure(string table, string url, string reason, TimeSpan? ttl)
            => _store[(table, url)] = (true, reason);

        public void SetTtl(string table, string url, TimeSpan? ttl)
        {
            // No-op: see class doc comment.
        }

        public void Invalidate(string table, string url)
            => _store.Remove((table, url));
    }
}