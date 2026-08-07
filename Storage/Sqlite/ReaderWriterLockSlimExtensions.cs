namespace MetadataHealthCheck.v2.Storage.Sqlite
{
    /// <summary>
    /// Ported verbatim from the reference plugin Emby.AutoOrganize's own
    /// ReaderWriterLockSlimExtensions.cs, confirmed via decompile - not
    /// written from scratch.
    ///
    /// DELIBERATE, NOT A BUG: Read() below returns a WriteLockToken, exactly
    /// as the reference plugin's own real source does - every "read" call
    /// takes a full exclusive lock, identically to Write(). This looks like a
    /// copy-paste naming mistake, but it's preserved here on purpose rather
    /// than "corrected" to a real EnterReadLock: BaseSqliteRepository's
    /// CreateConnection hands out Clone()d connections that all wrap the
    /// SAME underlying native sqlite3 handle (Clone() does not open an
    /// independent native connection - confirmed via decompile of
    /// SQLiteDatabaseConnection.Clone), under ConnectionFlags.NoMutex, which
    /// deliberately disables SQLite's own internal thread-safety for that
    /// handle. Whether truly concurrent reads against that shared handle
    /// would actually be safe is unverified; the reference plugin's own
    /// production code never risks finding out, and this project reproduces
    /// that same fully-serialized-everything behavior rather than optimizing
    /// past a safety margin nobody has actually tested.
    /// </summary>
    public static class ReaderWriterLockSlimExtensions
    {
        private sealed class WriteLockToken : IDisposable
        {
            private ReaderWriterLockSlim? _sync;

            public WriteLockToken(ReaderWriterLockSlim sync)
            {
                _sync = sync;
                sync.EnterWriteLock();
            }

            public void Dispose()
            {
                if (_sync != null)
                {
                    _sync.ExitWriteLock();
                    _sync = null;
                }
            }
        }

        public static IDisposable Read(this ReaderWriterLockSlim obj) => new WriteLockToken(obj);

        public static IDisposable Write(this ReaderWriterLockSlim obj) => new WriteLockToken(obj);
    }
}