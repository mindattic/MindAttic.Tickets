using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace MindAttic.Tickets.Data;

/// <summary>
/// <c>data/Tickets.db</c> brought up to date by the rows that changed (M.TIX-T344), not by reading whole tables again.
/// SQL Server's change tracking is on for every table (schema 9, <see cref="ChangeTracking"/>): it numbers every commit and
/// keeps, per table, the key of each row a commit inserted, updated or deleted. The file carries the number it's complete
/// up to (<see cref="CopyMark"/>, in its header, so a file git puts back says how old it is), and a copy asks for the keys
/// changed since then with each row as it is now, in one round trip, and writes those.
/// <para>Whoever holds the file's write lock reads its mark, then asks the server, then writes: copies made by several
/// processes at once land one after another, each from a later read than the one before, whatever order their own writes
/// committed in. What's applied is the server's state, never one process's own changes, so a process that dies between its
/// commit and its copy loses nothing: the next copy, anyone's, starts from the same mark. A mark the server can't answer
/// from (none, another database's, older than the tracked changes go back, ahead of the server) means every table is read
/// whole once; so does <c>tickets db sync</c>. A table whose rows don't count the same afterwards is read whole too.</para>
/// <para>The mark is also tied to the file's schema cookie, which every whole-table copy moves (it drops and recreates the
/// guard triggers): a copy made by a build from before this one, which knows no mark and may land an older read over a
/// newer one, leaves the mark not answering, and the next copy reads every table rather than trust it.</para>
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>Whether the server tracks changes on every table, asked at open with the schema question.</summary>
    private bool tracked;

    /// <summary>The file holds the server's rows as of a copy this instance just made, with nothing written here since.</summary>
    private bool fileCurrent;

    /// <summary>Why change tracking couldn't be turned on, or a copy by changes couldn't be made (the copy was then made
    /// whole, as before M.TIX-T344); null when neither went wrong in this process.</summary>
    public static string? ChangeTrackingError { get; private set; }

    /// <summary>What the file is complete up to: the server's change-tracking version, and which database that number
    /// counts in (<see cref="Origin"/>, stamped with the file's schema cookie, <see cref="Stamp"/>). Kept in the SQLite
    /// header (<c>user_version</c>, <c>application_id</c>), so no table differs from the server's. Version 0 is no mark.</summary>
    internal readonly record struct CopyMark(long Version, int Origin);

    /// <summary>The rows of one table changed since a mark: each key, with the row as it is now (null: deleted), and how
    /// many rows the table holds on the server.</summary>
    internal sealed record TableChanges(string Table, long Count, List<(object?[] Key, (string Column, object? Value)[]? Row)> Rows);

    /// <summary>The server's answer to "what changed since": its version now (read before the rows), the database it
    /// counts in, the oldest version it can still answer from, and each table's changes.</summary>
    internal sealed record ServerChanges(long Version, int Origin, long MinValid, List<TableChanges> Tables);

    // ── The mark ────────────────────────────────────────────────────────────

    internal static CopyMark ReadMark(DbConnection file, DbTransaction? tx) =>
        new(Convert.ToInt64(ScalarOn(file, tx, "PRAGMA user_version"), CultureInfo.InvariantCulture),
            Convert.ToInt32(ScalarOn(file, tx, "PRAGMA application_id"), CultureInfo.InvariantCulture));

    /// <summary>Writes the mark with the rows it describes, in their transaction. A version the header can't hold (past
    /// 2^31) is written as none, so the next copy is whole rather than wrong.</summary>
    internal static void WriteMark(DbConnection file, DbTransaction tx, CopyMark mark)
    {
        if (mark.Version is <= 0 or > int.MaxValue) mark = default;
        if (ReadMark(file, tx) == mark) return;
        Raw(file, tx, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {mark.Version}; PRAGMA application_id = {mark.Origin};"));
    }

    /// <summary>Which database a version counts in: the server and database named, and when the database was created (a
    /// restore or a new database starts its versions again). Never 0.</summary>
    internal static int Origin(string describe, string created)
    {
        var hash = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(describe.ToUpperInvariant() + "|" + created)), 0);
        return hash == 0 ? 1 : hash;
    }

    /// <summary>The file's schema cookie: SQLite raises it at every schema change, a dropped or created trigger included.</summary>
    internal static int Cookie(DbConnection file, DbTransaction? tx) =>
        Convert.ToInt32(ScalarOn(file, tx, "PRAGMA schema_version"), CultureInfo.InvariantCulture);

    /// <summary><paramref name="origin"/> as the mark holds it in a file whose schema cookie is <paramref name="cookie"/>:
    /// a file whose schema has changed since (a whole-table copy by anything that doesn't write the mark) no longer matches.
    /// Never 0.</summary>
    internal static int Stamp(int origin, int cookie)
    {
        var stamp = unchecked(origin * 16777619 ^ cookie);
        return stamp == 0 ? 1 : stamp;
    }

    /// <summary>Whether <paramref name="changes"/> are everything that happened since <paramref name="mark"/>, read from a
    /// file whose schema cookie is <paramref name="cookie"/>.</summary>
    internal static bool Answers(CopyMark mark, ServerChanges changes, int cookie) =>
        mark.Version > 0 && mark.Origin == Stamp(changes.Origin, cookie) && mark.Version <= changes.Version && mark.Version >= changes.MinValid;

    // ── One transaction on the file ─────────────────────────────────────────

    /// <summary>Runs <paramref name="write"/> in one write transaction on the SQLite file at <paramref name="file"/>. It calls
    /// the action it's handed before it changes a row: that makes the schema and lifts the guard triggers, which are put
    /// back before the commit. The mark it returns (version and unstamped origin; default clears it, null leaves it) is
    /// written last, stamped with the schema cookie the file ends up with. A write that changes nothing leaves the file as
    /// it was.</summary>
    private static T InFile<T>(string file, Func<SqliteConnection, SqliteTransaction, Action, (T Result, CopyMark? Mark)> write)
    {
        using var target = new SqliteConnection($"Data Source={file};Pooling=False;Foreign Keys=False");
        target.Open();
        Raw(target, null, "PRAGMA busy_timeout = 15000; PRAGMA journal_mode = DELETE;");
        using var tx = target.BeginTransaction();
        var lifted = false;
        var (result, mark) = write(target, tx, () =>
        {
            if (lifted) return;
            lifted = true;
            SqliteSchema(target, tx);
            Raw(target, tx, string.Concat(TriggerNames.Select(n => $"DROP TRIGGER IF EXISTS {n};")));
        });
        if (lifted) SqliteSchema(target, tx);
        if (mark is { } m) WriteMark(target, tx, m.Version > 0 ? m with { Origin = Stamp(m.Origin, Cookie(target, tx)) } : default);
        tx.Commit();
        return result;
    }

    /// <summary>Replaces each of <paramref name="tables"/> in the file with the rows given for it.</summary>
    private static void Replace(DbConnection target, DbTransaction tx, IReadOnlyList<(string Table, List<(string Column, object? Value)[]> Rows)> tables)
    {
        foreach (var (table, _) in tables.Reverse()) Raw(target, tx, $"DELETE FROM {table};");
        foreach (var (table, list) in tables)
            foreach (var row in list) Insert(target, tx, table, row, quote: false);
    }

    /// <summary>
    /// Writes one table's changes into the file: every changed key's row goes, then each row that's still there comes back
    /// as it is now, at the rowid it had (so a query with no ORDER BY reads an updated row where it read it before; a new
    /// row goes last). Taking them all out first means no row ever collides with another's old value on the way.
    /// </summary>
    internal static void Apply(DbConnection target, DbTransaction tx, TableChanges changes)
    {
        var table = changes.Table;
        var where = string.Join(" AND ", TicketsServer.Keys[table].Select((k, i) => $"{k} = @k{i}"));
        var keyIsRowid = TicketsServer.Identities.Contains(table);
        var back = new List<((string Column, object? Value)[] Row, long? Rowid)>();
        foreach (var (key, row) in changes.Rows)
        {
            long? rowid = null;
            using (var find = target.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = $"SELECT rowid FROM {table} WHERE {where}";
                for (var i = 0; i < key.Length; i++) find.Parameters.Add(new SqliteParameter("@k" + i, key[i] ?? DBNull.Value));
                if (find.ExecuteScalar() is { } found and not DBNull) rowid = Convert.ToInt64(found, CultureInfo.InvariantCulture);
            }
            if (rowid is { } at) Raw(target, tx, $"DELETE FROM {table} WHERE rowid = @r", ("@r", at));
            if (row is not null) back.Add((row, keyIsRowid ? null : rowid));
        }
        // Rows going back where they were first: a new row takes the next free rowid, which mustn't be one of those.
        foreach (var (row, rowid) in back.OrderBy(b => b.Rowid is null))
            Insert(target, tx, table, rowid is { } at ? [("rowid", (object?)at), .. row] : row, quote: false);
    }

    // ── What the server says changed ────────────────────────────────────────

    private static string? changesSql;

    /// <summary>One batch: the version now and the database's creation time; then, per table in <see cref="TableOrder"/>, the
    /// keys changed since <c>@last</c> with whether the row is still there, the table's row count and the row; last, the
    /// newest of the tables' oldest answerable versions and how many tables are tracked.</summary>
    internal static string ChangesSql => changesSql ??=
        "SELECT CHANGE_TRACKING_CURRENT_VERSION(), (SELECT CONVERT(nvarchar(40), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());\n" +
        string.Concat(TableOrder.Select(table =>
        {
            var key = TicketsServer.Keys[table];
            return $"SELECT CASE WHEN t.[{key[0]}] IS NULL THEN 0 ELSE 1 END, (SELECT COUNT_BIG(*) FROM {table}), {string.Join(", ", key.Select(k => $"c.[{k}]"))}, t.* " +
                   $"FROM CHANGETABLE(CHANGES {table}, @last) AS c LEFT JOIN {table} AS t ON {string.Join(" AND ", key.Select(k => $"t.[{k}] = c.[{k}]"))};\n";
        })) +
        "SELECT MAX(m.v), COUNT_BIG(m.v) FROM (VALUES " + string.Join(", ", TableOrder.Select(t => $"(CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'{t}')))")) + ") AS m(v);";

    /// <summary>What changed on the server since version <paramref name="since"/>; null when it isn't tracking changes.</summary>
    private ServerChanges? ChangesSince(long since)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = ambient;
        cmd.CommandText = ChangesSql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@last";
        p.DbType = System.Data.DbType.Int64;
        p.Value = since;
        cmd.Parameters.Add(p);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;
        var version = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
        var origin = Origin(server!.Describe(), r.IsDBNull(1) ? "" : r.GetString(1));
        var tables = new List<TableChanges>();
        foreach (var table in TableOrder)
        {
            if (!r.NextResult()) throw new InvalidOperationException($"The server's changes stop before {table}.");
            var keys = TicketsServer.Keys[table].Length;
            var count = 0L;
            var rows = new List<(object?[], (string, object?)[]?)>();
            while (r.Read())
            {
                count = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                object?[] key = [.. Enumerable.Range(2, keys).Select(i => r.IsDBNull(i) ? null : r.GetValue(i))];
                var there = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture) == 1;
                rows.Add((key, there ? [.. Enumerable.Range(2 + keys, r.FieldCount - 2 - keys).Select(i => (r.GetName(i), r.IsDBNull(i) ? null : r.GetValue(i)))] : null));
            }
            tables.Add(new TableChanges(table, count, rows));
        }
        if (!r.NextResult() || !r.Read()) throw new InvalidOperationException("The server's changes stop before their oldest version.");
        // A table that isn't tracked has no oldest version: nothing can be answered from.
        var all = !r.IsDBNull(0) && Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture) == TableOrder.Length;
        return new ServerChanges(version, origin, all ? Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture) : long.MaxValue, tables);
    }

    /// <summary>Writes <paramref name="changes"/> into the file, then counts each table they touched: one whose rows don't
    /// number what the server's do is read whole and replaced (under the file's lock, so from a read later than any copy before).</summary>
    private void Apply(SqliteConnection target, SqliteTransaction tx, ServerChanges changes)
    {
        foreach (var table in changes.Tables.Where(t => t.Rows.Count > 0))
        {
            Apply(target, tx, table);
            if (Convert.ToInt64(ScalarOn(target, tx, $"SELECT COUNT(*) FROM {table.Table}"), CultureInfo.InvariantCulture) != table.Count)
                Replace(target, tx, [(table.Table, ReadAll(db, ambient, table.Table))]);
        }
    }

    private enum CopyOutcome { Done, Whole, Untracked }

    /// <summary>
    /// Brings the file up to the server by the rows changed since its mark, or by every table when the mark can't be
    /// answered from. False when the server isn't tracking changes (the caller copies as before M.TIX-T344).
    /// </summary>
    private bool CopyChanges()
    {
        var outcome = InFile(Path, (target, tx, lift) =>
        {
            var mark = ReadMark(target, tx);
            if (mark.Version <= 0) return (CopyOutcome.Whole, (CopyMark?)null);
            if (ChangesSince(mark.Version) is not { } changes) return (CopyOutcome.Untracked, null);
            if (!Answers(mark, changes, Cookie(target, tx))) return (CopyOutcome.Whole, null);
            if (changes.Tables.Any(t => t.Rows.Count > 0))
            {
                lift();
                Apply(target, tx, changes);
            }
            return (CopyOutcome.Done, new CopyMark(changes.Version, changes.Origin));
        });
        return outcome == CopyOutcome.Done || (outcome == CopyOutcome.Whole && CopyWhole());
    }

    /// <summary>
    /// Rewrites every table of the file from the server and marks it. The version is read first, then the tables (before
    /// the file is locked: they're the whole database), so each table is at least that new; under the file's lock the
    /// changes since that version are then applied on top, which brings back anything a copy made meanwhile had that
    /// these reads were too early for. False when the server isn't tracking changes.
    /// </summary>
    private bool CopyWhole()
    {
        long version;
        int origin;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = ambient;
            cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION(), (SELECT CONVERT(nvarchar(40), create_date, 126) FROM sys.databases WHERE database_id = DB_ID())";
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return false;
            version = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            origin = Origin(server!.Describe(), r.IsDBNull(1) ? "" : r.GetString(1));
        }
        var rows = TableOrder.Select(t => (Table: t, Rows: ReadAll(db, ambient, t))).ToList();
        InFile(Path, (target, tx, lift) =>
        {
            lift();
            Replace(target, tx, rows);
            var mark = new CopyMark(version, origin);
            if (ChangesSince(version) is { } since && since.Origin == origin && version >= since.MinValid)
            {
                Apply(target, tx, since);
                mark = new CopyMark(since.Version, origin);
            }
            return (true, (CopyMark?)mark);
        });
        return true;
    }

    /// <summary>
    /// <see cref="CopyChanges"/>, with anything that goes wrong in it but a busy file turned into "not done" (and changes
    /// not asked for again by this instance): the caller then copies whole, as it always did, so a fault here costs a
    /// slower copy, never a stale file. A busy file is thrown, as a whole copy would throw it.
    /// </summary>
    private bool TryCopyChanges()
    {
        fileCurrent = false;
        if (!tracked) return false;
        try
        {
            if (CopyChanges()) return fileCurrent = true;
            tracked = false;
            return false;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { throw; } // SQLITE_BUSY, SQLITE_LOCKED
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidCastException or FormatException or OverflowException
                                       or ArgumentException or KeyNotFoundException or IndexOutOfRangeException or IOException or UnauthorizedAccessException)
        {
            tracked = false;
            ChangeTrackingError = $"The copy by changed rows failed, so it was made whole: {ex.Message}";
            return false;
        }
    }

    /// <summary>Whether the file can be read in the server's place right now: it's just been brought up to the server by
    /// this instance, or is now. Never while the server isn't tracking changes, or inside a write.</summary>
    private bool FileIsCurrent()
    {
        if (server is null || !tracked || ambient is not null) return false;
        if (fileCurrent)
        {
            fileCurrent = false; // once: a later read asks the server what changed again
            return true;
        }
        try
        {
            var current = TryCopyChanges();
            fileCurrent = false;
            return current;
        }
        catch (SqliteException) { return false; }
    }

    /// <summary>Rows read from the file itself, for what's read whole after a write (every repo's page).</summary>
    private static List<T> FileRows<T>(SqliteConnection file, string sql, Func<DbDataReader, T> read)
    {
        using var cmd = file.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    /// <summary>Why the file is being copied whole after each change rather than by the rows that changed, for
    /// <c>tickets db server</c> to say; null when it's copied by changes (or the file is the store).</summary>
    public string? ChangeTrackingProblem()
    {
        if (server is null) return null;
        if (!tracked) return ChangeTrackingError ?? "change tracking isn't on for every table";
        try
        {
            using var file = new SqliteConnection($"Data Source={Path};Pooling=False;Mode=ReadOnly");
            file.Open();
            return ChangesSince(Math.Max(ReadMark(file, null).Version, 0)) is null ? "change tracking isn't on" : null;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidCastException or IOException) { return ex.Message; }
    }

    // ── Schema 9 on SQL Server: change tracking ─────────────────────────────

    /// <summary>The T-SQL condition that's true when change tracking is on for the database and every table.</summary>
    private static string TrackedCondition() =>
        "CHANGE_TRACKING_CURRENT_VERSION() IS NOT NULL AND (SELECT COUNT_BIG(*) FROM sys.change_tracking_tables WHERE object_id IN (" +
        string.Join(", ", TableOrder.Select(t => $"OBJECT_ID(N'{t}')")) + $")) = {TableOrder.Length}";

    private static bool IsTracked(DbConnection connection, DbTransaction? tx) =>
        Convert.ToInt32(ScalarOn(connection, tx, $"SELECT CASE WHEN {TrackedCondition()} THEN 1 ELSE 0 END"), CultureInfo.InvariantCulture) == 1;

    /// <summary>
    /// Schema 9 (M.TIX-T344): turns change tracking on for the database and each table that doesn't have it (every table
    /// has a primary key; a system-versioned table is tracked on its current rows, its history table left alone). Each
    /// statement runs on its own, outside any transaction (ALTER DATABASE takes none), under the write lock so no write is
    /// midway; one already done is skipped, so a second run, or two processes at once, change nothing. The same statements
    /// on SQL Server and Azure SQL Database. A server that refuses (no ALTER permission) leaves the copy made whole as
    /// before, and <see cref="ChangeTrackingError"/> says why; <c>tickets db sync</c> tries again.
    /// </summary>
    internal static bool ChangeTracking(SqlConnection c)
    {
        try
        {
            if (IsTracked(c, null)) return true;
        }
        catch (SqlException ex)
        {
            ChangeTrackingError = $"Change tracking couldn't be asked about: {ex.Message}";
            return false;
        }
        var locked = false;
        try
        {
            locked = Convert.ToInt32(ScalarOn(c, null, $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'{WriteLock}', @LockMode = N'Exclusive', " +
                                                       "@LockOwner = N'Session', @LockTimeout = 60000; SELECT @r;"), CultureInfo.InvariantCulture) >= 0;
            if (!locked)
            {
                ChangeTrackingError = "Change tracking wasn't turned on: the ticket database stayed busy.";
                return false;
            }
            if (Convert.ToInt64(ScalarOn(c, null, "SELECT COUNT_BIG(*) FROM sys.change_tracking_databases WHERE database_id = DB_ID()"), CultureInfo.InvariantCulture) == 0)
                Raw(c, null, string.Create(CultureInfo.InvariantCulture,
                    $"ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = {TicketsServer.ChangeRetentionDays()} DAYS, AUTO_CLEANUP = ON)"));
            var on = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT OBJECT_NAME(object_id) FROM sys.change_tracking_tables";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(0)) on.Add(r.GetString(0));
            }
            foreach (var table in TableOrder.Where(t => !on.Contains(t)))
                Raw(c, null, $"ALTER TABLE {table} ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF)");
            ChangeTrackingError = null;
        }
        catch (SqlException ex) { ChangeTrackingError = $"Change tracking couldn't be turned on: {ex.Message}"; }
        finally
        {
            if (locked)
                try { Raw(c, null, $"EXEC sp_releaseapplock @Resource = N'{WriteLock}', @LockOwner = N'Session';"); }
                catch (Exception ex) when (ex is DbException or InvalidOperationException) { } // the connection went, and its lock with it
        }
        try { return IsTracked(c, null); }
        catch (SqlException) { return false; }
    }
}
