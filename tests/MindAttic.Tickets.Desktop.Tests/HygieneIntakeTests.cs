using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// Ticket hygiene on the Audit tab and GitHub intake from the window (M.TIX-T138): the closed tickets to double-check, as
/// <c>tickets ticket hygiene</c> finds them, and intake that shows its dry run and files exactly that, only once confirmed.
/// </summary>
public sealed class HygieneIntakeTests
{
    private string root = "";
    private MainViewModel model = null!;
    private Answers dialogs = null!;
    private int ghCalls;

    [SetUp]
    public async Task SetUp()
    {
        ghCalls = 0;
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-hygiene-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL",
            "| AL-T1 | 2026-10-01 | fix | closed | alpha fix with no proof | — |\n" +
            "| AL-T2 | 2026-10-02 | fix | closed | alpha save fix | SaveTests.Saves |\n" +
            "| AL-T3 | 2026-10-03 | fix | open | AL-T2 broke again: save fails | — |\n");
        var beta = MakeRepo("Beta", "BE",
            "| BE-T1 | 2026-10-04 | feature | closed | beta feature | — |\n| BE-T2 | 2026-10-05 | chore | closed | beta chore | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
            db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);

        var created = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        (int, string) Gh(string[] a)
        {
            ghCalls++;
            if (a[0] == "issue")
                return a[3] == "o/alpha" ? (0, """[{"number":7,"title":"Crash on save","labels":[{"name":"bug"}],"url":"https://github.com/o/alpha/issues/7"}]""") : (0, "[]");
            return a[3] == "o/alpha" && a[5] == "main"
                ? (0, $$"""[{"databaseId":99,"name":"CI","headBranch":"main","createdAt":"{{created}}","url":"https://github.com/o/alpha/actions/runs/99"}]""")
                : (0, "[]");
        }
        var store = new TicketStore(root) { Gh = Gh, Remote = r => $"https://github.com/o/{r.Name.ToLowerInvariant()}.git" };
        dialogs = new Answers();
        model = new MainViewModel(store, new DesktopSettings(), dialogs, new OrchestratorState(Path.Combine(root, "none", "orchestrator.db")));
        await model.RefreshAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        for (var attempt = 1; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50); }
        }
    }

    private string MakeRepo(string name, string code, string rows)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            $"---\ncodex: 1\nproject: {name}\ncode: {code}\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# {name} — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" + rows);
        return dir;
    }

    private string[] Summaries(string repo)
    {
        using var db = TicketsDb.Open(TicketsDb.DefaultPath(root));
        return [.. db.Tickets(repo).Select(t => t.Ticket.Summary)];
    }

    [Test]
    public void Audit_lists_closed_tickets_with_no_ref_or_that_came_back_as_the_cli_does()
    {
        Assert.That(model.HygieneRows.Select(f => (f.Repo, f.TicketId, f.Why.StartsWith("closed with no Ref", StringComparison.Ordinal) ? "no ref" : f.Why[..5])),
            Is.EqualTo(new[] { ("Alpha", "AL-T1", "no ref"), ("Alpha", "AL-T2", "AL-T3"), ("Beta", "BE-T1", "no ref") }),
            "a chore closed with no ref isn't flagged; a fix a later ticket says broke again is");
    }

    [Test]
    public void The_repo_filter_and_search_narrow_the_hygiene_list()
    {
        model.SelectedView = MainViewModel.AuditView;
        model.RepoFilter = "Beta";
        Assert.That(model.HygieneRows.Select(f => f.TicketId), Is.EqualTo(new[] { "BE-T1" }));
        model.RepoFilter = MainViewModel.AllRepos;
        model.Search = "came back";
        Assert.That(model.HygieneRows.Select(f => f.TicketId), Is.EqualTo(new[] { "AL-T2" }));
        Assert.That(model.StatusMessage, Does.Contain("1 closed tickets to double-check"));
    }

    [Test]
    public async Task A_findings_ticket_link_opens_it_in_Repos()
    {
        model.SelectedView = MainViewModel.AuditView;
        model.GoTo("AL-T1");
        Assert.That(model.IsTicketsView, Is.True);
        Assert.That(model.SelectedTicketRow?.Id, Is.EqualTo("AL-T1"));
        // The selection loads the detail off the UI thread, and that load runs git for the ticket's commits (M.TIX-T85): a
        // fixed short wait loses the race on a busy machine (M.TIX-T151, M.TIX-T155), so wait for it as the other tests do.
        for (var i = 0; i < 250 && model.Detail?.Id != "AL-T1"; i++) await Task.Delay(20);
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T1"));
    }

    [Test]
    public async Task Intake_shows_its_dry_run_and_cancelling_files_nothing()
    {
        dialogs.Next = null;
        await model.IntakeAsync();

        Assert.That(dialogs.Asked, Is.Not.Null, "the preview is shown before anything is filed");
        Assert.That(dialogs.Asked!.Message, Does.Contain("Alpha  ·  fix  ·  GH#7: Crash on save").And.Contain("CI#99: CI failed on main"));
        Assert.That(dialogs.Asked.Ok, Is.EqualTo("File 2"));
        Assert.That(Summaries("Alpha"), Has.None.Contains("GH#7").And.None.Contains("CI#99"));
        Assert.That(model.StatusMessage, Is.EqualTo("Nothing filed from GitHub."));
    }

    [Test]
    public async Task Confirming_files_exactly_the_preview_once_without_asking_GitHub_again()
    {
        dialogs.Next = new PromptAnswer(null, "");
        await model.IntakeAsync();
        var asked = ghCalls;

        Assert.That(Summaries("Alpha"), Has.One.EqualTo("GH#7: Crash on save").And.One.EqualTo("CI#99: CI failed on main"));
        Assert.That(model.StatusMessage, Is.EqualTo("Filed 2 ticket(s) from GitHub."));
        Assert.That(model.TicketRows.Select(r => r.Summary), Has.Member("GH#7: Crash on save"), "the window refreshed");

        dialogs.Asked = null;
        await model.IntakeAsync();
        Assert.That(ghCalls, Is.GreaterThan(asked));
        Assert.That(dialogs.Asked, Is.Null, "nothing new: nothing to confirm");
        Assert.That(model.StatusMessage, Does.StartWith("Nothing new on GitHub"));
        Assert.That(Summaries("Alpha").Count(s => s.StartsWith("GH#7", StringComparison.Ordinal)), Is.EqualTo(1));
    }

    [Test]
    public void Filing_the_preview_skips_what_was_filed_since()
    {
        var store = new TicketStore(root) { Gh = (_ => (0, "[]")) };
        var preview = new[] { new IntakePreview("Alpha", new Tickets.Repos.IntakeItem("GH#7", "fix", "GH#7: Crash on save", "u")) };
        Assert.That(store.FileIntake(preview), Has.Count.EqualTo(1));
        Assert.That(store.FileIntake(preview), Is.Empty);
    }

    [Test]
    public async Task A_gh_failure_is_said_rather_than_nothing_new()
    {
        var store = new TicketStore(root) { Gh = _ => (-1, "the GitHub CLI (gh) isn't installed"), Remote = _ => "https://github.com/o/alpha" };
        var m = new MainViewModel(store, new DesktopSettings(), dialogs, new OrchestratorState(Path.Combine(root, "none", "orchestrator.db")));
        await m.RefreshAsync();
        await m.IntakeAsync();
        Assert.That(m.StatusMessage, Does.Contain("gh) isn't installed"));
        Assert.That(dialogs.Asked, Is.Null);
    }

    private sealed class Answers : IDialogs
    {
        public PromptAnswer? Next { get; set; }
        public Prompt? Asked { get; set; }
        public PromptAnswer? Ask(Prompt prompt) { Asked = prompt; return Next; }
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }
}
