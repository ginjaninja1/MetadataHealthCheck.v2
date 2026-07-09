using System.Runtime.InteropServices;
using System.Text;

namespace SQLitePCL.pretty
{
    // ==========================================================================
    // SANDBOX-ONLY TEST DOUBLE. Not part of the shipped plugin.
    //
    // Mirrors the small slice of the real SQLitePCL.pretty public API that
    // MetadataHealthCheck.v2's storage layer uses (matching the pattern
    // confirmed against Emby.AutoOrganize's BaseSqliteRepository /
    // SqliteFileOrganizationRepository). The real NuGet package
    // (SQLitePCL.pretty.core) lives on nuget.org, which this build sandbox
    // cannot reach. This shim exists purely so the actual source can be
    // compiled and exercised here; the production .csproj references the
    // real package and ships zero lines of this file. See Project Log.
    // ==========================================================================

    public enum ConnectionFlags { Create = 1, ReadWrite = 2, ReadOnly = 4, PrivateCache = 8, NoMutex = 16 }
    public enum TransactionMode { Deferred }

    public static class SQLite3
    {
        public static string Version => "3.45.1 (shim over system libsqlite3)";
        public static string[] CompilerOptions => new[] { "SHIM" };

        public static IDatabaseConnection Open(string filename, ConnectionFlags flags, object? vfs, bool ownsHandle)
        {
            var rc = Native.sqlite3_open(filename, out var handle);
            Native.ThrowIfError(handle, rc);
            return new DatabaseConnection(handle);
        }
    }

    public interface IDatabaseConnection : IDisposable
    {
        IStatement PrepareStatement(string sql);
        void Execute(string sql);
        void ExecuteAll(string sql);
        void BeginTransaction(TransactionMode mode);
        void CommitTransaction();
        void RollbackTransaction();
        void Close();
        IDatabaseConnection Clone(bool ownsHandle);
    }

    public interface IStatement : IDisposable
    {
        bool MoveNext();
        IEnumerable<IResultSet> ExecuteQuery();
    }

    public interface IResultSet
    {
        string GetString(int index);
        int GetInt(int index);
        double GetDouble(int index);
        Guid GetGuid(int index);
        bool IsDBNull(int index);
    }

    public static class SqlitePrettyExtensions
    {
        public static void TryBind(this IStatement statement, string name, object? value)
            => ((Statement)statement).Bind(name, value);

        public static void RunInTransaction(this IDatabaseConnection db, Action<IDatabaseConnection> action, TransactionMode mode)
        {
            db.BeginTransaction(mode);
            try
            {
                action(db);
                db.CommitTransaction();
            }
            catch
            {
                db.RollbackTransaction();
                throw;
            }
        }
    }

    internal sealed class DatabaseConnection : IDatabaseConnection
    {
        internal readonly IntPtr Handle;
        private bool _inTransaction;

        public DatabaseConnection(IntPtr handle) => Handle = handle;

        public IStatement PrepareStatement(string sql)
        {
            var rc = Native.sqlite3_prepare_v2(Handle, sql, -1, out var stmt, IntPtr.Zero);
            Native.ThrowIfError(Handle, rc);
            return new Statement(Handle, stmt, sql);
        }

        public void Execute(string sql)
        {
            using var stmt = PrepareStatement(sql);
            stmt.MoveNext();
        }

        public void ExecuteAll(string sql)
        {
            foreach (var part in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                Execute(trimmed);
            }
        }

        public void BeginTransaction(TransactionMode mode)
        {
            if (_inTransaction) return;
            Execute("BEGIN;");
            _inTransaction = true;
        }

        public void CommitTransaction()
        {
            if (!_inTransaction) return;
            Execute("COMMIT;");
            _inTransaction = false;
        }

        public void RollbackTransaction()
        {
            if (!_inTransaction) return;
            Execute("ROLLBACK;");
            _inTransaction = false;
        }

        public void Close() => Native.sqlite3_close(Handle);

        // Real SQLitePCL.pretty clones a read-only connection handle; the shim's
        // single-writer-connection usage in BaseSqliteRepository never actually
        // exercises Clone(false) on a distinct read path in Phase 1's scope, so
        // this just hands back the same wrapped handle.
        public IDatabaseConnection Clone(bool ownsHandle) => this;

        // IMPORTANT: mirrors the reference plugin's actual usage pattern, where
        // every method does `using (var connection = CreateConnection()) {...}`
        // on the SAME shared connection repeatedly, yet the connection is only
        // really torn down later via an explicit .Close() call in
        // DisposeConnection(). That only works if Dispose() (invoked by every
        // `using` block) does NOT close the underlying handle - only Close()
        // does. Confirmed by BaseSqliteRepository.DisposeConnection() calling
        // both `using (_connection) { _connection.Close(); }` - if Dispose()
        // already closed the handle, that explicit Close() would be redundant
        // and every prior `using` block in the codebase would have broken the
        // shared connection after its first use.
        public void Dispose() { /* no-op by design - see remarks above */ }
    }

    internal sealed class Statement : IStatement
    {
        private readonly IntPtr _db;
        private readonly IntPtr _stmt;
        private readonly Dictionary<string, int> _paramIndex = new();

        public Statement(IntPtr db, IntPtr stmt, string sql)
        {
            _db = db;
            _stmt = stmt;
            // Parse @name-style parameters in declared order for binding by name.
            // Uses a regex over the raw SQL text rather than splitting on
            // whitespace/punctuation, since named params can be adjacent to
            // operators with no surrounding space (e.g. "col=@Param").
            int idx = 1;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql, "@[A-Za-z_][A-Za-z0-9_]*"))
            {
                if (!_paramIndex.ContainsKey(m.Value))
                    _paramIndex[m.Value] = idx++;
            }
        }

        public void Bind(string name, object? value)
        {
            if (!_paramIndex.TryGetValue(name, out var index)) return;
            if (value == null) { Native.sqlite3_bind_null(_stmt, index); return; }

            switch (value)
            {
                case double d: Native.sqlite3_bind_double(_stmt, index, d); break;
                case int i: Native.sqlite3_bind_double(_stmt, index, i); break;
                case byte[] blobArr: BindBlob(index, blobArr); break;
                default:
                    var bytes = Encoding.UTF8.GetBytes(value.ToString() ?? "");
                    Native.sqlite3_bind_text(_stmt, index, bytes, bytes.Length, new IntPtr(-1));
                    break;
            }
        }

        private void BindBlob(int index, byte[] bytes) => Native.sqlite3_bind_blob(_stmt, index, bytes, bytes.Length, new IntPtr(-1));

        public bool MoveNext()
        {
            var rc = Native.sqlite3_step(_stmt);
            Native.ThrowIfError(_db, rc);
            return rc == Native.SQLITE_ROW;
        }

        public IEnumerable<IResultSet> ExecuteQuery()
        {
            while (MoveNext())
                yield return new ResultSet(_stmt);
        }

        public void Dispose() => Native.sqlite3_finalize(_stmt);
    }

    internal sealed class ResultSet : IResultSet
    {
        private readonly IntPtr _stmt;
        public ResultSet(IntPtr stmt) => _stmt = stmt;

        public string GetString(int index) => Native.ColumnText(_stmt, index) ?? "";
        public int GetInt(int index) => (int)Native.sqlite3_column_double(_stmt, index);
        public double GetDouble(int index) => Native.sqlite3_column_double(_stmt, index);
        public bool IsDBNull(int index) => Native.sqlite3_column_type(_stmt, index) == 5; // SQLITE_NULL
        public Guid GetGuid(int index)
        {
            var s = GetString(index);
            return Guid.TryParse(s, out var g) ? g : Guid.Empty;
        }
    }

    internal static class Native
    {
        private const string Lib = "libsqlite3.so.0";
        public const int SQLITE_OK = 0;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_DONE = 101;

        [DllImport(Lib)] public static extern int sqlite3_open(string filename, out IntPtr db);
        [DllImport(Lib)] public static extern int sqlite3_close(IntPtr db);
        [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
        [DllImport(Lib)] public static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
        [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
        [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
        [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Lib)] public static extern double sqlite3_column_double(IntPtr stmt, int col);
        [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);

        public static string? ColumnText(IntPtr stmt, int col)
        {
            var ptr = sqlite3_column_text(stmt, col);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
        }

        public static void ThrowIfError(IntPtr db, int rc)
        {
            if (rc != SQLITE_OK && rc != SQLITE_ROW && rc != SQLITE_DONE)
                throw new InvalidOperationException($"sqlite error {rc}: {Marshal.PtrToStringUTF8(sqlite3_errmsg(db))}");
        }
    }
}
