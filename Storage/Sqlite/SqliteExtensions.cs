using System;
using System.Collections.Generic;
using SQLitePCL.pretty;

namespace MetadataHealthCheck.v2.Storage.Sqlite
{
    /// <summary>
    /// TryBind/ExecuteQuery are NOT part of the real SQLitePCL.pretty.core NuGet
    /// package - they're extension methods the reference plugin Emby.AutoOrganize
    /// defines itself, in its own Data/SqliteExtensions.cs, layered on top of the
    /// package's actual (lower-level) IStatement/IBindParameter API. Confirmed
    /// directly against that file, not assumed. Ported here, trimmed to only the
    /// overloads this project's repositories actually call (string and double
    /// named-parameter binds, plus the ExecuteQuery enumerator) - see the full
    /// reference file for the complete original overload set (int, bool, Guid,
    /// DateTimeOffset, byte spans, etc.) if a future evidence collector needs one.
    /// </summary>
    public static class SqliteExtensions
    {
        // Mirrors Emby.AutoOrganize's Data/SqliteExtensions.cs RunQueries helper.
        public static void RunQueries(this IDatabaseConnection db, string[] queries)
        {
            db.BeginTransaction(TransactionMode.Deferred);
            try
            {
                db.ExecuteAll(string.Join(";", queries));
                db.CommitTransaction();
            }
            catch
            {
                db.RollbackTransaction();
                throw;
            }
        }

        private static void CheckName(IStatement statement, string name)
        {
#if DEBUG
            throw new Exception("Invalid param name: " + name + ". SQL: " + statement.SQL);
#endif
        }

        public static void TryBind(this IStatement statement, string name, string value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                if (value == null)
                {
                    bindParam.BindNull();
                }
                else
                {
                    bindParam.Bind(value);
                }
            }
            else
            {
                CheckName(statement, name);
            }
        }

        public static void TryBind(this IStatement statement, string name, double value)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.Bind(value);
            }
            else
            {
                CheckName(statement, name);
            }
        }

        public static void TryBindNull(this IStatement statement, string name)
        {
            IBindParameter bindParam;
            if (statement.BindParameters.TryGetValue(name, out bindParam))
            {
                bindParam.BindNull();
            }
            else
            {
                CheckName(statement, name);
            }
        }

        public static IEnumerable<IResultSet> ExecuteQuery(this IStatement This)
        {
            while (This.MoveNext())
            {
                yield return This.Current;
            }
        }
    }
}