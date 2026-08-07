using MetadataHealthCheck.v2.Core.Interfaces;

namespace MetadataHealthCheck.v2.Storage
{
    /// <summary>
    /// In-memory IApiResponseCache, scoped to this process's lifetime. Intended
    /// for sources whose data changes regularly within a single run (e.g. a
    /// future live Emby transport client) rather than MusicBrainz's
    /// calendar-TTL persistent case -- see ApiResponseCacheRepository for that.
    ///
    /// ttl is deliberately ignored: the cache's own lifetime (the run) already
    /// bounds staleness. A caller that knows a specific entry is stale mid-run
    /// (e.g. Emby just reported a library change) should call Invalidate to
    /// force the next Get to miss, rather than relying on any time-based expiry.
    /// </summary>
    public class InMemoryApiResponseCache : IApiResponseCache
    {
        private readonly Dictionary<(string Table, string Url), string> _store = new();

        public string? Get(string table, string url)
            => _store.TryGetValue((table, url), out var value) ? value : null;

        public void Set(string table, string url, string response, TimeSpan? ttl)
            => _store[(table, url)] = response;

        public void Invalidate(string table, string url)
            => _store.Remove((table, url));
    }
}