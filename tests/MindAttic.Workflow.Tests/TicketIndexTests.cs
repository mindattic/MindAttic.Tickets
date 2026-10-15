using Microsoft.Data.Sqlite;
using MindAttic.Workflow.Tickets;
using NUnit.Framework;

namespace MindAttic.Workflow.Tests;

[TestFixture]
public sealed class TicketIndexTests
{
    private string root = "";

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("wf-idx-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private Repo MakeRepo(string name, string stories)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        Directory.CreateDirectory(Path.Combine(path, "docs"));
        File.WriteAllText(Path.Combine(path, "docs", "USER_STORIES.md"), stories);
        return new Repo(name, path);
    }

    private static (string Repo, string Id, string Date, string Type, string Status, string Summary, string Ref)[] ReadRows(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT repo, id, date, type, status, summary, ref FROM tickets ORDER BY repo, id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string, string, string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return [.. rows];
    }

    [Test]
    public void Rebuild_writes_every_repos_tickets_and_a_generated_at_marker()
    {
        var a = MakeRepo("A", StoryFile.NewFileText("A", "AA", "2026-10-12"));
        var fileA = StoryFile.Load(a.StoriesPath);
        fileA.Add("fix", TicketRules.Open, "one|two", "ref with | pipe", "2026-10-12");
        fileA.Save();
        MakeRepo("B", StoryFile.NewFileText("B", "BB", "2026-10-13"));
        var dbPath = Path.Combine(root, "tickets.db");

        var count = TicketIndex.Rebuild(new Workspace(root).Repos(), dbPath);

        Assert.That(count, Is.EqualTo(1));
        var rows = ReadRows(dbPath);
        Assert.That(rows, Has.Length.EqualTo(1));
        Assert.That(rows[0], Is.EqualTo(("A", "AA-T1", "2026-10-12", "fix", "open", "one|two", "ref with | pipe")));

        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var meta = connection.CreateCommand();
        meta.CommandText = "SELECT value FROM meta WHERE key = 'generated_at'";
        Assert.That(meta.ExecuteScalar(), Is.Not.Null);
    }

    [Test]
    public void Rebuild_replaces_a_stale_file_instead_of_merging_into_it()
    {
        var dbPath = Path.Combine(root, "tickets.db");
        MakeRepo("A", StoryFile.NewFileText("A", "AA", "2026-10-12"));
        var fileA = StoryFile.Load(new Workspace(root).Find("A")!.StoriesPath);
        fileA.Add("fix", TicketRules.Open, "stays", "", "2026-10-12");
        fileA.Save();
        TicketIndex.Rebuild(new Workspace(root).Repos(), dbPath);

        Directory.Delete(Path.Combine(root, "A"), recursive: true);
        TicketIndex.Rebuild(new Workspace(root).Repos(), dbPath);

        Assert.That(ReadRows(dbPath), Is.Empty);
    }
}
