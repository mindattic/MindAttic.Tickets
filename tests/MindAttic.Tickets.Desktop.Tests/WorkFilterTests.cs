using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// Tickets Desktop as the one place to manage tickets (M.TIX-T133): the Queued / Needs approval / Blocked filters beside
/// repo, status and type, the Tickets list grouped by repo, and <c>--ticket</c> opening a listed ticket without a re-read.
/// </summary>
public sealed class WorkFilterTests
{
    private string root = "";
    private string dbPath = "";
    private MainViewModel model = null!;

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-work-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL",
            "| AL-T1 | 2026-10-01 | feature | open | alpha feature | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha fix | — |\n" +
            "| AL-T3 | 2026-10-03 | chore | closed | alpha done | — |\n");
        var beta = MakeRepo("Beta", "BE",
            "| BE-T1 | 2026-10-04 | feature | open | beta feature | — |\n| BE-T2 | 2026-10-05 | fix | open | beta fix | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath))
        {
            db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);
            db.Approve("BE-T1");
            db.AddLink("BE-T2", "AL-T2", "requires"); // waits on an open ticket in another repo
            db.AddLink("AL-T2", "AL-T3", "requires"); // waits only on a closed one: not blocked
        }
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

    private string[] Queue() => [.. model.QueueRows.Select(r => r.Id)];
    private string[] Tickets() => [.. model.TicketRows.Select(r => r.Id)];

    [Test]
    public void Blocked_shows_tickets_waiting_on_other_open_tickets()
    {
        model.WorkFilter = MainViewModel.Blocked;

        Assert.That(Queue(), Is.EqualTo(new[] { "BE-T2" }));
        Assert.That(model.QueueRows.Single().BlockedText, Does.Contain("AL-T2"));
        Assert.That(model.IsFiltered, Is.True);
        Assert.That(model.Headline, Does.Contain("1 blocked"));
    }

    [Test]
    public void Needs_approval_shows_open_tickets_autopilot_wont_take_until_approved()
    {
        model.WorkFilter = MainViewModel.NeedsApproval;
        Assert.That(Queue(), Is.EqualTo(new[] { "AL-T1" }));
    }

    [Test]
    public void Queued_shows_what_autopilot_takes_next()
    {
        model.WorkFilter = MainViewModel.Queued;

        Assert.That(Queue(), Is.EquivalentTo(new[] { "AL-T2", "BE-T1" }), "a fix and an approved feature; BE-T2 is blocked");
        model.SelectedView = MainViewModel.TicketsView;
        model.ShowArchived = true;
        Assert.That(Tickets(), Is.EquivalentTo(new[] { "AL-T2", "BE-T1" }), "closed tickets are never queued");
    }

    [Test]
    public async Task Closing_the_blocker_unblocks_it()
    {
        using (var db = TicketsDb.Open(dbPath)) db.Close("AL-T2", TicketRules.Closed, "done");
        await model.RefreshAsync();

        model.WorkFilter = MainViewModel.Blocked;
        Assert.That(Queue(), Is.Empty);
        model.WorkFilter = MainViewModel.Queued;
        Assert.That(Queue(), Does.Contain("BE-T2"));
    }

    [Test]
    public void Blockers_are_followed_all_the_way_down_and_cycles_end()
    {
        Ticket T(string id, string status) => new(id, "2026-10-01", "feature", status, id, "");
        var blockers = TicketStore.OpenBlockers(
            [T("A-T1", "open"), T("A-T2", "closed"), T("A-T3", "open"), T("A-T4", "open")],
            [new Link("A-T1", "a-t2", "requires", null), new Link("A-T2", "A-T3", "requires", null),
             new Link("A-T3", "A-T4", "relates", null), new Link("A-T4", "A-T1", "requires", null)]);

        Assert.That(blockers["A-T1"], Is.EqualTo(new[] { "A-T3" }), "through the closed A-T2; a relates link isn't a wait");
        Assert.That(blockers["A-T4"], Is.EqualTo(new[] { "A-T1", "A-T3" }));
        Assert.That(blockers.ContainsKey("A-T3"), Is.False);
    }

    [Test]
    public void Tickets_are_grouped_by_repo_and_sorted_within_each()
    {
        model.SelectedView = MainViewModel.TicketsView;
        model.SortBy = "Date";
        model.SortDescending = true;

        Assert.That(model.GroupByRepo, Is.True, "on by default");
        Assert.That(Tickets(), Is.EqualTo(new[] { "AL-T2", "AL-T1", "BE-T2", "BE-T1" }));

        model.GroupByRepo = false;
        Assert.That(Tickets(), Is.EqualTo(new[] { "BE-T2", "BE-T1", "AL-T2", "AL-T1" }));
    }

    [Test]
    public async Task Opening_a_listed_ticket_clears_the_work_filter_that_hides_it()
    {
        model.WorkFilter = MainViewModel.Blocked;

        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);

        Assert.That(model.WorkFilter, Is.EqualTo(MainViewModel.AnyWork));
        Assert.That(model.SelectedQueueRow?.Id, Is.EqualTo("AL-T1"));
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T1"));
    }

    [Test]
    public async Task A_ticket_filed_after_the_last_read_still_opens()
    {
        using (var db = TicketsDb.Open(dbPath)) db.Add("Beta", "feature", TicketRules.Open, "filed just now", "");

        Assert.That(await model.OpenTicketAsync("BE-T3"), Is.True);
        Assert.That(model.Detail?.Id, Is.EqualTo("BE-T3"));
    }
}
