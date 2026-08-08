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
    ///   - supplies DefaultCacheTtl/FailureCacheTtl for its own API's
    ///     data-churn profile,
    ///   - and otherwise writes its endpoint methods exactly as it would
    ///     without caching, calling the inherited Get(...).
    ///
    /// Caching lives at this one choke point because every call already
    /// funnels through it (confirmed against HttpMusicBrainzApiClient before
    /// this class existed) -- there is no separate decorator class to
    /// assemble, and no caching-specific code in any concrete client's own
    /// endpoint methods.
    ///
    /// FAILURE CACHING: a confirmed failure (e.g. an HTTP 404 for an MBID
    /// MusicBrainz's own artist store no longer recognises) is cached too,
    /// not just successes -- it genuinely reflects the state of the remote
    /// data at the time of the call. It's written under FailureCacheTtl
    /// provisionally; whether that's actually the right TTL depends on
    /// something this class can't know by itself (did the wider resolution
    /// this failure was part of ultimately succeed anyway?), so the caller
    /// is expected to call ReconcileFailureTtls once that's known.
    /// </summary>
    public abstract class CachedHttpApiClientBase : IDisposable
    {
        private readonly HttpClient _http;
        private readonly IApiResponseCache _cache;
        private readonly string _baseUrl;

        // (table, url) pairs that were a Failure this run, whether freshly
        // recorded via a live 404 or served from an existing cached failure
        // entry -- either way, whether THIS resolution's outcome ultimately
        // succeeded despite touching it is the signal ReconcileFailureTtls
        // needs, so both cases are tracked identically here. A HashSet, not a
        // List: the same failed URL can legitimately be looked up more than
        // once within one entity's own resolution (e.g. two different
        // evidence collectors touching the same recording), and each one
        // only needs a single TTL correction, not one per lookup.
        private readonly HashSet<(string Table, string Url)> _failuresTouchedThisInstance = new();

        /// <summary>
        /// Exposed so a concrete client's own endpoint methods can log directly
        /// (e.g. same-run in-memory shortcut hits distinct from this base
        /// class's own transport/persistent-cache logging).
        /// </summary>
        protected StructuredLogger Logger { get; }

        /// <summary>
        /// Free-text label (e.g. "Paul Francis Webster (73d73e65...)") naming
        /// the source entity currently being resolved. Set once per entity by
        /// the caller (each entity gets its own client instance in
        /// BatchHarness/SmokeTest) so a failure log line can say WHICH
        /// resolution triggered a given live call, not just the bare URL --
        /// without this there was no way to tell, from the log alone, whether
        /// a failing MBID came from a candidate search result or somewhere
        /// else, or which entity to go re-check in MusicBrainz directly.
        /// </summary>
        public string? ResolutionContextLabel { get; set; }

        /// <summary>Live HTTP calls only -- a cache hit never increments this.</summary>
        public int TotalApiCalls { get; private set; }
        public Dictionary<string, int> ApiCallsByType { get; } = new();

        /// <summary>Tracked separately from TotalApiCalls so cache effectiveness is visible on its own.</summary>
        public int TotalCacheHits { get; private set; }
        public Dictionary<string, int> CacheHitsByType { get; } = new();

        /// <summary>Live HTTP calls that did not succeed -- a distinct signal from
        /// TotalApiCalls. Unlike before, a failure IS now cached (see class
        /// doc comment), so this counts fresh live failures only, not
        /// failures served from cache on a rerun.</summary>
        public int TotalFailures { get; private set; }
        public Dictionary<string, int> FailuresByType { get; } = new();

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

        /// <summary>Null means cache indefinitely (no expiry recorded). Applied to
        /// successful responses, and provisionally to fresh failures until
        /// ReconcileFailureTtls corrects them.</summary>
        protected abstract TimeSpan? DefaultCacheTtl { get; }

        /// <summary>TTL applied to a failure once the resolution it was part of
        /// is known NOT to have matched (see ReconcileFailureTtls). Deliberately
        /// separate from DefaultCacheTtl so a concrete client can pick a much
        /// shorter window for "this is worth re-checking soon" than for
        /// "this data rarely changes."</summary>
        protected abstract TimeSpan? FailureCacheTtl { get; }

        protected string? Get(string relativeUrl, string callName, string callDescription)
        {
            if (!CallNameToCacheTable.TryGetValue(callName, out var table))
            {
                Logger.Warn("ApiCache", "[{0}] no cache table mapped -- proceeding uncached. Add an entry to CallNameToCacheTable.", callName);
                var (liveBody, _) = FetchLive(relativeUrl, callName, callDescription);
                return liveBody;
            }

            var cached = _cache.Get(table, relativeUrl);
            switch (cached.Kind)
            {
                case ApiCacheEntryKind.Success:
                    TotalCacheHits++;
                    CacheHitsByType[callName] = CacheHitsByType.TryGetValue(callName, out var hits) ? hits + 1 : 1;
                    Logger.Info("ApiCache", "[{0}] cache hit, table={1}", callName, table);
                    Logger.Info("ApiCache", "  (avoided GET {0}{1})", _baseUrl, relativeUrl);
                    return cached.Response;

                case ApiCacheEntryKind.Failure:
                    TotalCacheHits++;
                    CacheHitsByType[callName] = CacheHitsByType.TryGetValue(callName, out var failHits) ? failHits + 1 : 1;
                    _failuresTouchedThisInstance.Add((table, relativeUrl));
                    Logger.Info("ApiCache", "[{0}] cached failure hit, table={1}, reason={2}", callName, table, cached.FailureReason);
                    Logger.Info("ApiCache", "  (avoided GET {0}{1})", _baseUrl, relativeUrl);
                    return null;

                default: // Miss
                    var (body, failureReason) = FetchLive(relativeUrl, callName, callDescription);
                    if (body != null)
                    {
                        _cache.Set(table, relativeUrl, body, DefaultCacheTtl);
                    }
                    else if (failureReason != null)
                    {
                        // Provisional TTL: DefaultCacheTtl, not FailureCacheTtl.
                        // We don't yet know whether the wider resolution this
                        // call was part of will succeed anyway -- see
                        // ReconcileFailureTtls, which corrects this once that's
                        // known. Starting short and only lengthening on
                        // success would risk under-caching in the common case;
                        // starting long and shortening on confirmed failure is
                        // the safer default direction.
                        _cache.SetFailure(table, relativeUrl, failureReason, DefaultCacheTtl);
                        _failuresTouchedThisInstance.Add((table, relativeUrl));
                    }
                    return body;
            }
        }

        /// <summary>
        /// Corrects the TTL of every (table, url) failure this client instance
        /// touched -- whether freshly recorded this run or served from an
        /// existing cached failure entry -- based on whether the resolution
        /// they were part of ultimately succeeded. Binary re-affirmation, not
        /// escalating decay: every call simply re-applies whichever TTL
        /// matches THIS resolution's own outcome (last-write-wins), so a
        /// failure that keeps being touched by resolutions that keep not
        /// matching stays on the short TTL, while one touched by a resolution
        /// that matched anyway relaxes back to the long default -- no
        /// failure-streak counter, deliberately, per discussion.
        /// </summary>
        public void ReconcileFailureTtls(bool matched)
        {
            var ttl = matched ? DefaultCacheTtl : FailureCacheTtl;
            foreach (var (table, url) in _failuresTouchedThisInstance)
                _cache.SetTtl(table, url, ttl);
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

        /// <summary>Returns (body, null) on success, or (null, reason) on failure.</summary>
        private (string? Body, string? FailureReason) FetchLive(string relativeUrl, string callName, string callDescription)
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
                    var reason = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) -- Location: {location}";
                    Logger.Warn("HttpApi", "  -> {0} for {1}", reason, relativeUrl);
                    RecordFailure(callName, relativeUrl, reason);
                    return (null, reason);
                }
                return (response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), null);
            }
            catch (Exception ex)
            {
                var reason = $"{ex.GetType().Name}: {ex.Message}";
                Logger.ErrorException("HttpApi", $"  -> request failed for {relativeUrl}", ex);
                RecordFailure(callName, relativeUrl, reason);
                return (null, reason);
            }
        }

        private void RecordFailure(string callName, string relativeUrl, string reason)
        {
            TotalFailures++;
            FailuresByType[callName] = FailuresByType.TryGetValue(callName, out var n) ? n + 1 : 1;
            // Deliberately bypasses Logger's own writeToConsole setting: a
            // failure is worth seeing even when a caller (BatchHarness, one
            // logger per worker) has suppressed this logger's routine
            // per-artist trace output. Not retried -- see the earlier design
            // discussion on this (retry-worthiness varies by status code and
            // wasn't bundled into this change).
            lock (Console.Out)
            {
                var context = ResolutionContextLabel != null ? $"[{ResolutionContextLabel}] " : "";
                Console.WriteLine($"[FAILURE] {context}[{callName}] {reason} -- {_baseUrl}{relativeUrl}");
            }
        }

        public void Dispose() => _http.Dispose();
    }
}