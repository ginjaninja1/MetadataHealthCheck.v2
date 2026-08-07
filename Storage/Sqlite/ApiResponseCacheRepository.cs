using System.Text.RegularExpressions;
using MetadataHealthCheck.v2.Core.Interfaces;
using MetadataHealthCheck.v2.Diagnostics;
using SQLitePCL.pretty;

namespace MetadataHealthCheck.v2.Storage.Sqlite
{
    /// <summary>
    /// Persistent IApiResponseCache. Deliberately its own database FILE (not a
    /// few extra tables on MatchRepository's db): this data is disposable and
    /// regenerable, unlike resolution_candidates/evidence/match_results (the
    /// actual audit trail), so it can be deleted/reinitialized independently
    /// without touching anything permanent. An admin can delete the file
    /// directly; Initialize() (called from the constructor, same idiom as
    /// MatchRepository) recreates whatever tables get used from nothing.
    ///
    /// One table per call type (e.g. "musicbrainz_artists",
    /// "musicbrainz_recordingswithrelationships"), not one shared table:
    /// callers choose the table name (see CachedHttpApiClientBase's
    /// CallNameToCacheTable), created lazily on first use here. This buys
    /// per-call-type visibility/size and independent drop-and-reinit in a
    /// plain SQLite browser - it does NOT meaningfully change lookup speed
    /// versus one shared indexed table; that was a deliberately rejected
    /// justification, see conversation history.
    ///
    /// THREAD SAFETY: intended to be constructed ONCE and shared across every
    /// concurrent caller (e.g. every BatchHarness worker). Every public method
    /// below wraps its ENTIRE body in WriteLock.Read()/Write() (see
    /// ReaderWriterLockSlimExtensions), matching the exact granularity of the
    /// reference plugin Emby.AutoOrganize's own repository (confirmed via
    /// decompile), because BaseSqliteRepository.CreateConnection hands out
    /// Clone()d wrappers that all share ONE underlying native sqlite3 handle
    /// (Clone() does not open an independent native connection), opened under
    /// ConnectionFlags.NoMutex. An earlier, narrower fix here that only
    /// guarded table-creation DDL was insufficient - the real production
    /// incident was a native AccessViolationException on an ordinary SELECT,
    /// not just on CREATE TABLE, under 16-way BatchHarness concurrency.
    ///
    /// KNOWN, UNRESOLVED, LOW-SEVERITY: BaseSqliteRepository.CreateConnection
    /// itself has a narrow race on the very first call across multiple
    /// threads (checks _connection != null before acquiring its own lock, not
    /// inside it) - inherited unchanged from the reference plugin, not
    /// something introduced here. Worst case is one leaked, never-disposed
    /// native connection during startup, not a crash - lower severity than
    /// what this class's own locking above addresses, so left alone rather
    /// than silently "fixed" without discussing whether to touch the borrowed
    /// base class at all.
    /// </summary>
    public class ApiResponseCacheRepository : BaseSqliteRepository, IApiResponseCache
    {
        // Table names come only from each client's own CallNameToCacheTable
        // dictionary (developer-authored, not user/request input), but this is
        // still string-interpolated directly into DDL/DML, so it's validated
        // defensively rather than trusted blindly.
        private static readonly Regex ValidTableName = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

        private readonly HashSet<string> _initializedTables = new();

        public ApiResponseCacheRepository(string dbPath, StructuredLogger logger) : base(logger)
        {
            DbFilePath = dbPath;
            using var connection = CreateConnection();
            RunDefaultInitialization(connection);
        }

        private void EnsureTable(IDatabaseConnection connection, string table)
        {
            // No locking here: callers (Get/Set/Invalidate below) each hold
            // WriteLock for their entire body, matching the granularity the
            // reference plugin's own repository uses around every one of its
            // methods -- not just around table creation, which was an earlier,
            // narrower fix that helped but didn't fully solve a real
            // AccessViolationException under 16-way BatchHarness concurrency.
            // See ReaderWriterLockSlimExtensions's doc comment for why.
            if (_initializedTables.Contains(table)) return;

            if (!ValidTableName.IsMatch(table))
                throw new ArgumentException($"Invalid cache table name: \"{table}\". Must match {ValidTableName}.", nameof(table));

            connection.Execute(
                $"CREATE TABLE IF NOT EXISTS {table} (" +
                "url TEXT PRIMARY KEY, response TEXT NOT NULL, cached_at TEXT NOT NULL, expires_at TEXT NULL)");
            _initializedTables.Add(table);
        }

        public string? Get(string table, string url)
        {
            // Whole-method-body lock, matching Emby.AutoOrganize's own
            // repository exactly (confirmed via decompile) -- CreateConnection
            // hands out Clone()d wrappers sharing ONE underlying native
            // sqlite3 handle (see ReaderWriterLockSlimExtensions's doc
            // comment), so every operation against it needs to be fully
            // serialized, not just table creation.
            using (WriteLock.Read())
            {
                using var connection = CreateConnection(true);
                EnsureTable(connection, table);

                var sql = $"SELECT response, expires_at FROM {table} WHERE url=@Url";
                using var statement = connection.PrepareStatement(sql);
                statement.TryBind("@Url", url);

                foreach (var row in statement.ExecuteQuery())
                {
                    var response = row.GetString(0);
                    if (!row.IsDBNull(1))
                    {
                        var expiresAt = DateTime.Parse(row.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
                        if (DateTime.UtcNow >= expiresAt)
                        {
                            Logger.Debug("ApiCache", "[{0}] entry expired, treating as miss. url={1}", table, url);
                            return null;
                        }
                    }
                    return response;
                }
                return null;
            }
        }

        public void Set(string table, string url, string response, TimeSpan? ttl)
        {
            using (WriteLock.Write())
            {
                using var connection = CreateConnection();
                EnsureTable(connection, table);

                connection.RunInTransaction(db =>
                {
                    var sql = $"INSERT OR REPLACE INTO {table} (url, response, cached_at, expires_at) " +
                              "VALUES (@Url, @Response, @CachedAt, @ExpiresAt)";
                    using var statement = db.PrepareStatement(sql);
                    statement.TryBind("@Url", url);
                    statement.TryBind("@Response", response);
                    statement.TryBind("@CachedAt", DateTime.UtcNow.ToString("O"));
                    if (ttl.HasValue)
                        statement.TryBind("@ExpiresAt", DateTime.UtcNow.Add(ttl.Value).ToString("O"));
                    else
                        statement.TryBindNull("@ExpiresAt");
                    statement.MoveNext();
                }, TransactionMode);
            }
        }

        public void Invalidate(string table, string url)
        {
            using (WriteLock.Write())
            {
                using var connection = CreateConnection();
                EnsureTable(connection, table);

                connection.RunInTransaction(db =>
                {
                    using var statement = db.PrepareStatement($"DELETE FROM {table} WHERE url=@Url");
                    statement.TryBind("@Url", url);
                    statement.MoveNext();
                }, TransactionMode);
            }
        }
    }
}