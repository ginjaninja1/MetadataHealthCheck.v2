using System.Globalization;
using MetadataHealthCheck.v2.Diagnostics;
using SQLitePCL.pretty;

namespace MetadataHealthCheck.v2.Storage.Sqlite
{
    /// <summary>
    /// Mirrors Emby.AutoOrganize's Data/BaseSqliteRepository.cs pattern
    /// (confirmed against the reference plugin's actual source, not assumed
    /// from the spec's description alone) — single shared write connection,
    /// ReaderWriterLockSlim guard, WAL + synchronous=Normal on init, the
    /// GetColumnNames/AddColumn migration idiom (§9).
    ///
    /// Deviates from the reference in one place: takes this project's own
    /// StructuredLogger instead of MediaBrowser.Model.Logging.ILogger, since
    /// wiring against the real Emby-hosted ILogger requires building inside an
    /// actual Emby server install (§15.1's listed unverified item). Everything
    /// SQLite-specific — the actual subject of this pattern — is otherwise a
    /// direct match.
    /// </summary>
    public abstract class BaseSqliteRepository : IDisposable
    {
        protected string DbFilePath { get; set; } = "";
        protected ReaderWriterLockSlim WriteLock;
        protected StructuredLogger Logger { get; }

        protected BaseSqliteRepository(StructuredLogger logger)
        {
            Logger = logger;
            WriteLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        protected TransactionMode TransactionMode => TransactionMode.Deferred;
        protected TransactionMode ReadTransactionMode => TransactionMode.Deferred;

        private static bool _versionLogged;
        protected IDatabaseConnection? _connection;

        protected virtual bool EnableTempStoreMemory => false;
        protected virtual int? CacheSize => null;

        protected IDatabaseConnection CreateConnection(bool isReadOnly = false)
        {
            if (_connection != null)
                return _connection.Clone(false);

            lock (WriteLock)
            {
                if (!_versionLogged)
                {
                    _versionLogged = true;
                    Logger.Info("Storage", "Sqlite version: {0}", SQLite3.Version);
                }

                var connectionFlags = ConnectionFlags.Create | ConnectionFlags.ReadWrite
                    | ConnectionFlags.PrivateCache | ConnectionFlags.NoMutex;

                var db = SQLite3.Open(DbFilePath, connectionFlags, null, false);

                try
                {
                    var queries = new List<string> { "PRAGMA synchronous=Normal" };
                    if (CacheSize.HasValue)
                        queries.Add("PRAGMA cache_size=" + CacheSize.Value.ToString(CultureInfo.InvariantCulture));
                    queries.Add(EnableTempStoreMemory ? "PRAGMA temp_store = memory" : "PRAGMA temp_store = file");
                    db.ExecuteAll(string.Join(";", queries));
                }
                catch
                {
                    db.Dispose();
                    throw;
                }

                if (!isReadOnly)
                    _connection = db;

                return db;
            }
        }

        protected void RunDefaultInitialization(IDatabaseConnection db)
        {
            var queries = new List<string>
            {
                "PRAGMA journal_mode=WAL",
                "PRAGMA page_size=4096",
                "PRAGMA synchronous=Normal",
                EnableTempStoreMemory ? "pragma temp_store = memory" : "pragma temp_store = file",
            };
            db.ExecuteAll(string.Join(";", queries));
        }

        protected List<string> GetColumnNames(IDatabaseConnection connection, string table)
        {
            var list = new List<string>();
            using var statement = connection.PrepareStatement($"PRAGMA table_info({table})");
            foreach (var row in statement.ExecuteQuery())
            {
                if (!row.IsDBNull(1))
                    list.Add(row.GetString(1));
            }
            return list;
        }

        protected bool AddColumn(IDatabaseConnection connection, string table, string columnName, string type, List<string> existingColumnNames)
        {
            if (existingColumnNames.Contains(columnName, StringComparer.OrdinalIgnoreCase))
                return false;

            connection.Execute($"alter table {table} add column {columnName} {type} NULL");
            return true;
        }

        public void Dispose()
        {
            DisposeConnection();
        }

        private readonly object _disposeLock = new();
        private void DisposeConnection()
        {
            try
            {
                lock (_disposeLock)
                {
                    WriteLock.EnterWriteLock();
                    try
                    {
                        if (_connection != null)
                        {
                            _connection.Close();
                            _connection.Dispose();
                            _connection = null;
                        }
                    }
                    finally
                    {
                        WriteLock.ExitWriteLock();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.ErrorException("Storage", "Error disposing database", ex);
            }
        }
    }
}
