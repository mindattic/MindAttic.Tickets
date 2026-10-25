using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace MindAttic.Tickets.Data;

/// <summary>
/// The ticket database on SQL Server (M.TIX-T131, see <see cref="TicketsServer"/>): the dialect the queries are rewritten into
/// there, the write lock writers queue on, and <c>data/Tickets.db</c> kept a complete copy, each table a write touched
/// rewritten from the server once it commits (the hooks read the file through stdlib sqlite3, and git keeps it).
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>The write transaction open on the connection, which every command joins.</summary>
    private DbTransaction? ambient;

    /// <summary>The tables written since the last copy into the file.</summary>
    private readonly HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A write transaction: on SQL Server it takes the database's one write lock first (as SQLite's BEGIN IMMEDIATE
    /// does), and its commit copies what it changed into the file.</summary>
    private sealed class Tx(TicketsDb owner, DbTransaction inner) : IDisposable
    {
        private bool done;

        private DbTransaction Inner => inner;

        public static implicit operator DbTransaction(Tx t) => t.Inner;

        public void Commit()
        {
            inner.Commit();
            done = true;
            owner.ambient = null;
            owner.ReleaseWriteLock();
            owner.Copy();
        }

        public void Dispose()
        {
            if (!done)
            {
                try { inner.Rollback(); }
                catch (InvalidOperationException) { } // already rolled back (a THROW in a trigger)
                owner.touched.Clear();
            }
            inner.Dispose();
            if (ReferenceEquals(owner.ambient, inner)) owner.ambient = null;
            if (!done) owner.ReleaseWriteLock();
        }
    }

    private const string WriteLock = "MindAttic.Tickets.write";

    private bool holdsWriteLock;

    private Tx Begin()
    {
        if (server is not null)
        {
            // The lock is the session's, taken before the transaction begins (M.TIX-T137): a system-versioned table stamps every
            // row a transaction writes with the time the transaction began, so one begun before the last writer committed would
            // close that writer's row before it opened, which SQL Server refuses.
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'{WriteLock}', @LockMode = N'Exclusive', " +
                              "@LockOwner = N'Session', @LockTimeout = 15000; SELECT @r;";
            if (Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) < 0)
                throw new IOException("The ticket database on SQL Server stayed busy past its timeout.");
            holdsWriteLock = true;
            try { return Started(db.BeginTransaction()); }
            catch
            {
                ReleaseWriteLock();
                throw;
            }
        }
        var tx = db.BeginTransaction();
        if (TicketsServer.For(Path) is not null)
        {
            // Opened on the file, then waited on its lock while `tickets db server migrate` moved the database: a write here
            // would land in what's only a copy now.
            tx.Dispose();
            throw new InvalidOperationException("The ticket database just moved to SQL Server; run the command again.");
        }
        return Started(tx);
    }

    private Tx Started(DbTransaction tx)
    {
        ambient = tx;
        return new Tx(this, tx);
    }

    private void ReleaseWriteLock()
    {
        if (!holdsWriteLock) return;
        holdsWriteLock = false;
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"EXEC sp_releaseapplock @Resource = N'{WriteLock}', @LockOwner = N'Session';";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { } // the connection went, and its lock with it
    }

    [GeneratedRegex(@"^\s*(?:BEGIN TRY\s+)?(?:INSERT(?: OR \w+)? INTO|UPDATE(?: OR \w+)?|DELETE FROM)\s+(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex WriteTarget();

    /// <summary>Notes the table <paramref name="sql"/> wrote; written outside a transaction, it's copied into the file at once.</summary>
    private void Wrote(string sql, DbTransaction? tx)
    {
        if (server is null || WriteTarget().Match(sql) is not { Success: true } m) return;
        var table = m.Groups[1].Value;
        touched.Add(table);
        // The history trigger writes ticket_events (and row_md) on a status or ref change.
        if (table.Equals("tickets", StringComparison.OrdinalIgnoreCase)) touched.Add("ticket_events");
        if (tx is null) Copy();
    }

    /// <summary>Rewrites each table written since the last copy, from SQL Server, into the file. A copy that can't be made now
    /// (the file held past its timeout) is made by the next <c>tickets db sync</c>, which copies every table.</summary>
    private void Copy()
    {
        if (server is null || touched.Count == 0) return;
        var tables = TableOrder.Where(touched.Contains).ToList();
        touched.Clear();
        try
        {
            CopyTables(db, null, Path, tables);
            LastCopyError = null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { LastCopyError = ex.Message; }
    }

    /// <summary>Runs <paramref name="sql"/> against the store as written (rewritten for SQL Server): a test planting rows no
    /// method would write (a link to nothing, an ID with a stray CR), on whichever store it's running against.</summary>
    internal void Plant(string sql, params (string Name, object? Value)[] args) => Exec(sql, null, args);

    /// <summary>Why the last copy into the file couldn't be made (null when it was), for <c>tickets db server</c> to say.</summary>
    public string? LastCopyError { get; private set; }

    /// <summary>Every table, parents before children (the order rows go in and the order the dump lists them).</summary>
    internal static readonly string[] TableOrder =
        ["meta", "repos", "epics", "stories", "backlog_items", "backlog_refs", "tickets", "ticket_events", "ticket_queue", "ticket_work", "ticket_notes", "repo_renames", "id_history", "links", "blocks"];

    /// <summary>Copies <paramref name="tables"/> from <paramref name="source"/> into the SQLite file at <paramref name="file"/>,
    /// replacing them, in one transaction with its guard triggers lifted and put back before it commits.</summary>
    internal static void CopyTables(DbConnection source, DbTransaction? sourceTx, string file, IReadOnlyList<string> tables)
    {
        var rows = tables.Select(t => (Table: t, Rows: ReadAll(source, sourceTx, t))).ToList();
        using var target = new SqliteConnection($"Data Source={file};Pooling=False;Foreign Keys=False");
        target.Open();
        Raw(target, null, "PRAGMA busy_timeout = 15000; PRAGMA journal_mode = DELETE;");
        using var tx = target.BeginTransaction();
        SqliteSchema(target, tx);
        Raw(target, tx, string.Concat(TriggerNames.Select(n => $"DROP TRIGGER IF EXISTS {n};")));
        foreach (var (table, list) in rows.AsEnumerable().Reverse()) Raw(target, tx, $"DELETE FROM {table};");
        foreach (var (table, list) in rows)
            foreach (var row in list) Insert(target, tx, table, row, quote: false);
        SqliteSchema(target, tx);
        tx.Commit();
    }

    private static readonly string[] TriggerNames =
        ["tickets_no_delete", "tickets_append_only", "tickets_history", "repo_renames_no_delete", "repo_renames_no_update", "id_history_no_delete", "id_history_no_update",
         "ticket_notes_no_delete", "ticket_notes_no_update"];

    private static List<(string Column, object? Value)[]> ReadAll(DbConnection connection, DbTransaction? tx, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT * FROM {table}";
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, object?)[]>();
        while (r.Read()) rows.Add([.. Enumerable.Range(0, r.FieldCount).Select(i => (r.GetName(i), r.IsDBNull(i) ? null : r.GetValue(i)))]);
        return rows;
    }

    private static void Insert(DbConnection connection, DbTransaction tx, string table, (string Column, object? Value)[] row, bool quote) =>
        Raw(connection, tx,
            $"INSERT INTO {table} ({string.Join(", ", row.Select(c => quote && c.Column == "key" ? "[key]" : c.Column))}) VALUES ({string.Join(", ", row.Select((_, i) => "@p" + i))})",
            [.. row.Select((c, i) => ("@p" + i, c.Value))]);

    // ── The T-SQL a query becomes on SQL Server ─────────────────────────────

    [GeneratedRegex(@"\$(\w+)")] private static partial Regex Parameter();
    [GeneratedRegex(@"(?<![\w\[""'.])key(?![\w\]""'])", RegexOptions.IgnoreCase)] private static partial Regex KeyColumn();
    [GeneratedRegex(@"\.key(?![\w\]])", RegexOptions.IgnoreCase)] private static partial Regex QualifiedKey();
    [GeneratedRegex(@"^(\s*INSERT INTO \w+ \([^)]*\))\s*(VALUES.*?)\s+RETURNING (\w+)\s*$", RegexOptions.Singleline)] private static partial Regex Returning();
    [GeneratedRegex(@"^(\s*SELECT)(.*?)\s+LIMIT 1\s*$", RegexOptions.Singleline)] private static partial Regex Limit1();
    [GeneratedRegex(@"^\s*INSERT OR IGNORE INTO", RegexOptions.IgnoreCase)] private static partial Regex InsertOrIgnore();

    /// <summary>
    /// <paramref name="sql"/> as SQL Server takes it: <c>@</c> parameters, <c>COLLATE NOCASE</c> as a case-insensitive
    /// collation, SQLite's clock as T-SQL's, <c>RETURNING</c> as <c>OUTPUT</c>, <c>LIMIT 1</c> as <c>TOP 1</c>, <c>INSERT OR
    /// IGNORE</c> as an insert whose duplicate-key error is passed over, COUNT as a <c>bigint</c>, and the reserved
    /// <c>key</c> column bracketed. What has no such rewrite is written for both dialects where it's used.
    /// </summary>
    internal static string ToTransactSql(string sql)
    {
        var t = Parameter().Replace(sql, "@$1")
            .Replace("COLLATE NOCASE", "COLLATE " + TicketsServer.NoCase, StringComparison.OrdinalIgnoreCase)
            .Replace("strftime('%Y-%m-%dT%H:%M:%SZ', 'now')", "FORMAT(SYSUTCDATETIME(), 'yyyy-MM-ddTHH:mm:ssZ')", StringComparison.Ordinal)
            .Replace("AS TEXT)", "AS nvarchar(450))", StringComparison.OrdinalIgnoreCase)
            .Replace("COUNT(*)", "COUNT_BIG(*)", StringComparison.OrdinalIgnoreCase);
        t = QualifiedKey().Replace(t, ".[key]");
        t = KeyColumn().Replace(t, "[key]");
        if (Returning().Match(t) is { Success: true } r) t = $"{r.Groups[1].Value} OUTPUT INSERTED.{r.Groups[3].Value} {r.Groups[2].Value}";
        if (Limit1().Match(t) is { Success: true } l) t = $"{l.Groups[1].Value} TOP 1{l.Groups[2].Value}";
        if (InsertOrIgnore().IsMatch(t))
            t = $"BEGIN TRY {InsertOrIgnore().Replace(t, "INSERT INTO").TrimEnd().TrimEnd(';')}; END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN (2601, 2627) THROW; END CATCH";
        return t;
    }

    // ── What each dialect writes its own way ────────────────────────────────

    /// <summary>Sets a <c>meta</c> row.</summary>
    private void SetMeta(string key, string value, DbTransaction? tx = null)
    {
        if (server is null) Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)", tx, ("$k", key), ("$v", value));
        else Exec("UPDATE meta SET value = $v WHERE key = $k; IF @@ROWCOUNT = 0 INSERT INTO meta (key, value) VALUES ($k, $v)", tx, ("$k", key), ("$v", value));
    }

    /// <summary>Holds off the foreign-key checks for the rest of <paramref name="tx"/> (a repo rename, an ID reassignment), as
    /// SQLite's <c>defer_foreign_keys</c> does; <see cref="CheckForeignKeys"/> puts them back and checks every row before commit.</summary>
    private void DeferForeignKeys(DbTransaction tx)
    {
        if (server is null) Exec("PRAGMA defer_foreign_keys = ON", tx);
        else foreach (var t in TicketsServer.Referencing) Exec($"ALTER TABLE {t} NOCHECK CONSTRAINT ALL", tx);
    }

    private void CheckForeignKeys(DbTransaction tx)
    {
        if (server is null) return; // SQLite checks the deferred keys at commit
        foreach (var t in TicketsServer.Referencing) Exec($"ALTER TABLE {t} WITH CHECK CHECK CONSTRAINT ALL", tx);
    }

    /// <summary>Lifts the guard triggers named for the rest of <paramref name="tx"/>; <see cref="EnsureSchema"/> puts them back.</summary>
    private void LiftGuards(DbTransaction tx, params string[] names) =>
        Exec(string.Concat(names.Select(n => $"DROP TRIGGER IF EXISTS {n};")), tx);

    private void EnsureServerSchema(DbTransaction? tx)
    {
        tx ??= ambient;
        if (tx is null)
        {
            // At open: the whole schema, the versioning step (M.TIX-T137) included, which backs the database up first.
            ServerSchema((SqlConnection)db, server!.Describe(), backupBeforeVersioning);
            return;
        }
        // Inside a write that lifted guard triggers: put them back before it commits.
        ServerTables(db, tx);
        ServerTriggers(db, tx);
    }

    /// <summary>
    /// The SQL Server schema on <paramref name="connection"/>, step by step as its <c>schema_version</c> rises: the tables
    /// (versions 1 to 5), the tickets made system-versioned (version 6, <see cref="Versioning"/>), then the guard triggers.
    /// </summary>
    internal static void ServerSchema(SqlConnection connection, string describe, bool backup = true)
    {
        ServerTables(connection, null);
        Versioning(connection, describe, backup);
        ServerTriggers(connection, null);
        var version = IsVersioned(connection, null) ? SchemaVersion : SchemaVersion - 1;
        Raw(connection, null, $"IF NOT EXISTS (SELECT 1 FROM meta WHERE [key] = 'schema_version') INSERT INTO meta ([key], value) VALUES ('schema_version', '{version}') " +
                              $"ELSE UPDATE meta SET value = '{version}' WHERE [key] = 'schema_version' AND CAST(value AS int) < {version}");
    }

    private static void ServerTables(DbConnection connection, DbTransaction? tx)
    {
        foreach (var create in TicketsServer.Tables)
        {
            var table = Regex.Match(create, @"CREATE TABLE (\w+)").Groups[1].Value;
            Raw(connection, tx, $"IF OBJECT_ID(N'{table}', N'U') IS NULL BEGIN {create} END");
        }
    }

    private static void ServerTriggers(DbConnection connection, DbTransaction? tx)
    {
        foreach (var (name, body) in TicketsServer.Triggers)
            Raw(connection, tx, $"IF OBJECT_ID(N'{name}', N'TR') IS NULL EXEC (N'{body.Replace("'", "''", StringComparison.Ordinal)}')");
    }

    /// <summary>A copy of the whole database beside the file (<c>&lt;file&gt;.&lt;UTC stamp&gt;.bak</c>, a SQLite file either way)
    /// before a bulk rewrite; returns its path.</summary>
    private string Backup()
    {
        var backup = $"{Path}.{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.bak";
        if (server is null)
        {
            using var dest = new SqliteConnection($"Data Source={backup};Pooling=False");
            dest.Open();
            ((SqliteConnection)db).BackupDatabase(dest); // SQLite's online backup API: safe to run against the live, open connection
        }
        else CopyTables(db, ambient, backup, TableOrder);
        return backup;
    }

    // ── Moving the data between the file and SQL Server ─────────────────────

    public sealed record Copied(string Table, long Rows);

    /// <summary>
    /// Copies every table of the SQLite file at <paramref name="file"/> into the SQL Server database <paramref name="connection"/>
    /// names (created when it isn't there), checks each table's row count, then uses it on this machine. A server already
    /// holding tickets is left alone unless <paramref name="replace"/>, so a second run can't copy an older file over it.
    /// </summary>
    public static IReadOnlyList<Copied> MigrateToServer(string file, string connection, bool replace = false, bool configure = true)
    {
        TicketsServer.CreateDatabase(connection);
        var server = new TicketsServer(connection);
        using (var schema = new TicketsDbOn(server, versioning: false)) { } // creates the tables and triggers; versioned once the rows are in
        // The file's write lock is held from the copy until SQL Server is switched on: a ticket added meanwhile waits for it, then
        // opens on SQL Server (the setting is read at every open), rather than landing in the file after its rows were read.
        using var source = new SqliteConnection($"Data Source={System.IO.Path.GetFullPath(file)};Pooling=False");
        source.Open();
        Raw(source, null, "PRAGMA busy_timeout = 15000;");
        using var held = source.BeginTransaction(deferred: false);
        using var target = server.Open();
        using var tx = target.BeginTransaction();
        Raw(target, tx, "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'MindAttic.Tickets.write', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;");
        var there0 = Convert.ToInt64(ScalarOn(target, tx, "SELECT COUNT_BIG(*) FROM tickets"), System.Globalization.CultureInfo.InvariantCulture);
        if (there0 > 0 && !replace)
            throw new InvalidOperationException($"{server.Describe()} already holds {there0} tickets; pass --replace to copy {file} over them.");
        foreach (var (name, _) in TicketsServer.Triggers) Raw(target, tx, $"DISABLE TRIGGER {name} ON {TriggerTable(name)}");
        foreach (var t in TicketsServer.Referencing) Raw(target, tx, $"ALTER TABLE {t} NOCHECK CONSTRAINT ALL");
        foreach (var table in TableOrder.Reverse()) Raw(target, tx, $"DELETE FROM {table}");
        var copied = new List<Copied>();
        foreach (var table in TableOrder)
        {
            var rows = ReadAll(source, held, table);
            if (TicketsServer.Identities.Contains(table)) Raw(target, tx, $"SET IDENTITY_INSERT {table} ON");
            foreach (var row in rows) Insert(target, tx, table, row, quote: true);
            if (TicketsServer.Identities.Contains(table)) Raw(target, tx, $"SET IDENTITY_INSERT {table} OFF");
            var there = Convert.ToInt64(ScalarOn(target, tx, $"SELECT COUNT_BIG(*) FROM {table}"), System.Globalization.CultureInfo.InvariantCulture);
            if (there != rows.Count)
                throw new InvalidOperationException($"{table}: {rows.Count} rows read from {file} but {there} written to {server.Describe()}; nothing was changed.");
            copied.Add(new Copied(table, rows.Count));
        }
        foreach (var t in TicketsServer.Referencing) Raw(target, tx, $"ALTER TABLE {t} WITH CHECK CHECK CONSTRAINT ALL");
        foreach (var (name, _) in TicketsServer.Triggers) Raw(target, tx, $"ENABLE TRIGGER {name} ON {TriggerTable(name)}");
        tx.Commit();
        // Versioned now the rows are there, so each ticket's first versions are worked back from its events (M.TIX-T137).
        ServerSchema(target, server.Describe(), backup: false);
        if (configure) TicketsServer.Configure(connection);
        held.Rollback(); // nothing was written to the file; its lock goes now
        return copied;
    }

    /// <summary>Copies the SQL Server database back into the file at <paramref name="file"/> (every table) and stops using
    /// SQL Server on this machine: the file is the store again.</summary>
    public static IReadOnlyList<Copied> MigrateToSqlite(string file, string connection, bool configure = true)
    {
        using var source = new TicketsServer(connection).Open();
        CopyTables(source, null, file, TableOrder);
        if (configure) TicketsServer.Configure(null);
        return [.. TableOrder.Select(t => new Copied(t, Convert.ToInt64(ScalarOn(source, null, $"SELECT COUNT_BIG(*) FROM {t}"), System.Globalization.CultureInfo.InvariantCulture)))];
    }

    /// <summary>Rewrites every table of the file from SQL Server (<c>tickets db sync</c>): a copy a write couldn't make then
    /// is made now. Nothing while the file is the store.</summary>
    public void CopyAll()
    {
        if (server is null) return;
        touched.Clear();
        CopyTables(db, null, Path, TableOrder);
    }

    /// <summary>Each table's rows in the store and in the file, for <c>tickets db server</c>.</summary>
    public IReadOnlyList<(string Table, long Store, long File)> Counts()
    {
        using var file = new SqliteConnection($"Data Source={Path};Pooling=False;Mode=ReadOnly");
        file.Open();
        return [.. TableOrder.Select(t => (t,
            Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {t}"), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt64(ScalarOn(file, null, $"SELECT COUNT(*) FROM {t}"), System.Globalization.CultureInfo.InvariantCulture)))];
    }

    private static string TriggerTable(string trigger) =>
        trigger.StartsWith("tickets_", StringComparison.Ordinal) ? "tickets" : trigger.StartsWith("repo_renames", StringComparison.Ordinal) ? "repo_renames"
            : trigger.StartsWith("ticket_notes", StringComparison.Ordinal) ? "ticket_notes" : "id_history";

    private static object? ScalarOn(DbConnection connection, DbTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>A database opened straight on a server (for creating its schema), whatever this machine has configured.</summary>
    private sealed class TicketsDbOn : IDisposable
    {
        private readonly SqlConnection connection;

        public TicketsDbOn(TicketsServer server, bool versioning = true)
        {
            connection = server.Open();
            if (versioning) ServerSchema(connection, server.Describe());
            else
            {
                ServerTables(connection, null);
                ServerTriggers(connection, null);
            }
        }

        public void Dispose() => connection.Dispose();
    }
}
