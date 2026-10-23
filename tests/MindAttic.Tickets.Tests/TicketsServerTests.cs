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
}
