using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>M.TIX-T77: the Board view's Proposed / Approved / In progress / Done columns, and moving a card (drag or keyboard)
/// changes its approval, work record or status.</summary>
public sealed class BoardTests
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
        answers.Clear();
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-board-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | proposed one | — |\n" +
            "| AL-T2 | 2026-10-02 | feature | open | approved one | — |\n" +
            "| AL-T3 | 2026-10-03 | feature | open | worked one | — |\n" +
            "| AL-T4 | 2026-10-04 | fix | closed | done one | abc123 |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.Approve("AL-T2");
            db.Approve("AL-T3");
            db.SetWorking("AL-T3", "ryan@example.com", "sess-3");
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(answers)) { SelectedView = MainViewModel.BoardView };
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

    private string[] Ids(BoardColumn c) => [.. model.BoardColumnOf(c).Rows.Select(r => r.Id)];

    private TicketRowViewModel Card(string id) => model.BoardColumns.SelectMany(c => c.Rows).Single(r => r.Id == id);

    private TicketDetail Detail(string id) => new TicketStore(root).Detail(id)!;

    [Test]
    public void Tickets_sit_in_the_column_for_their_approval_work_and_status()
    {
        Assert.That(model.BoardColumns.Select(c => c.Title), Is.EqualTo(new[] { "Proposed", "Approved", "In progress", "Done" }));
        Assert.That(Ids(BoardColumn.Proposed), Is.EqualTo(new[] { "AL-T1" }));
        Assert.That(Ids(BoardColumn.Approved), Is.EqualTo(new[] { "AL-T2" }));
        Assert.That(Ids(BoardColumn.InProgress), Is.EqualTo(new[] { "AL-T3" }));
        Assert.That(Ids(BoardColumn.Done), Is.EqualTo(new[] { "AL-T4" }));
        Assert.That(model.BoardColumnOf(BoardColumn.Done).Heading, Is.EqualTo("Done: 1"));
    }

    [Test]
    public async Task Moving_right_by_keyboard_approves_then_starts_work_then_closes()
    {
        await model.MoveCardByAsync(Card("AL-T1"), +1);
        Assert.That(Ids(BoardColumn.Approved), Does.Contain("AL-T1"));
        Assert.That(Detail("AL-T1").Info.Approval, Is.EqualTo(TicketsDb.Approved));
        Assert.That(model.SelectedBoardRow?.Id, Is.EqualTo("AL-T1"));

        await model.MoveCardByAsync(Card("AL-T1"), +1);
        Assert.That(Ids(BoardColumn.InProgress), Does.Contain("AL-T1"));
        Assert.That(Detail("AL-T1").Work.Session, Is.EqualTo(Board.HandSession));

        answers.Enqueue(new PromptAnswer(null, "BoardTests"));
        await model.MoveCardByAsync(Card("AL-T1"), +1);
        var done = Detail("AL-T1");
        Assert.That(Ids(BoardColumn.Done), Does.Contain("AL-T1"));
        Assert.That((done.Info.Ticket.Status, done.Info.Ticket.Ref, done.Work.IsWorking), Is.EqualTo((TicketRules.Closed, "BoardTests", false)));
    }

    [Test]
    public async Task Moving_left_out_of_in_progress_clears_the_work_and_back_to_proposed_unapproves()
    {
        Assert.That(await model.MoveCardAsync(Card("AL-T3"), BoardColumn.Approved), Is.True);
        Assert.That(Detail("AL-T3").Work.IsWorking, Is.False);
        Assert.That(Ids(BoardColumn.Approved), Does.Contain("AL-T3"));

        await model.MoveCardAsync(Card("AL-T2"), BoardColumn.Proposed);
        Assert.That(Detail("AL-T2").Info.Approval, Is.EqualTo(TicketsDb.Proposed));
        Assert.That(Ids(BoardColumn.Proposed), Is.EqualTo(new[] { "AL-T1", "AL-T2" }));
    }

    [Test]
    public async Task Cancelling_the_close_prompt_or_moving_a_closed_card_changes_nothing()
    {
        answers.Enqueue(null);
        Assert.That(await model.MoveCardAsync(Card("AL-T2"), BoardColumn.Done), Is.False);
        Assert.That(Detail("AL-T2").Info.Ticket.Status, Is.EqualTo(TicketRules.Open));

        Assert.That(await model.MoveCardAsync(Card("AL-T4"), BoardColumn.Approved), Is.False);
        Assert.That(model.StatusMessage, Does.Contain("can't be moved back"));
        Assert.That(Ids(BoardColumn.Done), Is.EqualTo(new[] { "AL-T4" }));

        await model.MoveCardByAsync(Card("AL-T1"), -1);
        Assert.That(model.StatusMessage, Does.Contain("first column"));
    }

    [Test]
    public void The_store_refuses_to_reopen_a_closed_ticket()
    {
        var store = new TicketStore(root);
        Assert.Throws<InvalidOperationException>(() => store.MoveCard("AL-T4", BoardColumn.Proposed));
        Assert.DoesNotThrow(() => store.MoveCard("AL-T4", BoardColumn.Done));
    }
}
