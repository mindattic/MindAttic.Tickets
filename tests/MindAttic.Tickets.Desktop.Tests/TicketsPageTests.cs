using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The interactive tickets page (M.TIX-T63), end to end through the window's view model and store: every repo's tickets
/// listed, approve, reject (closed wont-do and archived) and drag-and-drop reordering, each written to the ticket
/// database's queue, which the orchestrator and autopilot read.
/// </summary>
public sealed class TicketsPageTests
{
    private string root = "";
    private string dbPath = "";
    private MainViewModel model = null!;
    private readonly Answers dialogs = new();

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL",
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n" +
            "| AL-T3 | 2026-10-03 | chore | closed | alpha done | — |\n");
        var beta = MakeRepo("Beta", "BE",
            "| BE-T1 | 2026-10-04 | feature | open | beta one | — |\n| BE-T2 | 2026-10-05 | docs | open | beta two | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath)) db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);

        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), dialogs);
        await model.RefreshAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        // Selecting a moved row loads its detail pane in the background (fire and forget, as in the window); let it finish.
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

    private T InDb<T>(Func<TicketsDb, T> read)
    {
        using var db = TicketsDb.Open(dbPath);
        return read(db);
    }

    private TicketRowViewModel Row(string id) => model.QueueRows.Single(r => r.Id == id);

    private string[] Shown() => [.. model.QueueRows.Select(r => r.Id)];

    [Test]
    public void Lists_every_repos_tickets_with_closed_ones_archived()
    {
        Assert.That(model.RepoChoices, Does.Contain("Alpha").And.Contain("Beta"));
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T1", "AL-T2", "BE-T1", "BE-T2" }));
        Assert.That(model.TicketRows.Select(r => r.Id), Is.EquivalentTo(new[] { "AL-T1", "AL-T2", "BE-T1", "BE-T2" }));

        model.ShowArchived = true;
        Assert.That(model.TicketRows.Select(r => r.Id), Does.Contain("AL-T3"));
    }

    [Test]
    public async Task Approving_a_ticket_writes_it_to_the_queue()
    {
        await model.ApproveAsync(Row("BE-T1"));

        Assert.That(InDb(db => db.QueueState("BE-T1").Approval), Is.EqualTo(TicketsDb.Approved));
        Assert.That(Row("BE-T1").MayWork, Is.True);

        await model.UnapproveAsync(Row("BE-T1"));
        Assert.That(InDb(db => db.QueueState("BE-T1").Approval), Is.EqualTo(TicketsDb.Proposed));
    }

    [Test]
    public async Task Rejecting_a_ticket_closes_it_wont_do_and_archives_it()
    {
        dialogs.Next = new PromptAnswer(null, "not needed");
        await model.RejectAsync(Row("AL-T1"));

        var t = InDb(db => db.Ticket("AL-T1")!.Value.Ticket);
        Assert.That((t.Status, t.Ref), Is.EqualTo((TicketRules.WontDo, "not needed")));
        Assert.That(InDb(db => db.QueueState("AL-T1").Approval), Is.EqualTo(TicketsDb.Rejected));
        Assert.That(Shown(), Does.Not.Contain("AL-T1"));
        Assert.That(model.TicketRows.Select(r => r.Id), Does.Not.Contain("AL-T1"));

        model.ShowArchived = true;
        Assert.That(model.TicketRows.Single(r => r.Id == "AL-T1").Status, Is.EqualTo(TicketRules.WontDo));
    }

    [Test]
    public async Task Cancelling_a_rejection_changes_nothing()
    {
        dialogs.Next = null;
        await model.RejectAsync(Row("AL-T1"));
        Assert.That(InDb(db => db.Ticket("AL-T1")!.Value.Ticket.Status), Is.EqualTo(TicketRules.Open));
    }

    [Test]
    public async Task Dragging_a_ticket_sets_the_queue_order_the_orchestrator_reads()
    {
        await model.MoveToAsync(Row("BE-T2"), 0); // dropped at the top
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T2", "AL-T1", "AL-T2", "BE-T1" }));
        Assert.That(InDb(db => db.Queue().Select(q => q.Ticket.Id).ToArray()), Is.EqualTo(Shown()));

        await model.MoveToAsync(Row("AL-T1"), 3); // dropped at the bottom
        await model.MoveByAsync(Row("AL-T2"), +1); // Alt+Down
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T2", "BE-T1", "AL-T2", "AL-T1" }));
        Assert.That(InDb(db => db.Queue().Select(q => q.Ticket.Id).ToArray()), Is.EqualTo(Shown()));

        // A fresh window (or any other reader of the database) sees the same order.
        var other = new MainViewModel(new TicketStore(root), new DesktopSettings());
        await other.RefreshAsync();
        Assert.That(other.QueueRows.Select(r => r.Id), Is.EqualTo(Shown()));
    }

    [Test]
    public async Task Reordering_one_repos_filtered_queue_leaves_the_others_in_place()
    {
        await model.MoveToAsync(Row("BE-T2"), 0);
        var before = InDb(db => (db.QueueState("AL-T1").Priority, db.QueueState("AL-T2").Priority));

        model.RepoFilter = "Beta";
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T2", "BE-T1" }));
        await model.MoveToAsync(Row("BE-T1"), 0);

        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T1", "BE-T2" }));
        Assert.That(InDb(db => (db.QueueState("AL-T1").Priority, db.QueueState("AL-T2").Priority)), Is.EqualTo(before));
        Assert.That(InDb(db => db.Queue("Beta").Select(q => q.Ticket.Id).ToArray()), Is.EqualTo(new[] { "BE-T1", "BE-T2" }));
    }

    private sealed class Answers : IDialogs
    {
        public PromptAnswer? Next { get; set; }
        public PromptAnswer? Ask(Prompt prompt) => Next;
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }
}
