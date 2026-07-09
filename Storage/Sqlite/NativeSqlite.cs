using System.Runtime.InteropServices;

namespace MetadataHealthCheck.v2.Storage.Sqlite
{
    /// <summary>
    /// TEMPORARY SUBSTITUTE — §9 specifies SQLite access via SQLitePCL.pretty,
    /// matching the reference plugin Emby.AutoOrganize. That package lives on
    /// nuget.org, which is not reachable from this build sandbox (only a fixed
    /// allow-list of domains, not including nuget.org, is reachable here).
    /// Rather than fake the storage layer with an in-memory stand-in, this is a
    /// small direct P/Invoke wrapper over the system libsqlite3 (already present
    /// via the libsqlite3-0 apt package) — enough to genuinely exercise real
    /// SQLite semantics (WAL mode, real persistence, real SQL) for Phase 1
    /// testing. Swap for SQLitePCL.pretty once building in an environment with
    /// nuget access, per the reference-plugin-matching requirement in §9.
    /// Logged as an open item in the Project Log.
    /// </summary>
    internal static class NativeSqlite
    {
        private const string Lib = "libsqlite3.so.0";

        public const int SQLITE_OK = 0;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_DONE = 101;

        [DllImport(Lib)] public static extern int sqlite3_open(string filename, out IntPtr db);
        [DllImport(Lib)] public static extern int sqlite3_close(IntPtr db);
        [DllImport(Lib)] public static extern int sqlite3_exec(IntPtr db, string sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);
        [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
        [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
        [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
        [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
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
