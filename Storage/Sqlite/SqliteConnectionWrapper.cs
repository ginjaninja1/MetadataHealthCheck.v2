using System.Text;
using static MetadataHealthCheck.v2.Storage.Sqlite.NativeSqlite;

namespace MetadataHealthCheck.v2.Storage.Sqlite
{
    /// <summary>
    /// Single shared write connection (§9: "guarded by a ReaderWriterLockSlim" in
    /// the full design — Phase 1 keeps it simple with a single lock object since
    /// only one artist is ever processed at a time per §12.1's no-concurrency rule
    /// anyway). Sets WAL + synchronous=Normal on open per §9.
    /// </summary>
    internal sealed class SqliteConnectionWrapper : IDisposable
    {
        private readonly IntPtr _db;
        private readonly object _lock = new();

        public SqliteConnectionWrapper(string path)
        {
            var rc = sqlite3_open(path, out _db);
            ThrowIfError(_db, rc);
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
        }

        public void Execute(string sql, params (int index, object? value)[] binds)
        {
            lock (_lock)
            {
                var rc = sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero);
                ThrowIfError(_db, rc);
                try
                {
                    Bind(stmt, binds);
                    rc = sqlite3_step(stmt);
                    ThrowIfError(_db, rc);
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
        }

        public List<Dictionary<string, string?>> Query(string sql, params (int index, object? value)[] binds)
        {
            lock (_lock)
            {
                var rows = new List<Dictionary<string, string?>>();
                var rc = sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero);
                ThrowIfError(_db, rc);
                try
                {
                    Bind(stmt, binds);
                    while (true)
                    {
                        rc = sqlite3_step(stmt);
                        if (rc == SQLITE_DONE) break;
                        ThrowIfError(_db, rc);
                        if (rc != SQLITE_ROW) break;

                        int cols = sqlite3_column_count(stmt);
                        var row = new Dictionary<string, string?>();
                        for (int i = 0; i < cols; i++)
                            row[i.ToString()] = ColumnText(stmt, i);
                        rows.Add(row);
                    }
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
                return rows;
            }
        }

        private static void Bind(IntPtr stmt, (int index, object? value)[] binds)
        {
            foreach (var (index, value) in binds)
            {
                if (value == null) { sqlite3_bind_null(stmt, index); continue; }
                switch (value)
                {
                    case double d: sqlite3_bind_double(stmt, index, d); break;
                    default:
                        var bytes = Encoding.UTF8.GetBytes(value.ToString() ?? "");
                        sqlite3_bind_text(stmt, index, bytes, bytes.Length, new IntPtr(-1)); // SQLITE_TRANSIENT
                        break;
                }
            }
        }

        public void Dispose() => sqlite3_close(_db);
    }
}
