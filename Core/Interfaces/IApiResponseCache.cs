namespace MetadataHealthCheck.v2.Core.Interfaces
{
    /// <summary>
    /// Generic cache-aside store for raw REST API responses, keyed by
    /// (table, url). "Table" is a caller-chosen partition -- typically one per
    /// distinct call type (e.g. "musicbrainz_artists") -- not an inherently
    /// SQL concept: an in-memory implementation is free to treat it as just
    /// another key segment. Storing the raw response body (not a deserialized
    /// object) means a later change to a client's DTO/parsing logic doesn't
    /// invalidate anything already cached, since the cache never depends on
    /// that shape.
    /// </summary>
    public interface IApiResponseCache
    {
        /// <summary>Null if missing or expired.</summary>
        string? Get(string table, string url);

        /// <summary>ttl == null means cache indefinitely (no expiry recorded/checked).</summary>
        void Set(string table, string url, string response, TimeSpan? ttl);

        /// <summary>Forces the next Get for this (table, url) to miss.</summary>
        void Invalidate(string table, string url);
    }
}