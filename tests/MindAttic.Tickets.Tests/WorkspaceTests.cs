using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

[TestFixture]
public sealed class WorkspaceTests
{
    private string root = "";

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("wf-ws-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private Repo MakeRepo(string name, string? stories = null)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        if (stories is not null)
        {
            Directory.CreateDirectory(Path.Combine(path, "docs"));
            File.WriteAllText(Path.Combine(path, "docs", "USER_STORIES.md"), stories);
        }
        return new Repo(name, path);
    }

    [Test]
    public void Repos_lists_only_git_folders_sorted_and_Containing_finds_the_enclosing_repo()
    {
        MakeRepo("Zeta");
        MakeRepo("alpha");
        Directory.CreateDirectory(Path.Combine(root, "NotARepo"));
        var worktree = Path.Combine(root, "Worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");

        var ws = new Workspace(root);

        Assert.That(ws.Repos().Select(r => r.Name), Is.EqualTo(new[] { "alpha", "Worktree", "Zeta" }));
        Assert.That(ws.Containing(Path.Combine(root, "Zeta", "src", "deep"))?.Name, Is.EqualTo("Zeta"));
        Assert.That(ws.Containing(Path.Combine(root, "NotARepo")), Is.Null);
        Assert.That(ws.Find("ZETA")?.Name, Is.EqualTo("Zeta"));
    }

    [Test]
    public void Check_reports_duplicate_codes_missing_files_and_bad_tables()
    {
        MakeRepo("GridGame2026", StoryFile.NewFileText("GridGame2026", "GG", "2026-10-12"));
        MakeRepo("GridGameGaiden", StoryFile.NewFileText("GridGameGaiden", "GG", "2026-10-12"));
        MakeRepo("NoStories");
        MakeRepo("Broken", StoryFile.NewFileText("Broken", "BR", "2026-10-12") + "| XX-T1 | 2026-10-12 | fix | open | s | r |\n");

        var problems = WorkspaceTickets.Check(new Workspace(root).Repos());

        Assert.That(problems, Has.Some.Matches<TicketProblem>(p => p.Message.Contains("project code 'GG' is used by 2 repos")));
        Assert.That(problems, Has.Some.Matches<TicketProblem>(p => p.Repo == "NoStories" && p.Message == "no docs/USER_STORIES.md"));
        Assert.That(problems, Has.Some.Matches<TicketProblem>(p => p.Repo == "Broken" && p.Message.Contains("XX-T1")));
    }

    [Test]
    public void All_lists_tickets_across_repos()
    {
        var a = MakeRepo("A", StoryFile.NewFileText("A", "AA", "2026-10-12"));
        var file = StoryFile.Load(a.StoriesPath);
        file.Add("fix", TicketRules.Open, "one", "", "2026-10-12");
        file.Save();
        MakeRepo("B");

        var all = WorkspaceTickets.All(new Workspace(root).Repos());

        Assert.That(all.Select(x => (x.Repo.Name, x.Ticket.Id)), Is.EqualTo(new[] { ("A", "AA-T1") }));
    }

    [TestCase("GridGame2026", "GG")]
    [TestCase("MindAttic.Ideas", "MAI")]
    [TestCase("MindAttic.Tickets", "MAT")]
    [TestCase("Tutor", "TUT")]
    [TestCase("USPSAddressValidator", "UAV")]
    [TestCase("Audible-To-GoodReads", "ATGR")]
    [TestCase("Ciao-ChatGpt-Bonjour-Claude", "CIA")]
    public void DeriveCode_follows_the_codex_rule(string repo, string expected)
    {
        Assert.That(WorkspaceTickets.DeriveCode(repo, new HashSet<string>()), Is.EqualTo(expected));
    }

    [Test]
    public void DeriveCode_avoids_codes_already_taken()
    {
        Assert.That(WorkspaceTickets.DeriveCode("GridGameGaiden", new HashSet<string> { "GGG" }), Is.EqualTo("GRI"));
        Assert.That(WorkspaceTickets.DeriveCode("Tutor", new HashSet<string> { "TUT", "TUTO" }), Is.EqualTo("TUT2"));
    }

    [Test]
    public void Either_name_of_this_repo_finds_its_folder_while_it_is_renamed()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
        try
        {
            var ws = new Workspace(root);
            Assert.That(ws.Find("MindAttic.Tickets")?.Name, Is.EqualTo("MindAttic.Workflow"), "before the folder is renamed");
            Assert.That(Data.TicketsDb.HomeFolder(root), Is.EqualTo("MindAttic.Workflow"));
            Directory.Move(Path.Combine(root, "MindAttic.Workflow"), Path.Combine(root, "MindAttic.Tickets"));
            Assert.That(ws.Find("MindAttic.Workflow")?.Name, Is.EqualTo("MindAttic.Tickets"), "after it");
            Assert.That(Data.TicketsDb.HomeFolder(root), Is.EqualTo("MindAttic.Tickets"));
            Assert.That(ws.Find("Nope"), Is.Null);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
