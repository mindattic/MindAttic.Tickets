using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// M.TIX-T80: Ctrl+Z / the undo toast take back approve, reject, a queue move, a close, a board move and link changes with
/// compensating changes, newest first; the history keeps both, and a change someone else has changed again isn't undone.
/// </summary>
public sealed class UndoTests
{
    private string root = "";
    private string dbPath = "";
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
        answers.Clear();
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-undo-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n" +
            "| AL-T2 | 2026-10-02 | feature | open | alpha two | — |\n" +
            "| AL-T3 | 2026-10-03 | fix | open | alpha three | — |\n" +
            "| AL-T4 | 2026-10-04 | chore | open | alpha four | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath))
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.Approve("AL-T2");
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(answers)) { SelectedView = MainViewModel.QueueView };
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

    private TicketRowViewModel Row(string id) => model.QueueRows.Single(r => r.Id == id);

    private TicketDetail Detail(string id) => new TicketStore(root).Detail(id)!;

    private string[] Queue() { using var db = TicketsDb.Open(dbPath); return [.. db.Queue().Select(q => q.Ticket.Id)]; }

    private static bool Undone(TicketDetail d) => d.History.Any(e => e.Field == "undo");

    [Test]
    public async Task Undoing_an_approval_puts_it_back_to_proposed_and_keeps_the_history()
    {
        var events = Detail("AL-T1").History.Count;
        await model.ApproveAsync(Row("AL-T1"));
        Assert.That(Detail("AL-T1").Info.Approval, Is.EqualTo(TicketsDb.Approved));
        Assert.That((model.CanUndo, model.IsUndoToastShown, model.UndoToast), Is.EqualTo((true, true, "AL-T1 approved.")));

        Assert.That(await model.UndoAsync(), Is.True);
        var d = Detail("AL-T1");
        Assert.That(d.Info.Approval, Is.EqualTo(TicketsDb.Proposed));
        Assert.That(Undone(d), Is.True);
        Assert.That(d.History.Count, Is.GreaterThan(events + 1), "the approval and its undo are both kept");
        Assert.That((model.CanUndo, model.IsUndoToastShown), Is.EqualTo((false, false)));
        Assert.That(model.StatusMessage, Does.Contain("Undone: AL-T1 approved."));
    }

    [Test]
    public async Task Undoing_a_reject_reopens_it_with_its_old_ref()
    {
        var before = Detail("AL-T2");
        answers.Enqueue(new PromptAnswer(null, "not needed"));
        await model.RejectAsync(Row("AL-T2"));
        Assert.That(Detail("AL-T2").Info.Ticket.Status, Is.EqualTo(TicketRules.WontDo));

        Assert.That(await model.UndoAsync(), Is.True);
        var d = Detail("AL-T2");
        Assert.That((d.Info.Ticket.Status, d.Info.Ticket.Ref, d.Info.Approval),
            Is.EqualTo((TicketRules.Open, before.Info.Ticket.Ref, TicketsDb.Approved)));
        Assert.That(Undone(d), Is.True);
        Assert.That(model.QueueRows.Select(r => r.Id), Does.Contain("AL-T2"));
    }

    [Test]
    public async Task Undoing_a_board_close_reopens_the_ticket()
    {
        model.SelectedView = MainViewModel.BoardView;
        await model.RefreshAsync();
        answers.Enqueue(new PromptAnswer(null, "UndoTests"));
        Assert.That(await model.MoveCardAsync(model.BoardColumns.SelectMany(c => c.Rows).Single(r => r.Id == "AL-T2"), BoardColumn.Done), Is.True);
        Assert.That(Detail("AL-T2").Info.Ticket.Status, Is.EqualTo(TicketRules.Closed));

        Assert.That(await model.UndoAsync(), Is.True);
        var d = Detail("AL-T2");
        Assert.That((d.Info.Ticket.Status, d.Info.Approval), Is.EqualTo((TicketRules.Open, TicketsDb.Approved)));
        Assert.That(d.Info.Ticket.Ref, Is.Not.EqualTo("UndoTests"));
    }

    [Test]
    public async Task Undoing_a_move_to_in_progress_clears_the_work_and_the_approval()
    {
        model.SelectedView = MainViewModel.BoardView;
        await model.RefreshAsync();
        Assert.That(await model.MoveCardAsync(model.BoardColumns.SelectMany(c => c.Rows).Single(r => r.Id == "AL-T1"), BoardColumn.InProgress), Is.True);
        Assert.That(Detail("AL-T1").Work.Session, Is.EqualTo(Board.HandSession));

        Assert.That(await model.UndoAsync(), Is.True);
        var d = Detail("AL-T1");
        Assert.That((d.Work.IsWorking, d.Info.Approval), Is.EqualTo((false, TicketsDb.Proposed)));
    }

    [Test]
    public async Task Undoing_a_queue_move_restores_the_order()
    {
        var before = Queue();
        await model.MoveToAsync(model.QueueRows[^1], 0);
        Assert.That(Queue(), Is.Not.EqualTo(before));

        Assert.That(await model.UndoAsync(), Is.True);
        Assert.That(Queue(), Is.EqualTo(before));
        Assert.That(model.QueueRows.Select(r => r.Id), Is.EqualTo(before));
    }

    [Test]
    public async Task Undoing_a_link_add_removes_it_and_undoing_a_remove_brings_it_back()
    {
        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);
        answers.Enqueue(new PromptAnswer("related", "AL-T4"));
        await model.AddLinkAsync();
        Assert.That(Detail("AL-T1").Links.Any(l => l.To == "AL-T4"), Is.True);
        Assert.That(await model.UndoAsync(), Is.True);
        Assert.That(Detail("AL-T1").Links, Is.Empty);

        using (var db = TicketsDb.Open(dbPath)) db.AddLink("AL-T1", "AL-T3", "requires");
        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);
        await model.RemoveLinkAsync(model.Detail!.Links.Single(l => l.Other == "AL-T3"));
        Assert.That(Detail("AL-T1").Links, Is.Empty);
        Assert.That(await model.UndoAsync(), Is.True);
        Assert.That(Detail("AL-T1").Links.Select(l => (l.From, l.To, l.Kind)), Is.EqualTo(new[] { ("AL-T1", "AL-T3", "requires") }));
    }

    [Test]
    public async Task Undo_goes_newest_first()
    {
        await model.ApproveAsync(Row("AL-T1"));
        await model.ApproveAsync(Row("AL-T4"));
        Assert.That(model.NextUndo, Is.EqualTo("AL-T4 approved."));

        await model.UndoAsync();
        Assert.That((Detail("AL-T4").Info.Approval, Detail("AL-T1").Info.Approval), Is.EqualTo((TicketsDb.Proposed, TicketsDb.Approved)));
        await model.UndoAsync();
        Assert.That(Detail("AL-T1").Info.Approval, Is.EqualTo(TicketsDb.Proposed));
        Assert.That(await model.UndoAsync(), Is.False);
        Assert.That(model.StatusMessage, Is.EqualTo("Nothing to undo."));
    }

    [Test]
    public async Task A_change_someone_else_changed_again_is_not_undone()
    {
        await model.ApproveAsync(Row("AL-T1"));
        using (var db = TicketsDb.Open(dbPath)) db.Reject("AL-T1", "someone else");

        Assert.That(await model.UndoAsync(), Is.False);
        Assert.That(model.StatusMessage, Does.Contain("changed since"));
        var d = Detail("AL-T1");
        Assert.That(d.Info.Ticket.Status, Is.EqualTo(TicketRules.WontDo));
        Assert.That(Undone(d), Is.False);
        Assert.That(model.CanUndo, Is.False, "a refused undo leaves the stack");
    }

    [Test]
    public async Task Nothing_changed_offers_no_undo()
    {
        await model.ApproveAsync(Row("AL-T2")); // already approved
        Assert.That((model.CanUndo, model.IsUndoToastShown), Is.EqualTo((false, false)));
    }
}
