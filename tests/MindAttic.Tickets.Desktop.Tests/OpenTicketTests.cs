using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// <c>--ticket ID</c> (M.TIX-T117): Tickets Desktop opens on the named ticket, selected with its detail pane open, and a
/// second start hands its id to the window already open. The Orchestrator dashboard's ticket links start it this way.
/// </summary>
public sealed class OpenTicketTests
{
    private string root = "";
    private MainViewModel model = null!;

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-open-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n" +
            "| AL-T3 | 2026-10-03 | chore | closed | alpha done | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) db.Import([new Repo("Alpha", alpha)]);

        model = new MainViewModel(new TicketStore(root), new DesktopSettings());
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

    [TestCase(new[] { "--root", "D:\\x", "--ticket", "m.tix-t117" }, "M.TIX-T117")]
    [TestCase(new[] { "--ticket=AL-T2", "--root", "D:\\x" }, "AL-T2")]
    [TestCase(new[] { "--TICKET", " AL-T1 " }, "AL-T1")]
    [TestCase(new[] { "--root", "D:\\x" }, null)]
    [TestCase(new[] { "--ticket" }, null)]
    [TestCase(new[] { "--ticket", "--high-contrast" }, null)]
    public void Reads_the_ticket_from_the_command_line(string[] args, string? expected) =>
        Assert.That(TicketHandoff.TicketArg(args), Is.EqualTo(expected));

    [Test]
    public async Task An_open_ticket_opens_selected_in_the_queue_with_its_detail_pane()
    {
        model.SelectedView = MainViewModel.TicketsView;

        Assert.That(await model.OpenTicketAsync("al-t2"), Is.True);

        Assert.That(model.SelectedView, Is.EqualTo(MainViewModel.QueueView), "the queue, where it can be approved and moved");
        Assert.That(model.SelectedQueueRow?.Id, Is.EqualTo("AL-T2"));
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T2"));
        Assert.That(model.ApproveCommand.CanExecute(null), Is.True);
        Assert.That(model.MoveTopCommand.CanExecute(null), Is.True);
        Assert.That(model.CloseTicketCommand.CanExecute(null), Is.True);
    }

    [Test]
    public async Task A_closed_ticket_opens_in_tickets_with_archived_shown()
    {
        Assert.That(await model.OpenTicketAsync("AL-T3"), Is.True);

        Assert.That(model.SelectedView, Is.EqualTo(MainViewModel.TicketsView));
        Assert.That(model.ShowArchived, Is.True);
        Assert.That(model.SelectedTicketRow?.Id, Is.EqualTo("AL-T3"));
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T3"));
    }

    [Test]
    public async Task Filters_that_hide_the_ticket_are_cleared()
    {
        model.Search = "alpha one";
        Assert.That(model.QueueRows.Select(r => r.Id), Is.EqualTo(new[] { "AL-T1" }));

        Assert.That(await model.OpenTicketAsync("AL-T2"), Is.True);

        Assert.That(model.Search, Is.Empty);
        Assert.That(model.SelectedQueueRow?.Id, Is.EqualTo("AL-T2"));
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T2"));
    }

    [Test]
    public async Task Opening_the_selected_ticket_again_reopens_its_closed_detail_pane()
    {
        await model.OpenTicketAsync("AL-T1");
        model.CloseDetailCommand.Execute(null);
        Assert.That(model.IsDetailOpen, Is.False);

        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);

        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T1"));
    }

    [Test]
    public async Task An_unknown_ticket_says_so_and_changes_nothing()
    {
        Assert.That(await model.OpenTicketAsync("AL-T99"), Is.False);

        Assert.That(model.StatusMessage, Does.Contain("AL-T99"));
        Assert.That(model.Detail, Is.Null);
    }

    [Test]
    public async Task A_second_start_hands_its_ticket_to_the_open_window()
    {
        var pipe = "MindAttic.Tickets.Desktop.Tests." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var listening = TicketHandoff.ListenAsync(id => received.TrySetResult(id), stop.Token, pipe);

        Assert.That(await Task.Run(() => TicketHandoff.Send("al-t2", TimeSpan.FromSeconds(5), pipe)), Is.True);
        var id = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(id, Is.EqualTo("AL-T2"));

        Assert.That(await model.OpenTicketAsync(id), Is.True);
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T2"));

        stop.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Sending_with_no_window_open_gives_up()
    {
        var pipe = "MindAttic.Tickets.Desktop.Tests." + Guid.NewGuid().ToString("N");
        Assert.That(TicketHandoff.Send("AL-T1", TimeSpan.FromMilliseconds(200), pipe), Is.False);
    }
}
