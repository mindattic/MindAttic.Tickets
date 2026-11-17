using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>
/// M.TIX-T344: on SQL Server, <c>data/Tickets.db</c> is brought up to date by the rows that changed since the mark in its
/// header, not by reading whole tables again. The first tests need no server (the keys, the mark, what a table's changes do
/// to the file); the last run against a scratch database on this machine's SQL Server and are skipped with none reachable.
/// </summary>
[NonParallelizable]
public sealed class CopyByChangesTests
{
    private string root = null!;
    private string? database;

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("tickets-changes-").FullName;

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        if (database is null) return;
        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(TicketsServer.DefaultConnection(database: "master"));
        master.Open();
        using var cmd = master.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
        cmd.ExecuteNonQuery();
    }

    private string NewFile(string name = "Tickets.db")
    {
        var file = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (TicketsDb.Open(file)) { }
        return file;
    }

    private static SqliteConnection OpenFile(string file)
    {
        var c = new SqliteConnection($"Data Source={file};Pooling=False;Foreign Keys=False");
        c.Open();
        return c;
    }

    private static List<object?[]> Query(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) rows.Add([.. Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i))]);
        return rows;
    }

    private static void Run(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void Every_table_has_its_key_and_the_file_keys_it_the_same()
    {
        Assume.That(Environment.GetEnvironmentVariable(TicketsServer.TestEnvVar), Is.Null.Or.Empty, "the file's own schema");
        var file = NewFile();
        using var c = OpenFile(file);
        Assert.That(TicketsServer.Keys.Keys, Is.EquivalentTo(TicketsDb.TableOrder));
        foreach (var table in TicketsDb.TableOrder)
        {
            var key = Query(c, $"SELECT name FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk").Select(r => (string)r[0]!).ToArray();
            Assert.That(TicketsServer.Keys[table], Is.EqualTo(key), table);
            var create = TicketsServer.Tables.Single(t => t.Contains($"CREATE TABLE {table} ", StringComparison.Ordinal));
            foreach (var k in key) Assert.That(create, Does.Contain(k), $"{table}.{k} on SQL Server");
        }
        Assert.That(TicketsDb.ChangesSql.Split("CHANGETABLE(CHANGES ").Length - 1, Is.EqualTo(TicketsDb.TableOrder.Length), "one question a table, in one batch");
    }

    [Test]
    public void The_mark_is_kept_in_the_files_header_and_a_whole_table_copy_clears_it()
    {
        Assume.That(Environment.GetEnvironmentVariable(TicketsServer.TestEnvVar), Is.Null.Or.Empty, "the file's own header");
        var file = NewFile();
        using (var c = OpenFile(file))
        {
            Assert.That(TicketsDb.ReadMark(c, null), Is.EqualTo(default(TicketsDb.CopyMark)), "a new file has none");
            using var tx = c.BeginTransaction();
            TicketsDb.WriteMark(c, tx, new TicketsDb.CopyMark(4321, -77));
            tx.Commit();
        }
        using (var c = OpenFile(file))
        {
            Assert.That(TicketsDb.ReadMark(c, null), Is.EqualTo(new TicketsDb.CopyMark(4321, -77)));
            Assert.That(Query(c, "SELECT name FROM sqlite_master WHERE name LIKE '%mark%' OR name LIKE '%sync%'"), Is.Empty, "no table of its own");
            using var tx = c.BeginTransaction();
            TicketsDb.WriteMark(c, tx, new TicketsDb.CopyMark((long)int.MaxValue + 1, 5));
            tx.Commit();
            Assert.That(TicketsDb.ReadMark(c, null), Is.EqualTo(default(TicketsDb.CopyMark)), "a version the header can't hold is no mark");
            using var again = c.BeginTransaction();
            TicketsDb.WriteMark(c, again, new TicketsDb.CopyMark(9, 3));
            again.Commit();
        }

        // Rows read before the file was locked vouch for no version.
        var other = NewFile("other/Tickets.db");
        using (var source = OpenFile(other)) TicketsDb.CopyTables(source, null, file, ["meta"]);
        using (var c = OpenFile(file)) Assert.That(TicketsDb.ReadMark(c, null), Is.EqualTo(default(TicketsDb.CopyMark)));
    }

    [Test]
    public void A_mark_answers_only_from_its_own_database_and_within_what_the_server_still_keeps()
    {
        var changes = new TicketsDb.ServerChanges(Version: 50, Origin: 7, MinValid: 20, Tables: []);
        var mine = TicketsDb.Stamp(7, cookie: 12);
        Assert.That(mine, Is.Not.Zero);
        Assert.That(TicketsDb.Answers(new TicketsDb.CopyMark(20, mine), changes, 12), Is.True);
        Assert.That(TicketsDb.Answers(new TicketsDb.CopyMark(50, mine), changes, 12), Is.True, "nothing since is an answer too");
        Assert.That(TicketsDb.Answers(new TicketsDb.CopyMark(19, mine), changes, 12), Is.False, "older than the changes go back");
        Assert.That(TicketsDb.Answers(new TicketsDb.CopyMark(51, mine), changes, 12), Is.False, "ahead of the server: it was restored");
        Assert.That(TicketsDb.Answers(new TicketsDb.CopyMark(30, TicketsDb.Stamp(8, 12)), changes, 12), Is.False, "another database's number");
        Assert.That(TicketsDb.Answers(new TicketsDb.CopyMark(30, mine), changes, 30), Is.False, "the file's schema moved: something that writes no mark rewrote its tables");
        Assert.That(TicketsDb.Answers(default, changes, 12), Is.False, "no mark");
        Assert.That(TicketsDb.Origin("localhost/MindAttic.Tickets", "2026-01-01T00:00:00"), Is.Not.Zero.And.EqualTo(TicketsDb.Origin("LOCALHOST/mindattic.tickets", "2026-01-01T00:00:00")));
        Assert.That(TicketsDb.Origin("localhost/MindAttic.Tickets", "2026-01-01T00:00:00"), Is.Not.EqualTo(TicketsDb.Origin("x.database.windows.net/MindAttic.Tickets", "2026-01-01T00:00:00")));
        Assert.That(TicketsDb.Origin("localhost/MindAttic.Tickets", "2026-01-01T00:00:00"), Is.Not.EqualTo(TicketsDb.Origin("localhost/MindAttic.Tickets", "2026-02-01T00:00:00")));
    }

    [Test]
    public void A_tables_changes_update_delete_and_add_rows_and_an_updated_row_stays_where_it_was()
    {
        Assume.That(Environment.GetEnvironmentVariable(TicketsServer.TestEnvVar), Is.Null.Or.Empty, "the file alone");
        var file = NewFile();
        using var c = OpenFile(file);
        Run(c, "INSERT INTO repos (name, code) VALUES ('Alpha', 'AL');" +
               "DROP TRIGGER tickets_no_delete; DROP TRIGGER tickets_append_only; DROP TRIGGER tickets_history;" +
               "INSERT INTO tickets (id, repo, number, date, type, status, summary, ref, ordinal) VALUES " +
               "('AL-T1', 'Alpha', 1, '2026-01-01', 'fix', 'open', 'one', '—', 0), ('AL-T2', 'Alpha', 2, '2026-01-01', 'fix', 'open', 'two', '—', 1), " +
               "('AL-T3', 'Alpha', 3, '2026-01-01', 'fix', 'open', 'three', '—', 2);" +
               "INSERT INTO ticket_events (seq, ticket_id, at, field, old, new) VALUES (1, 'AL-T1', 't', 'created', NULL, 'open'), (2, 'AL-T2', 't', 'created', NULL, 'open');" +
               "INSERT INTO ticket_queue (ticket_id, approval, priority, updated) VALUES ('AL-T1', 'approved', 1, 't');");
        var rowids = Query(c, "SELECT id, rowid FROM tickets").ToDictionary(r => (string)r[0]!, r => (long)r[1]!);

        static (string, object?)[] Ticket(string id, long number, string status, string summary, long ordinal) =>
            [("id", id), ("repo", "Alpha"), ("number", number), ("date", "2026-01-01"), ("type", "fix"), ("status", status), ("summary", summary), ("ref", "abc"), ("ordinal", ordinal), ("row_md", null)];
        using (var tx = c.BeginTransaction())
        {
            TicketsDb.Apply(c, tx, new TicketsDb.TableChanges("tickets", 3,
            [
                (["AL-T4"], Ticket("AL-T4", 4, "open", "four", 3)),     // new, listed first: it mustn't take the rowid AL-T3 goes back to
                (["AL-T3"], Ticket("AL-T3", 3, "closed", "three", 2)),  // updated
                (["AL-T2"], null),                                      // deleted (an ID reassigned away)
                (["AL-T9"], null),                                      // deleted, and never in the file
            ]));
            TicketsDb.Apply(c, tx, new TicketsDb.TableChanges("ticket_events", 3,
            [
                ([2L], [("seq", 2L), ("ticket_id", "AL-T4"), ("at", "t"), ("field", "created"), ("old", null), ("new", "open")]),
                ([3L], [("seq", 3L), ("ticket_id", "AL-T3"), ("at", "t2"), ("field", "status"), ("old", "open"), ("new", "closed")]),
            ]));
            // Case folds as the server's case-insensitive key does.
            TicketsDb.Apply(c, tx, new TicketsDb.TableChanges("ticket_queue", 1,
                [(["al-t1"], [("ticket_id", "al-t1"), ("approval", "rejected"), ("priority", 5L), ("updated", "t2"), ("estimate", null)])]));
            tx.Commit();
        }

        Assert.That(Query(c, "SELECT id, status, ref FROM tickets ORDER BY id").Select(r => $"{r[0]} {r[1]} {r[2]}"),
            Is.EqualTo(new[] { "AL-T1 open —", "AL-T3 closed abc", "AL-T4 open abc" }));
        var after = Query(c, "SELECT id, rowid FROM tickets").ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
        Assert.That(after["AL-T1"], Is.EqualTo(rowids["AL-T1"]), "untouched");
        Assert.That(after["AL-T3"], Is.EqualTo(rowids["AL-T3"]), "an updated row keeps its place");
        Assert.That(after["AL-T4"], Is.Not.EqualTo(rowids["AL-T3"]));
        Assert.That(Query(c, "SELECT seq, ticket_id, field FROM ticket_events ORDER BY seq").Select(r => $"{r[0]} {r[1]} {r[2]}"),
            Is.EqualTo(new[] { "1 AL-T1 created", "2 AL-T4 created", "3 AL-T3 status" }));
        Assert.That(Query(c, "SELECT ticket_id, approval, priority FROM ticket_queue").Select(r => $"{r[0]} {r[1]} {r[2]}"), Is.EqualTo(new[] { "al-t1 rejected 5" }));
    }

    // ── Against SQL Server ──────────────────────────────────────────────────

    private string ScratchServer()
    {
        database = "MindAttic.Tickets.Test-" + Guid.NewGuid().ToString("N")[..12];
        var connection = TicketsServer.DefaultConnection(database: database);
        try { TicketsServer.CreateDatabase(connection); }
        catch (SqlException ex)
        {
            database = null;
            Assert.Ignore($"No SQL Server reachable on localhost: {ex.Message}");
        }
        return connection;
    }

    private static void RunOnServer(string connection, string sql)
    {
        using var c = new SqlConnection(connection);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every table's rows, each row its columns joined, sorted: the file's and the server's read the same way.</summary>
    private static Dictionary<string, List<string>> Rows(System.Data.Common.DbConnection c)
    {
        var all = new Dictionary<string, List<string>>();
        foreach (var table in TicketsDb.TableOrder)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table}";
            using var r = cmd.ExecuteReader();
            var rows = new List<string>();
            while (r.Read()) rows.Add(string.Join("\u001f", Enumerable.Range(0, r.FieldCount).Select(i => r.GetName(i) + "=" + (r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)))));
            rows.Sort(StringComparer.Ordinal);
            all[table] = rows;
        }
        return all;
    }

    private static void AssertFileIsTheServer(string file, string connection, string when)
    {
        using var server = new SqlConnection(connection);
        server.Open();
        using var c = OpenFile(file);
        var there = Rows(server);
        var here = Rows(c);
        foreach (var table in TicketsDb.TableOrder) Assert.That(here[table], Is.EqualTo(there[table]), $"{table} {when}");
    }

    private static TicketsDb.CopyMark Mark(string file)
    {
        using var c = OpenFile(file);
        return TicketsDb.ReadMark(c, null);
    }

    [Test]
    public void On_SQL_Server_the_file_follows_every_write_by_the_rows_that_changed()
    {
        Assume.That(Environment.GetEnvironmentVariable(TicketsServer.TestEnvVar), Is.Null.Or.Empty, "a scratch server of its own");
        var repo = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "docs", "USER_STORIES.md"), StoryFile.NewFileText("Alpha", "AL", "2026-10-23"));
        var connection = ScratchServer();
        var server = new TicketsServer(connection);
        var file = Path.Combine(root, "copy", "Tickets.db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        using (var db = TicketsDb.OpenOn(file, server, backup: false))
        {
            Assert.That(db.ChangeTrackingProblem(), Is.Null, "schema 9 turned change tracking on for every table");
            db.Import([new Repo("Alpha", repo)]);
            var first = Mark(file);
            Assert.That(first.Version, Is.GreaterThan(0), "the first copy read every table and marked the file");
            AssertFileIsTheServer(file, connection, "after the import");

            var a = db.Add("Alpha", "fix", "open", "first", "—", description: "## Why\n\nbecause");
            var b = db.Add("Alpha", "feature", "open", "second, blocked by " + a.Id, "—");
            Assert.That(Mark(file).Version, Is.GreaterThan(first.Version));
            Assert.That(Mark(file).Origin, Is.EqualTo(first.Origin));
            AssertFileIsTheServer(file, connection, "after two tickets");

            db.Close(a.Id, reference: "abc123");            // an update, and the trigger's history rows
            db.AddNote(b.Id, "someone", "a note");          // an append
            db.Approve(b.Id);
            db.SetDescription(a.Id, null);                  // a delete
            db.AddLink(b.Id, a.Id, "related");
            db.RemoveLink(b.Id, a.Id, "related");           // a delete on a system-versioned table
            db.ReassignTicketId(b.Id, "AL-T9", "renumbered for the test"); // a key change: the old row goes, the new one comes
            AssertFileIsTheServer(file, connection, "after updates, deletes and a key change");
            Assert.That(db.LastCopyError, Is.Null);
            Assert.That(TicketsDb.ChangeTrackingError, Is.Null);

            // The pages are built from the file now, and read as the server's rows do.
            var ws = new Workspace(root);
            db.Render(ws);
            Assert.That(File.ReadAllBytes(Path.Combine(repo, "docs", "USER_STORIES.md")), Is.EqualTo(db.View("Alpha")));
        }

        // A writer that died between its commit and its copy: the next write's copy brings its rows too.
        RunOnServer(connection, "INSERT INTO saved_filters (name, query, updated) VALUES (N'lost', N'status:open', N't'); " +
                                "UPDATE ticket_queue SET priority = 42 WHERE ticket_id = N'AL-T9';");
        // A file git put back: older than the server, and it says so itself.
        var old = File.ReadAllBytes(file);
        using (var db = TicketsDb.OpenOn(file, server, backup: false))
        {
            db.Add("Alpha", "fix", "open", "third", "—");
            AssertFileIsTheServer(file, connection, "after a write that followed a lost copy");
        }
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(file, old);
        using (var db = TicketsDb.OpenOn(file, server, backup: false))
        {
            db.AddNote("AL-T9", "someone", "after the file went back");
            AssertFileIsTheServer(file, connection, "after an older file caught up");
        }

        // A file that's drifted where its mark can't say: the changed table's rows don't count the same, so it's read whole.
        using (var c = OpenFile(file)) Run(c, "DROP TRIGGER ticket_notes_no_delete; DELETE FROM ticket_notes WHERE seq = (SELECT MIN(seq) FROM ticket_notes);");
        using (var db = TicketsDb.OpenOn(file, server, backup: false))
        {
            db.AddNote("AL-T9", "someone", "after a note went missing");
            AssertFileIsTheServer(file, connection, "after a table that didn't count the same");
        }

        // A copy by a build from before M.TIX-T344: tables rewritten (here from an older read), triggers dropped and
        // recreated, the mark left as it was. The moved schema cookie says so, and every table is read again.
        using (var c = OpenFile(file))
        {
            var before = TicketsDb.Cookie(c, null);
            Run(c, "DROP TRIGGER tickets_no_delete; DROP TRIGGER tickets_append_only; DROP TRIGGER tickets_history; " +
                   "UPDATE tickets SET status = 'open', ref = 'stale' WHERE id = 'AL-T1';");
            Assert.That(TicketsDb.Cookie(c, null), Is.Not.EqualTo(before));
        }
        using (var db = TicketsDb.OpenOn(file, server, backup: false))
        {
            db.AddNote("AL-T9", "someone", "after an old build's copy");
            AssertFileIsTheServer(file, connection, "after a copy that wrote no mark");
        }

        // No mark (a file from before M.TIX-T344, or one a whole-table copy wrote): every table is read once, then marked.
        using (var c = OpenFile(file)) Run(c, "PRAGMA user_version = 0; PRAGMA application_id = 0; DELETE FROM saved_filters;");
        using (var db = TicketsDb.OpenOn(file, server, backup: false))
        {
            db.Add("Alpha", "fix", "open", "fourth", "—");
            AssertFileIsTheServer(file, connection, "after a file with no mark");
            Assert.That(Mark(file).Version, Is.GreaterThan(0));

            // tickets db sync: every table whole, still marked.
            using (var c = OpenFile(file)) Run(c, "DELETE FROM links;");
            db.CopyAll();
            AssertFileIsTheServer(file, connection, "after tickets db sync");
            Assert.That(Mark(file).Version, Is.GreaterThan(0));
        }

        // Two writers at once, each copying after its own commit: the file ends up the server whatever order they landed in.
        Parallel.For(0, 4, i =>
        {
            using var db = TicketsDb.OpenOn(file, server, backup: false);
            for (var n = 0; n < 5; n++) db.Add("Alpha", "fix", "open", $"writer {i} ticket {n}", "—");
        });
        using (var db = TicketsDb.OpenOn(file, server, backup: false)) db.AddNote("AL-T9", "someone", "the last word");
        AssertFileIsTheServer(file, connection, "after four writers at once");
    }
}
