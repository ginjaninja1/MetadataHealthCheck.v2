using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Diagnostics;

namespace MetadataHealthCheck.v2.Http
{
    /// <summary>
    /// Base class for a resolver's REST transport client that wants response
    /// caching as a natural consequence of inheriting it, not something the
    /// resolver author builds or wires themselves. A concrete client:
    ///   - inherits this class,
    ///   - supplies CallNameToCacheTable (which cache table each of its own
    ///     callName labels -- the same strings it already passes into Get()
    ///     at every call site -- belongs to),
    ///   - supplies DefaultCacheTtl for its own API's data-churn profile,
    ///   - and otherwise writes its endpoint methods exactly as it would
    ///     without caching, calling the inherited Get(...).
    ///
    /// Caching lives at this one choke point because every call already
    /// funnels through it (confirmed against HttpMusicBrainzApiClient before
    /// this class existed) -- there is no separate decorator class to
    /// assemble, and no caching-specific code in any concrete client's own
    /// endpoint methods.
    /// </summary>
    public abstract class CachedHttpApiClientBase : IDisposable
    {
        private readonly HttpClient _http;
        private readonly IApiResponseCache _cache;
        private readonly string _baseUrl;

        /// <summary>
        /// Exposed so a concrete client's own endpoint methods can log directly
        /// (e.g. same-run in-memory shortcut hits distinct from this base
        /// class's own transport/persistent-cache logging).
        /// </summary>
        protected StructuredLogger Logger { get; }

        /// <summary>Live HTTP calls only -- a cache hit never increments this.</summary>
        public int TotalApiCalls { get; private set; }
        public Dictionary<string, int> ApiCallsByType { get; } = new();

        /// <summary>Tracked separately from TotalApiCalls so cache effectiveness is visible on its own.</summary>
        public int TotalCacheHits { get; private set; }
        public Dictionary<string, int> CacheHitsByType { get; } = new();

        protected CachedHttpApiClientBase(string baseUrl, IApiResponseCache cache, StructuredLogger logger)
        {
            _baseUrl = baseUrl;
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _cache = cache;
            Logger = logger;
        }

        /// <summary>
        /// Maps this client's callName labels to the cache table their
        /// responses belong in. A callName with no entry here is deliberately
        /// NOT cached (logged as a warning, not silently defaulted to some
        /// shared table) -- every call site is a conscious inclusion.
        /// </summary>
        protected abstract IReadOnlyDictionary<string, string> CallNameToCacheTable { get; }

        /// <summary>Null means cache indefinitely (no expiry recorded).</summary>
        protected abstract TimeSpan? DefaultCacheTtl { get; }

        protected string? Get(string relativeUrl, string callName, string callDescription)
        {
            if (!CallNameToCacheTable.TryGetValue(callName, out var table))
            {
                Logger.Warn("ApiCache", "[{0}] no cache table mapped -- proceeding uncached. Add an entry to CallNameToCacheTable.", callName);
                return FetchLive(relativeUrl, callName, callDescription);
            }

            var cached = _cache.Get(table, relativeUrl);
            if (cached != null)
            {
                TotalCacheHits++;
                CacheHitsByType[callName] = CacheHitsByType.TryGetValue(callName, out var hits) ? hits + 1 : 1;
                Logger.Info("ApiCache", "[{0}] cache hit, table={1}", callName, table);
                Logger.Info("ApiCache", "  (avoided GET {0}{1})", _baseUrl, relativeUrl);
                return cached;
            }

            var body = FetchLive(relativeUrl, callName, callDescription);
            if (body != null)
                _cache.Set(table, relativeUrl, body, DefaultCacheTtl);
            return body;
        }

        /// <summary>
        /// Forces the next Get for this callName+url to miss and refetch.
        /// Intended for clients whose data changes mid-run (e.g. a future
        /// Emby-facing client) -- not currently called by
        /// HttpMusicBrainzApiClient, whose data is calendar-TTL'd instead.
        /// </summary>
        protected void InvalidateCached(string callName, string relativeUrl)
        {
            if (CallNameToCacheTable.TryGetValue(callName, out var table))
                _cache.Invalidate(table, relativeUrl);
        }

        private string? FetchLive(string relativeUrl, string callName, string callDescription)
        {
            TotalApiCalls++;
            ApiCallsByType[callName] = ApiCallsByType.TryGetValue(callName, out var n) ? n + 1 : 1;
            Logger.Info("HttpApi", "[{0}] {1}", callName, callDescription);
            Logger.Info("HttpApi", "  GET {0}{1}", _baseUrl, relativeUrl);
            try
            {
                var response = _http.GetAsync(relativeUrl).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    var location = response.Headers.Location?.ToString() ?? "(none)";
                    Logger.Warn("HttpApi", "  -> HTTP {0} for {1} -- Location: {2}", (int)response.StatusCode, relativeUrl, location);
                    return null;
                }
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.ErrorException("HttpApi", $"  -> request failed for {relativeUrl}", ex);
                return null;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}