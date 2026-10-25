using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// M.TIX-T79: with several tickets selected, approve, reject, close, set the type of or move them in one go; each ticket is
/// changed and recorded in its own history, and one that can't take the change is named rather than failing the batch.
/// </summary>
public sealed class BulkActionTests
{
    private string root = "";
    private string dbPath = "";
    private MainViewModel model = null!;
    private readonly Queue<PromptAnswer?> answers = new();
    private int asked;

    private sealed class Answers(BulkActionTests t) : IDialogs
    {
        public PromptAnswer? Ask(Prompt prompt) { t.asked++; return t.answers.Dequeue(); }
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }

    [SetUp]
    public async Task SetUp()
    {
        answers.Clear();
        asked = 0;
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-bulk-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n" +
            "| AL-T2 | 2026-10-02 | feature | open | alpha two | — |\n" +
            "| AL-T3 | 2026-10-03 | fix | open | alpha three | — |\n" +
            "| AL-T4 | 2026-10-04 | chore | open | alpha four | — |\n" +
            "| AL-T5 | 2026-10-05 | chore | open | alpha five | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath))
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.Close("AL-T4", TicketRules.Closed, "old-ref");
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(this)) { SelectedView = MainViewModel.ReposView, ShowArchived = true };
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

    private TicketRowViewModel[] Rows(params string[] ids) =>
        [.. ids.Select(id => (model.IsQueueView ? model.QueueRows : model.TicketRows).Single(r => r.Id == id))];

    private TicketRowViewModel[] Select(params string[] ids) { var rows = Rows(ids); model.SelectedRows = rows; return rows; }

    private TicketDetail Detail(string id) => new TicketStore(root).Detail(id)!;

    private string[] Queue() { using var db = TicketsDb.Open(dbPath); return [.. db.Queue().Select(q => q.Ticket.Id)]; }

    [Test]
    public async Task Approving_several_approves_each_on_its_own_and_names_the_closed_one_as_skipped()
    {
        var before = new[] { "AL-T1", "AL-T3" }.ToDictionary(id => id, id => Detail(id).History.Count);
        await model.ApproveRowsAsync(Select("AL-T1", "AL-T3", "AL-T4"));

        Assert.That(Detail("AL-T1").Info.Approval, Is.EqualTo(TicketsDb.Approved));
        Assert.That(Detail("AL-T3").Info.Approval, Is.EqualTo(TicketsDb.Approved));
        Assert.That(Detail("AL-T4").Info.Approval, Is.Not.EqualTo(TicketsDb.Approved));
        foreach (var (id, count) in before) Assert.That(Detail(id).History.Count, Is.GreaterThan(count), $"{id} has its own history entry");
        Assert.That(model.StatusMessage, Does.Contain("AL-T1").And.Contain("AL-T3").And.Contain("Not changed: AL-T4"));

        // The batch is one undo.
        Assert.That(model.CanUndo, Is.True);
        Assert.That(await model.UndoAsync(), Is.True);
        Assert.That(Detail("AL-T1").Info.Approval, Is.Not.EqualTo(TicketsDb.Approved));
        Assert.That(Detail("AL-T3").Info.Approval, Is.Not.EqualTo(TicketsDb.Approved));
    }

    [Test]
    public async Task Rejecting_several_asks_once_and_cancelling_changes_nothing()
    {
        answers.Enqueue(null);
        await model.RejectRowsAsync(Select("AL-T1", "AL-T2"));
        Assert.That(asked, Is.EqualTo(1));
        Assert.That(Detail("AL-T1").Info.Ticket.Status, Is.EqualTo(TicketRules.Open));
        Assert.That(Detail("AL-T2").Info.Ticket.Status, Is.EqualTo(TicketRules.Open));

        answers.Enqueue(new PromptAnswer(null, "duplicate of AL-T3"));
        await model.RejectRowsAsync(Select("AL-T1", "AL-T2"));
        Assert.That(asked, Is.EqualTo(2), "one prompt for the whole selection");
        foreach (var id in new[] { "AL-T1", "AL-T2" })
        {
            var t = Detail(id).Info.Ticket;
            Assert.That((t.Status, t.Ref), Is.EqualTo((TicketRules.WontDo, "duplicate of AL-T3")), id);
        }
    }

    [Test]
    public async Task Closing_several_leaves_an_already_closed_one_alone()
    {
        answers.Enqueue(new PromptAnswer(null, "abc1234"));
        await model.CloseRowsAsync(Select("AL-T1", "AL-T4", "AL-T5"), TicketRules.Closed);

        Assert.That(asked, Is.EqualTo(1));
        foreach (var id in new[] { "AL-T1", "AL-T5" })
            Assert.That((Detail(id).Info.Ticket.Status, Detail(id).Info.Ticket.Ref), Is.EqualTo((TicketRules.Closed, "abc1234")), id);
        Assert.That(Detail("AL-T4").Info.Ticket.Ref, Is.EqualTo("old-ref"));
        Assert.That(model.StatusMessage, Does.Contain("Not changed: AL-T4"));
    }

    [Test]
    public void Closing_through_the_store_reports_an_already_closed_ticket_and_closes_the_rest()
    {
        var result = new TicketStore(root).CloseMany(["AL-T4", "AL-T5", "AL-T99"], TicketRules.Closed, "ref");
        Assert.That(result.Done, Is.EqualTo(new[] { "AL-T5" }));
        Assert.That(result.Failed.Select(f => f.Id), Is.EquivalentTo(new[] { "AL-T4", "AL-T99" }));
        Assert.That(Detail("AL-T4").Info.Ticket.Ref, Is.EqualTo("old-ref"));
    }

    [Test]
    public async Task Setting_the_type_skips_tickets_already_of_that_type()
    {
        var t3Events = Detail("AL-T3").History.Count;
        answers.Enqueue(new PromptAnswer("fix", "these are bugs"));
        await model.SetTypeRowsAsync(Select("AL-T1", "AL-T3"));

        Assert.That(Detail("AL-T1").Info.Ticket.Type, Is.EqualTo("fix"));
        Assert.That(Detail("AL-T3").History.Count, Is.EqualTo(t3Events), "an already-matching ticket isn't touched");
        Assert.That(model.StatusMessage, Does.Contain("AL-T3 (already a fix)"));
    }

    [Test]
    public async Task Moving_several_to_the_top_or_bottom_keeps_their_order()
    {
        model.SelectedView = MainViewModel.QueueView;
        await model.RefreshAsync();
        var queue = Queue();
        Assert.That(queue, Has.Length.EqualTo(4));
        var moving = new[] { queue[2], queue[3] };

        await model.MoveRowsAsync(Select(moving), toTop: true);
        Assert.That(Queue().Take(2), Is.EqualTo(moving));
        Assert.That(Queue().Skip(2), Is.EqualTo(new[] { queue[0], queue[1] }));

        var down = Queue().Take(2).ToArray();
        await model.MoveRowsAsync(Select(down), toTop: false);
        Assert.That(Queue().Skip(2), Is.EqualTo(down));
    }

    [Test]
    public void A_key_acts_on_the_whole_selection_only_from_a_selected_row()
    {
        var rows = Select("AL-T1", "AL-T2");
        var other = Rows("AL-T3")[0];
        Assert.That(model.IsMultiSelect, Is.True);
        Assert.That(model.RowsFor(rows[1]), Is.EqualTo(rows));
        Assert.That(model.RowsFor(other), Is.EqualTo(new[] { other }));

        Select("AL-T1");
        Assert.That(model.IsMultiSelect, Is.False);
        Assert.That(model.RowsFor(rows[1]), Is.EqualTo(new[] { rows[1] }));
    }
}
