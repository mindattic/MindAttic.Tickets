using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MindAttic.Tickets.Data;

/// <summary>
/// The ticket database on SQL Server (M.TIX-T131): the store of record for every repo's stories and tickets, on the full
/// instance beside the Prose database, in a database of its own (<see cref="DefaultDatabase"/>). <c>data/Tickets.db</c> stays
/// a complete copy, rewritten table by table after every change: the hooks read it through stdlib sqlite3, and git keeps it.
/// Turned on by <c>tickets db server migrate</c>, which writes <see cref="ConfigFile"/>; with nothing configured (another
/// machine, a test's scratch file) the SQLite file is the store, as before.
/// </summary>
public sealed class TicketsServer(string connectionString)
{
    public const string DefaultDatabase = "MindAttic.Tickets";

    /// <summary>A connection string to use instead of <see cref="ConfigFile"/>'s; "off" keeps the SQLite file the store.</summary>
    public const string EnvVar = "MINDATTIC_TICKETS_SQLSERVER";

    /// <summary>Set (to a server, e.g. "localhost"), every database a test opens gets a scratch SQL Server database of its own,
    /// so the whole suite runs against SQL Server as well as SQLite.</summary>
    public const string TestEnvVar = "MINDATTIC_TICKETS_TEST_SQLSERVER";

    /// <summary>The machine's setting, under <c>%APPDATA%\MindAttic\MindAttic.Tickets</c>: <c>{"SqlServer": "..."}</c>.</summary>
    public static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MindAttic", "MindAttic.Tickets", "database.json");

    public string ConnectionString { get; } = connectionString;

    public string Describe() => new SqlConnectionStringBuilder(ConnectionString) is var b ? $"{b.DataSource}/{b.InitialCatalog}" : ConnectionString;

    public static string DefaultConnection(string server = "localhost", string database = DefaultDatabase) => new SqlConnectionStringBuilder
    {
        DataSource = server,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        Encrypt = false,
        ConnectTimeout = 5,
        ApplicationName = "MindAttic.Tickets",
    }.ConnectionString;

    private static (DateTime Stamp, string? Connection) cached = (DateTime.MinValue, null);
    private static readonly Lock CacheLock = new();

    /// <summary>The connection configured for the workspace database, or null for SQLite alone; read again whenever the file
    /// changes, so <c>tickets db server migrate</c> reaches every running process at its next open.</summary>
    public static string? Configured()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env)
            return string.Equals(env, "off", StringComparison.OrdinalIgnoreCase) ? null : env;
        var stamp = File.GetLastWriteTimeUtc(ConfigFile);
        lock (CacheLock)
        {
            if (stamp == cached.Stamp) return cached.Connection;
            string? connection = null;
            try
            {
                if (File.Exists(ConfigFile))
                    using (var doc = JsonDocument.Parse(File.ReadAllText(ConfigFile)))
                        if (doc.RootElement.TryGetProperty("SqlServer", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } c)
                            connection = c;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return cached.Connection; }
            cached = (stamp, connection);
            return connection;
        }
    }

    /// <summary>Saves <paramref name="connection"/> as this machine's setting (null removes it: SQLite alone).</summary>
    public static void Configure(string? connection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        if (connection is null) File.Delete(ConfigFile);
        else File.WriteAllText(ConfigFile, JsonSerializer.Serialize(new Dictionary<string, string> { ["SqlServer"] = connection }, new JsonSerializerOptions { WriteIndented = true }));
        lock (CacheLock) cached = (DateTime.MinValue, null);
    }

    /// <summary>
    /// The server <paramref name="path"/> is kept on: the configured one for the workspace's own database (not one an
    /// environment variable or a caller points elsewhere), a scratch one per file while <see cref="TestEnvVar"/> is set, else
    /// none (the file is the store).
    /// </summary>
    public static TicketsServer? For(string path)
    {
        var full = Path.GetFullPath(path);
        if (Environment.GetEnvironmentVariable(TestEnvVar) is { Length: > 0 } test)
        {
            var name = "MindAttic.Tickets.Test-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
            var connection = DefaultConnection(test, name);
            CreateDatabase(connection);
            return new TicketsServer(connection);
        }
        if (Environment.GetEnvironmentVariable(TicketsDb.EnvVar) is { Length: > 0 } || Environment.GetEnvironmentVariable(TicketsDb.LegacyEnvVar) is { Length: > 0 })
            return null;
        if (!string.Equals(full, Path.GetFullPath(TicketsDb.DefaultPath()), StringComparison.OrdinalIgnoreCase)) return null;
        return Configured() is { } c ? new TicketsServer(c) : null;
    }

    public SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Creates the database when it isn't there, with a binary collation so text compares and sorts as SQLite's does;
    /// what SQLite compares <c>COLLATE NOCASE</c> is compared <see cref="NoCase"/>.</summary>
    public static void CreateDatabase(string connection)
    {
        var b = new SqlConnectionStringBuilder(connection);
        var name = b.InitialCatalog;
        b.InitialCatalog = "master";
        using var master = new SqlConnection(b.ConnectionString);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = "IF DB_ID(@name) IS NULL EXEC (N'CREATE DATABASE ' + @quoted + N' COLLATE Latin1_General_100_BIN2');";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@quoted", "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]");
        command.ExecuteNonQuery();
    }

    /// <summary>The collation SQLite's <c>COLLATE NOCASE</c> becomes.</summary>
    public const string NoCase = "Latin1_General_100_CI_AS";

    /// <summary>The tables, in the column order SQLite's have (the dump is <c>SELECT *</c> from either), keys as
    /// <c>nvarchar(450)</c>, SQLite's rowid keys as identities.</summary>
    internal static readonly string[] Tables =
    [
        """
        CREATE TABLE meta ([key] nvarchar(450) NOT NULL PRIMARY KEY, value nvarchar(max) NOT NULL);
        """,
        """
        CREATE TABLE repos (
            name nvarchar(450) NOT NULL PRIMARY KEY, code nvarchar(450) NOT NULL UNIQUE,
            project nvarchar(max) NULL, title nvarchar(max) NULL, updated nvarchar(max) NULL,
            front_matter nvarchar(max) NOT NULL DEFAULT '',
            bom bigint NOT NULL DEFAULT 0, crlf bigint NOT NULL DEFAULT 0, trailing_newline bigint NOT NULL DEFAULT 1);
        """,
        """
        CREATE TABLE blocks (
            repo nvarchar(450) NOT NULL REFERENCES repos(name), ordinal bigint NOT NULL,
            kind nvarchar(16) NOT NULL CHECK (kind IN ('raw', 'epic', 'story', 'backlog', 'tickets')),
            ref nvarchar(450) NULL, text nvarchar(max) NOT NULL,
            PRIMARY KEY (repo, ordinal));
        """,
        """
        CREATE TABLE epics (
            repo nvarchar(450) NOT NULL REFERENCES repos(name), [key] nvarchar(450) NOT NULL, title nvarchar(max) NOT NULL,
            anchor nvarchar(max) NULL, ordinal bigint NOT NULL,
            PRIMARY KEY (repo, [key]));
        """,
        """
        CREATE TABLE stories (
            uid bigint IDENTITY(1,1) PRIMARY KEY, repo nvarchar(450) NOT NULL REFERENCES repos(name), story_id nvarchar(450) NOT NULL,
            parent_uid bigint NULL REFERENCES stories(uid), epic nvarchar(450) NULL, section nvarchar(max) NULL, section_ref nvarchar(max) NULL,
            status nvarchar(16) NULL CHECK (status IS NULL OR status IN ('done', 'partial', 'planned')), glyph nvarchar(16) NULL, suffix nvarchar(max) NOT NULL DEFAULT '',
            text nvarchar(max) NOT NULL, evidence nvarchar(max) NULL, acceptance nvarchar(max) NULL,
            source nvarchar(16) NOT NULL CHECK (source IN ('bullet', 'sub-bullet', 'table', 'backlog')), ordinal bigint NOT NULL);
        CREATE INDEX stories_by_id ON stories (story_id);
        """,
        """
        CREATE TABLE backlog_items (
            uid bigint IDENTITY(1,1) PRIMARY KEY, repo nvarchar(450) NOT NULL REFERENCES repos(name), ordinal bigint NOT NULL, text nvarchar(max) NOT NULL);
        """,
        """
        CREATE TABLE backlog_refs (
            item_uid bigint NOT NULL REFERENCES backlog_items(uid), story_id nvarchar(450) NOT NULL, PRIMARY KEY (item_uid, story_id));
        """,
        """
        CREATE TABLE tickets (
            id nvarchar(450) NOT NULL PRIMARY KEY, repo nvarchar(450) NOT NULL REFERENCES repos(name), number bigint NULL,
            date nvarchar(64) NOT NULL, type nvarchar(64) NOT NULL,
            status nvarchar(16) NOT NULL CHECK (status IN ('open', 'closed', 'wont-do')),
            summary nvarchar(max) NOT NULL, ref nvarchar(max) NOT NULL DEFAULT N'—', ordinal bigint NOT NULL,
            row_md nvarchar(max) NULL);
        CREATE INDEX tickets_by_repo ON tickets (repo, ordinal);
        """,
        """
        CREATE TABLE ticket_events (
            seq bigint IDENTITY(1,1) PRIMARY KEY, ticket_id nvarchar(450) NOT NULL REFERENCES tickets(id), at nvarchar(64) NOT NULL,
            field nvarchar(64) NOT NULL, old nvarchar(max) NULL, new nvarchar(max) NULL);
        """,
        """
        CREATE TABLE links (
            from_id nvarchar(450) NOT NULL, to_id nvarchar(450) NOT NULL,
            kind nvarchar(32) NOT NULL CHECK (kind IN ('requires', 'related', 'follow-up-of', 'supersedes', 'duplicates', 'implements', 'part-of')),
            note nvarchar(max) NULL, PRIMARY KEY (from_id, to_id, kind), CHECK (from_id <> to_id));
        CREATE INDEX links_to ON links (to_id);
        """,
        $"""
        CREATE TABLE ticket_queue (
            ticket_id nvarchar(450) COLLATE {NoCase} NOT NULL PRIMARY KEY,
            approval nvarchar(16) NOT NULL DEFAULT 'proposed' CHECK (approval IN ('proposed', 'approved', 'rejected')),
            priority bigint NOT NULL DEFAULT 0,
            updated nvarchar(64) NOT NULL);
        """,
        $"""
        CREATE TABLE ticket_work (
            ticket_id nvarchar(450) COLLATE {NoCase} NOT NULL PRIMARY KEY,
            assignee nvarchar(450) NULL, reserved bigint NOT NULL DEFAULT 0,
            login nvarchar(450) NULL, session nvarchar(450) NULL, since nvarchar(64) NULL,
            updated nvarchar(64) NOT NULL);
        """,
        """
        CREATE TABLE repo_renames (
            seq bigint IDENTITY(1,1) PRIMARY KEY, old_name nvarchar(450) NOT NULL, new_name nvarchar(450) NOT NULL, at nvarchar(64) NOT NULL, reason nvarchar(max) NOT NULL);
        CREATE INDEX repo_renames_by_old ON repo_renames (old_name);
        CREATE INDEX repo_renames_by_new ON repo_renames (new_name);
        """,
        """
        CREATE TABLE id_history (
            seq bigint IDENTITY(1,1) PRIMARY KEY, kind nvarchar(16) NOT NULL CHECK (kind IN ('ticket', 'story', 'epic', 'code')),
            old_id nvarchar(450) NOT NULL, new_id nvarchar(450) NOT NULL, at nvarchar(64) NOT NULL, reason nvarchar(max) NOT NULL);
        CREATE INDEX id_history_by_old ON id_history (old_id);
        """,
    ];

    /// <summary>The guards, as SQLite's triggers keep them: tickets are never deleted and only their status and ref change
    /// (every such change a history row), and repo renames and ID history are never changed or deleted. T-SQL triggers fire
    /// once a statement, so each checks the rows it was given, and nothing fires for a statement that touched none.</summary>
    internal static readonly (string Name, string Body)[] Triggers =
    [
        // AFTER, not INSTEAD OF: a system-versioned table takes no INSTEAD OF trigger (M.TIX-T137). The THROW rolls the delete back.
        ("tickets_no_delete", """
            CREATE TRIGGER tickets_no_delete ON tickets AFTER DELETE AS
            BEGIN IF EXISTS (SELECT 1 FROM deleted) THROW 50000, 'tickets are never deleted; close one as wont-do instead', 1; END
            """),
        ("tickets_append_only", """
            CREATE TRIGGER tickets_append_only ON tickets AFTER UPDATE AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted) AND (UPDATE(id) OR UPDATE(repo) OR UPDATE(number) OR UPDATE(date) OR UPDATE(type) OR UPDATE(summary) OR UPDATE(ordinal))
                    AND EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.id = d.id
                                WHERE i.id IS NULL OR i.repo <> d.repo OR ISNULL(i.number, -1) <> ISNULL(d.number, -1) OR i.date <> d.date
                                   OR i.type <> d.type OR i.summary <> d.summary OR i.ordinal <> d.ordinal)
                    THROW 50000, 'tickets are append-only: only status and ref may change', 1;
            END
            """),
        ("tickets_history", """
            CREATE TRIGGER tickets_history ON tickets AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT (UPDATE(status) OR UPDATE(ref)) RETURN;
                DECLARE @now nvarchar(64) = FORMAT(SYSUTCDATETIME(), 'yyyy-MM-ddTHH:mm:ssZ');
                INSERT INTO ticket_events (ticket_id, at, field, old, new)
                    SELECT i.id, @now, 'status', d.status, i.status FROM inserted i JOIN deleted d ON d.id = i.id WHERE i.status <> d.status;
                INSERT INTO ticket_events (ticket_id, at, field, old, new)
                    SELECT i.id, @now, 'ref', d.ref, i.ref FROM inserted i JOIN deleted d ON d.id = i.id WHERE i.ref <> d.ref;
                UPDATE t SET row_md = NULL FROM tickets t JOIN inserted i ON i.id = t.id JOIN deleted d ON d.id = i.id
                    WHERE i.status <> d.status OR i.ref <> d.ref;
            END
            """),
        ("repo_renames_no_delete", """
            CREATE TRIGGER repo_renames_no_delete ON repo_renames INSTEAD OF DELETE AS
            BEGIN IF EXISTS (SELECT 1 FROM deleted) THROW 50000, 'repo renames are never deleted', 1; END
            """),
        ("repo_renames_no_update", """
            CREATE TRIGGER repo_renames_no_update ON repo_renames INSTEAD OF UPDATE AS
            BEGIN IF EXISTS (SELECT 1 FROM deleted) THROW 50000, 'repo renames are append-only', 1; END
            """),
        ("id_history_no_delete", """
            CREATE TRIGGER id_history_no_delete ON id_history INSTEAD OF DELETE AS
            BEGIN IF EXISTS (SELECT 1 FROM deleted) THROW 50000, 'ID history is never deleted', 1; END
            """),
        ("id_history_no_update", """
            CREATE TRIGGER id_history_no_update ON id_history INSTEAD OF UPDATE AS
            BEGIN IF EXISTS (SELECT 1 FROM deleted) THROW 50000, 'ID history is append-only', 1; END
            """),
    ];

    /// <summary>The tables a <c>REFERENCES</c> points out of, whose checks a repo rename or an ID reassignment holds off within
    /// its transaction (SQLite's <c>PRAGMA foreign_keys</c> / <c>defer_foreign_keys</c>), and checks again before it commits.</summary>
    internal static readonly string[] Referencing = ["blocks", "epics", "stories", "backlog_items", "backlog_refs", "tickets", "ticket_events"];

    /// <summary>
    /// The tables kept SYSTEM_VERSIONED (M.TIX-T137): every save keeps the row it replaced, with the period it held, in the
    /// history table beside it, never pruned. A ticket's own row, its approval and priority, its work (assignee, reservation,
    /// the session working it) and its links (prerequisites and blocks included), so a ticket reads as it did at any moment. The period columns are HIDDEN, so <c>SELECT *</c> (the copy into
    /// the file, the dump) reads the current rows as before.
    /// </summary>
    internal static readonly (string Table, string History, string[] Key)[] Versioned =
    [
        ("tickets", "tickets_versions", ["id"]),
        ("ticket_queue", "ticket_queue_versions", ["ticket_id"]),
        ("ticket_work", "ticket_work_versions", ["ticket_id"]),
        ("links", "links_versions", ["from_id", "to_id", "kind"]),
    ];

    /// <summary>The tables with identity keys, for copying rows in with their keys.</summary>
    internal static readonly HashSet<string> Identities = new(StringComparer.OrdinalIgnoreCase) { "stories", "backlog_items", "ticket_events", "repo_renames", "id_history" };
}
