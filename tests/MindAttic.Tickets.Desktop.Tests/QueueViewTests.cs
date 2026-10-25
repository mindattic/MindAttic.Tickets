using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The Queue view (M.TIX-T66): open tickets in priority order, each row ranked and spoken with its position; the ▲▼ / To
/// top buttons (and Alt+Up / Alt+Down, which call the same moves) enabled only where the ticket can move; and every
/// approve, reject and move said through the live region (<see cref="MainViewModel.Announcement"/>, WCAG 4.1.3).
/// </summary>
public sealed class QueueViewTests
{
    private string root = "";
    private string dbPath = "";
    private MainViewModel model = null!;
    private readonly Answers dialogs = new();

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-queue-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL",
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n" +
            "| AL-T3 | 2026-10-03 | chore | closed | alpha done | — |\n");
        var beta = MakeRepo("Beta", "BE",
            "| BE-T1 | 2026-10-04 | feature | open | beta one | — |\n| BE-T2 | 2026-10-05 | docs | open | beta two | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath)) db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);

        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), dialogs) { SelectedView = MainViewModel.QueueView };
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

    private string[] InDb() { using var db = TicketsDb.Open(dbPath); return [.. db.Queue().Select(q => q.Ticket.Id)]; }

    private TicketRowViewModel Row(string id) => model.QueueRows.Single(r => r.Id == id);

    private string[] Shown() => [.. model.QueueRows.Select(r => r.Id)];

    [Test]
    public void Lists_only_open_tickets_in_priority_order_each_ranked_and_spoken_with_its_place()
    {
        Assert.That(model.IsQueueView, Is.True);
        Assert.That(Shown(), Is.EqualTo(InDb()));
        Assert.That(Shown(), Does.Not.Contain("AL-T3"));
        Assert.That(model.QueueRows.Select(r => r.Rank), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(model.QueueRows[0].SpokenName, Does.StartWith($"Position 1. {model.QueueRows[0].Id}"));
    }

    [Test]
    public void Move_buttons_are_enabled_only_where_the_ticket_can_move()
    {
        var (first, middle, last) = (model.QueueRows[0], model.QueueRows[1], model.QueueRows[^1]);

        Assert.That(model.MoveUpCommand.CanExecute(first), Is.False);
        Assert.That(model.MoveTopCommand.CanExecute(first), Is.False);
        Assert.That(model.MoveDownCommand.CanExecute(first), Is.True);

        Assert.That(model.MoveUpCommand.CanExecute(middle), Is.True);
        Assert.That(model.MoveDownCommand.CanExecute(middle), Is.True);
        Assert.That(model.MoveTopCommand.CanExecute(middle), Is.True);

        Assert.That(model.MoveDownCommand.CanExecute(last), Is.False);
        Assert.That(model.MoveUpCommand.CanExecute(last), Is.True);

        // The action bar's buttons act on the selection when no row is given.
        model.SelectedQueueRow = last;
        Assert.That(model.MoveDownCommand.CanExecute(null), Is.False);
        Assert.That(model.MoveUpCommand.CanExecute(null), Is.True);
    }

    [Test]
    public async Task Moving_a_ticket_announces_its_new_place_and_re_ranks_the_queue()
    {
        var moved = model.QueueRows[^1];
        await model.MoveByAsync(moved, -1); // Alt+Up, or ▲

        Assert.That(model.Announcement, Is.EqualTo($"{moved.Id} moved to position 3 of 4."));
        Assert.That(Row(moved.Id).Rank, Is.EqualTo(3));
        Assert.That(Row(moved.Id).SpokenName, Does.StartWith($"Position 3. {moved.Id}"));
        Assert.That(InDb(), Is.EqualTo(Shown()));
        Assert.That(model.SelectedQueueRow?.Id, Is.EqualTo(moved.Id), "the moved ticket stays selected, so Alt+Up can repeat");

        await model.MoveToAsync(Row(moved.Id), 0); // To top, or Alt+Home
        Assert.That(Shown()[0], Is.EqualTo(moved.Id));
        Assert.That(model.Announcement, Is.EqualTo($"{moved.Id} moved to position 1 of 4."));
    }

    [Test]
    public async Task Moving_past_either_end_changes_nothing()
    {
        var before = Shown();
        await model.MoveByAsync(model.QueueRows[0], -1);
        await model.MoveByAsync(model.QueueRows[^1], +1);

        Assert.That(Shown(), Is.EqualTo(before));
        Assert.That(InDb(), Is.EqualTo(before));
        Assert.That(model.Announcement, Is.Empty);
    }

    [Test]
    public async Task Approving_and_rejecting_are_announced()
    {
        await model.ApproveAsync(Row("BE-T1"));
        Assert.That(model.Announcement, Is.EqualTo("BE-T1 approved."));
        Assert.That(Row("BE-T1").Approval, Is.EqualTo(TicketsDb.Approved));

        dialogs.Next = new PromptAnswer(null, "not needed");
        await model.RejectAsync(Row("AL-T1"));
        Assert.That(model.Announcement, Is.EqualTo("AL-T1 rejected and closed as wont-do."));
        Assert.That(Shown(), Does.Not.Contain("AL-T1"));
        Assert.That(model.QueueRows.Select(r => r.Rank), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    private sealed class Answers : IDialogs
    {
        public PromptAnswer? Next { get; set; }
        public PromptAnswer? Ask(Prompt prompt) => Next;
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }
}
