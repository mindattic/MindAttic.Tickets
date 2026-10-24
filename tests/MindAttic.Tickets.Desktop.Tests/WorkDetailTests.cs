using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>M.TIX-T134: the detail pane shows a ticket's assignee and reservation, assigns and reserves, and a ticket being
/// worked links back to the Orchestrator (MindAttic.Orchestrator.exe --show &lt;email|session|ticket&gt;).</summary>
public sealed class WorkDetailTests
{
    private string root = "";
    private MainViewModel model = null!;
    private readonly Queue<PromptAnswer?> answers = new();

    private sealed class Answers(Queue<PromptAnswer?> q) : IDialogs
    {
        public PromptAnswer? Ask(Prompt prompt) => q.Dequeue();
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-work-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) db.Import([new Repo("Alpha", alpha)]);
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(answers));
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

    [Test]
    public async Task Assign_and_reserve_from_the_detail_pane()
    {
        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);
        Assert.That((model.Detail!.AssigneeText, model.Detail.ReservedText, model.Detail.IsWorking), Is.EqualTo(("Nobody", "None", false)));

        answers.Enqueue(new PromptAnswer(null, "Ryan@Example.com"));
        model.AssignCommand.Execute(null);
        await WaitFor(() => model.Detail?.IsAssigned == true);
        Assert.That(model.Detail!.AssigneeText, Is.EqualTo("ryan@example.com"));

        answers.Enqueue(new PromptAnswer(null, "1.5M"));
        model.ReserveCommand.Execute(null);
        await WaitFor(() => model.Detail?.Work.Reserved > 0);
        Assert.That(model.Detail!.ReservedText, Is.EqualTo("1.5M tokens"));

        answers.Enqueue(new PromptAnswer(null, "plenty"));
        model.ReserveCommand.Execute(null);
        Assert.That(model.Detail!.Error, Does.Contain("Tokens"));
    }

    [Test]
    public async Task A_ticket_being_worked_links_its_login_session_and_itself_to_the_orchestrator()
    {
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) db.SetWorking("AL-T1", "ryan@example.com", "sess-42");
        await model.OpenTicketAsync("AL-T1");
        var d = model.Detail!;
        Assert.That(d.IsWorking, Is.True);
        Assert.That(d.WorkingText, Does.StartWith("Being worked on ryan@example.com since "));
        Assert.That(d.OrchestratorTargets.Select(t => t.Target), Is.EqualTo(new[] { "ryan@example.com", "sess-42", "AL-T1" }));

        var opened = new List<string>();
        model.OpenOrchestrator = t => { opened.Add(t); return null; };
        model.OpenInOrchestratorCommand.Execute("sess-42");
        Assert.That(opened, Is.EqualTo(new[] { "sess-42" }));
        Assert.That(model.StatusMessage, Is.EqualTo("Opened the Orchestrator on sess-42."));
    }

    [Test]
    public void The_orchestrator_link_is_harmless_without_the_exe_and_never_passes_an_option()
    {
        Assert.That(OrchestratorLink.Arguments("AL-T1"), Is.EqualTo(new[] { "--show", "AL-T1" }));
        Assert.That(OrchestratorLink.IsTarget("ryan@example.com") && OrchestratorLink.IsTarget("M.TIX-T134") && OrchestratorLink.IsTarget("0f8c-12ab"), Is.True);
        Assert.That(OrchestratorLink.IsTarget("--install") || OrchestratorLink.IsTarget("a b") || OrchestratorLink.IsTarget(""), Is.False);
        Assert.That(OrchestratorLink.Open("AL-T1", Path.Combine(root, "missing.exe")), Does.Contain("isn't installed"));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.That(condition(), Is.True, "timed out");
    }
}
