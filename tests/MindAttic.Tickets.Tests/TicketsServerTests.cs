using Microsoft.Data.SqlClient;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The ticket database moved to SQL Server and back (M.TIX-T131) loses nothing: every table's rows, history and
/// identities come back as they went, so the dump reads the same. Runs against a scratch database on this machine's SQL
/// Server, dropped afterwards; skipped with none reachable. (The whole suite runs on SQL Server too with
/// MINDATTIC_TICKETS_TEST_SQLSERVER set.)</summary>
[NonParallelizable]
public sealed class TicketsServerTests
{
    private string root = null!;
    private string? database;

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("tickets-server-").FullName;

    [TearDown]
    public void TearDown()
    {
        SqliteCleanup();
        if (database is null) return;
        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(TicketsServer.DefaultConnection(database: "master"));
        master.Open();
        using var cmd = master.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
        cmd.ExecuteNonQuery();
    }

    private void SqliteCleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Test]
    public void A_database_moved_to_SQL_Server_and_back_reads_the_same()
    {
        Assume.That(Environment.GetEnvironmentVariable(TicketsServer.TestEnvVar), Is.Null.Or.Empty, "a file to move needs the file run");
        var repo = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        File.WriteAllText(Path.Combine(repo, "docs", "USER_STORIES.md"), StoryFile.NewFileText("Alpha", "AL", "2026-10-23"));
        var file = Path.Combine(root, "Tickets.db");
        string before;
        using (var db = TicketsDb.Open(file))
        {
            db.Import([new Repo("Alpha", repo)]);
            var a = db.Add("Alpha", "fix", "open", "first", "—");
            var b = db.Add("Alpha", "feature", "open", "second, blocked by " + a.Id, "—");
            db.Approve(b.Id);
            db.Close(a.Id, reference: "abc123");
            before = db.Dump();
        }

        database = "MindAttic.Tickets.Test-" + Guid.NewGuid().ToString("N")[..12];
        var connection = TicketsServer.DefaultConnection(database: database);
        try { TicketsServer.CreateDatabase(connection); }
        catch (SqlException ex)
        {
            database = null;
            Assert.Ignore($"No SQL Server reachable on localhost: {ex.Message}");
        }

        var copied = TicketsDb.MigrateToServer(file, connection, configure: false);
        Assert.That(copied.Single(c => c.Table == "tickets").Rows, Is.EqualTo(2));
        Assert.That(copied.Single(c => c.Table == "ticket_events").Rows, Is.GreaterThanOrEqualTo(3), "created twice, closed once");
        Assert.Throws<InvalidOperationException>(() => TicketsDb.MigrateToServer(file, connection, configure: false), "never copied over tickets without --replace");

        var back = Path.Combine(root, "back", "Tickets.db");
        Directory.CreateDirectory(Path.GetDirectoryName(back)!);
        TicketsDb.MigrateToSqlite(back, connection, configure: false);
        using var returned = TicketsDb.Open(back);
        Assert.That(returned.Dump(), Is.EqualTo(before));
        Assert.That(returned.QueueState(returned.Tickets().Last().Ticket.Id).Approval, Is.EqualTo(TicketsDb.Approved));
    }

    /// <summary>A scratch SQL Server database (dropped afterwards), or the test is skipped with none reachable.</summary>
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

    private static void Run(string connection, string sql)
    {
        using var c = new SqlConnection(connection);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(string connection, string table)
    {
        using var c = new SqlConnection(connection);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT_BIG(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// M.TIX-T137: on SQL Server the tickets, their approval and priority, and their links are system-versioned. The first
    /// versions are worked back from the ticket events; every later save is kept with its period, across an ID change; a
    /// ticket still can't be deleted; the file copy keeps only the current rows; and a database from before versioning is
    /// versioned at its next open, its rows counted unchanged.
    /// </summary>
    [Test]
    public void On_SQL_Server_every_save_of_a_ticket_is_kept_as_a_version()
    {
        Assume.That(Environment.GetEnvironmentVariable(TicketsServer.TestEnvVar), Is.Null.Or.Empty, "a file to move needs the file run");
        var repo = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        File.WriteAllText(Path.Combine(repo, "docs", "USER_STORIES.md"), StoryFile.NewFileText("Alpha", "AL", "2026-10-23"));
        var file = Path.Combine(root, "Tickets.db");
        Ticket a, b;
        using (var db = TicketsDb.Open(file))
        {
            db.Import([new Repo("Alpha", repo)]);
            a = db.Add("Alpha", "fix", "open", "first", "—");
            b = db.Add("Alpha", "feature", "open", "second, blocked by " + a.Id, "—");
            db.Approve(b.Id);
            db.Close(a.Id, reference: "abc123");
        }

        var connection = ScratchServer();
        TicketsDb.MigrateToServer(file, connection, configure: false);
        var server = new TicketsServer(connection);
        var copy = Path.Combine(root, "copy", "Tickets.db");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        int before;
        using (var db = TicketsDb.OpenOn(copy, server, backup: false))
        {
            Assert.That(db.IsVersioned(), Is.True);
            var va = db.Versions(a.Id);
            Assert.That(va.Select(v => (v.Ticket.Status, v.Ticket.Ref)), Is.EqualTo(new[] { ("open", "—"), ("closed", "abc123") }), "worked back from its events");
            Assert.That(va[1].Changed, Is.EqualTo(new[] { "status", "ref" }));
            Assert.That(va.All(v => v.Recorded));
            var vb = db.Versions(b.Id);
            Assert.That(vb[0].Approval, Is.EqualTo(TicketsDb.Proposed));
            Assert.That(vb[^1].Approval, Is.EqualTo(TicketsDb.Approved));
            Assert.That(vb[0].Links.Select(l => (l.Kind, l.To)), Is.EqualTo(new[] { ("requires", a.Id) }), "its link dated from when it was filed");
            before = vb.Count;

            db.SetPriority(b.Id, 9);
            db.AddLink(b.Id, a.Id, "related");
            vb = db.Versions(b.Id);
            Assert.That(vb, Has.Count.EqualTo(before + 2));
            Assert.That(vb[^2].Changed, Is.EqualTo(new[] { "priority" }));
            Assert.That(vb[^2].Priority, Is.EqualTo(9));
            Assert.That(vb[^1].Changed, Is.EqualTo(new[] { "links" }));
            Assert.That(vb[^1].Links, Has.Count.EqualTo(2));

            db.Assign(b.Id, "someone@example.com");
            vb = db.Versions(b.Id);
            Assert.That(vb[^1].Changed, Is.EqualTo(new[] { "assignee" }));
            Assert.That(vb[^1].Assignee, Is.EqualTo("someone@example.com"));
            before++;

            db.ReassignTicketId(b.Id, "AL-T9", "renumbered for the test");
            var moved = db.Versions("AL-T9");
            Assert.That(moved, Has.Count.EqualTo(before + 3), "its versions under the old ID follow it");
            Assert.That(moved[0].Ticket.Id, Is.EqualTo(b.Id));
            Assert.That(moved[^1].Changed, Does.Contain("id"));
            Assert.That(moved[^1].Ticket.Id, Is.EqualTo("AL-T9"));

            Assert.Throws<SqlException>(() => db.Plant("DELETE FROM tickets WHERE id = $id", ("$id", a.Id)));
            Assert.That(db.Tickets(), Has.Count.EqualTo(2));
            Assert.That(db.CheckVersions(), Is.Empty);
        }
        using (var file2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            file2.Open();
            using var cmd = file2.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info('tickets')";
            using var r = cmd.ExecuteReader();
            var columns = new List<string>();
            while (r.Read()) columns.Add(r.GetString(0));
            Assert.That(columns, Does.Not.Contain("valid_from"), "the file keeps the current rows as before");
        }

        // A database from before versioning (M.TIX-T131's schema, its INSTEAD OF guard included) is versioned at its next open.
        SqlConnection.ClearAllPools();
        foreach (var (table, history) in new[] { ("tickets", "tickets_versions"), ("ticket_queue", "ticket_queue_versions"), ("ticket_work", "ticket_work_versions"), ("links", "links_versions") })
        {
            Run(connection, $"ALTER TABLE {table} SET (SYSTEM_VERSIONING = OFF)");
            Run(connection, $"ALTER TABLE {table} DROP PERIOD FOR SYSTEM_TIME");
            Run(connection, $"ALTER TABLE {table} DROP CONSTRAINT {table}_valid_from, {table}_valid_to");
            Run(connection, $"ALTER TABLE {table} DROP COLUMN valid_from, valid_to");
            Run(connection, $"DROP TABLE {history}");
        }
        Run(connection, "DROP TRIGGER tickets_no_delete");
        Run(connection, "CREATE TRIGGER tickets_no_delete ON tickets INSTEAD OF DELETE AS BEGIN IF EXISTS (SELECT 1 FROM deleted) THROW 50000, 'tickets are never deleted', 1; END");
        var counts = new[] { "tickets", "ticket_events", "ticket_queue", "ticket_work", "links" }.ToDictionary(t => t, t => Count(connection, t));
        using (var db = TicketsDb.OpenOn(copy, server, backup: false))
        {
            Assert.That(db.IsVersioned(), Is.True);
            Assert.That(counts.Keys.ToDictionary(t => t, t => Count(connection, t)), Is.EqualTo(counts));
            Assert.That(db.Versions(a.Id).Select(v => v.Ticket.Status), Is.EqualTo(new[] { "open", "closed" }));
            Assert.That(db.Versions("AL-T9")[^1].Priority, Is.EqualTo(9));
            Assert.That(db.CheckVersions(), Is.Empty);
            Assert.Throws<SqlException>(() => db.Plant("DELETE FROM tickets WHERE id = $id", ("$id", a.Id)), "the guard came back as an AFTER trigger");
        }
    }
}
