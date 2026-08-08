namespace MetadataHealthCheck.v2.Core.Interfaces
{
    /// <summary>Which of the three states a cache lookup landed in. A plain
    /// string? (as this interface used before) can't distinguish "no entry"
    /// from "entry exists, but it's a recorded failure" -- both would look
    /// like null to the caller, which is exactly wrong once failures are
    /// cached deliberately (see ApiCacheEntryKind.Failure below) rather than
    /// simply never written.</summary>
    public enum ApiCacheEntryKind
    {
        Miss,
        Success,
        Failure
    }

    /// <summary>Result of a single cache lookup. Response is only meaningful
    /// when Kind == Success; FailureReason only when Kind == Failure.</summary>
    public readonly struct ApiCacheLookupResult
    {
        public ApiCacheEntryKind Kind { get; }
        public string? Response { get; }
        public string? FailureReason { get; }

        private ApiCacheLookupResult(ApiCacheEntryKind kind, string? response, string? failureReason)
        {
            Kind = kind;
            Response = response;
            FailureReason = failureReason;
        }

        public static ApiCacheLookupResult Miss() => new(ApiCacheEntryKind.Miss, null, null);
        public static ApiCacheLookupResult Success(string response) => new(ApiCacheEntryKind.Success, response, null);
        public static ApiCacheLookupResult Failure(string reason) => new(ApiCacheEntryKind.Failure, null, reason);
    }

    /// <summary>
    /// Generic cache-aside store for raw REST API responses, keyed by
    /// (table, url). "Table" is a caller-chosen partition - typically one per
    /// distinct call type (e.g. "musicbrainz_artists") - not an inherently
    /// SQL concept: an in-memory implementation is free to treat it as just
    /// another key segment. Storing the raw response body (not a deserialized
    /// object) means a later change to a client's DTO/parsing logic doesn't
    /// invalidate anything already cached, since the cache never depends on
    /// that shape.
    /// </summary>
    public interface IApiResponseCache
    {
        ApiCacheLookupResult Get(string table, string url);

        /// <summary>ttl == null means cache indefinitely (no expiry recorded/checked).</summary>
        void Set(string table, string url, string response, TimeSpan? ttl);

        /// <summary>Records a confirmed failure (e.g. a 404) as its own cache
        /// entry, distinct from a miss, so a subsequent Get for the same URL
        /// returns Failure (skip the live call, the answer is already known)
        /// rather than Miss (try again live). See ApiCacheEntryKind's doc
        /// comment on why this needed a real interface change, not an
        /// overload of Set.</summary>
        void SetFailure(string table, string url, string reason, TimeSpan? ttl);

        /// <summary>Retroactively changes the TTL of an existing entry
        /// (success or failure) without touching its content - used to
        /// correct a failure's TTL once the wider outcome that depended on it
        /// is known (see ArtistMusicBrainzConfig.MusicBrainzApiCacheFailureTtl's
        /// doc comment). A no-op if the entry no longer exists (e.g. it
        /// already expired and was never re-fetched).</summary>
        void SetTtl(string table, string url, TimeSpan? ttl);

        /// <summary>Forces the next Get for this (table, url) to miss.</summary>
        void Invalidate(string table, string url);
    }
}