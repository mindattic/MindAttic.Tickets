using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>The filter bar's query and saved filters (M.TIX-T78).</summary>
public sealed class QueryFilterTests
{
    private string root = "";
    private string dbPath = "";
    private Answers dialogs = null!;
    private MainViewModel model = null!;

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-query-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL",
            "| AL-T1 | 2026-10-01 | feature | open | alpha feature | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha crash | — |\n" +
            "| AL-T3 | 2026-10-03 | fix | closed | alpha old crash | — |\n");
        var beta = MakeRepo("Beta", "BE", "| BE-T1 | 2026-10-04 | feature | open | beta feature | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath))
        {
            db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);
            db.Approve("BE-T1");
            db.SaveFilter("Approved", "approval:approved");
        }
        dialogs = new Answers();
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), dialogs);
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
    public void The_query_narrows_both_lists()
    {
        model.Query = "repo:AL type:fix";

        Assert.That(Queue(), Is.EqualTo(new[] { "AL-T2" }));
        Assert.That(model.IsFiltered, Is.True);
        model.SelectedView = MainViewModel.TicketsView;
        Assert.That(Tickets(), Is.EqualTo(new[] { "AL-T2" }), "closed tickets stay archived");

        model.Query = "repo:AL status:closed crash";
        Assert.That(Tickets(), Is.EqualTo(new[] { "AL-T3" }), "a status in the query shows archived tickets");
    }

    [Test]
    public void A_query_that_does_not_parse_says_why_and_filters_nothing_out()
    {
        model.Query = "status:done";

        Assert.That(model.QueryError, Does.Contain("Unknown status 'done'"));
        Assert.That(Queue(), Has.Length.EqualTo(3));
        Assert.That(model.SaveFilterCommand.CanExecute(null), Is.False);

        model.Query = "status:open";
        Assert.That(model.QueryError, Is.Empty);
    }

    [Test]
    public async Task Saved_filters_are_picked_saved_and_deleted()
    {
        Assert.That(model.SavedFilterChoices, Is.EqualTo(new[] { MainViewModel.NoSavedFilter, "Approved" }));

        model.SelectedSavedFilter = "Approved";
        Assert.That(model.Query, Is.EqualTo("approval:approved"));
        Assert.That(Queue(), Is.EqualTo(new[] { "BE-T1" }));

        model.Query = "type:fix";
        Assert.That(model.SelectedSavedFilter, Is.EqualTo(MainViewModel.NoSavedFilter), "typing past it unpicks it");
        dialogs.Next = new PromptAnswer(null, "Fixes");
        await model.SaveFilterAsync();
        Assert.That(model.SavedFilterChoices, Is.EqualTo(new[] { MainViewModel.NoSavedFilter, "Approved", "Fixes" }));
        Assert.That(model.SelectedSavedFilter, Is.EqualTo("Fixes"));
        using (var db = TicketsDb.Open(dbPath)) Assert.That(db.FindFilter("fixes")?.Query, Is.EqualTo("type:fix"));

        await model.DeleteFilterAsync();
        Assert.That(model.SavedFilterChoices, Is.EqualTo(new[] { MainViewModel.NoSavedFilter, "Approved" }));
        Assert.That(model.Query, Is.EqualTo("type:fix"), "the query stays");
    }

    [Test]
    public async Task Opening_a_ticket_the_query_hides_clears_it()
    {
        model.Query = "repo:BE";

        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);
        Assert.That(model.Query, Is.Empty);
        Assert.That(model.SelectedQueueRow?.Id, Is.EqualTo("AL-T1"));
    }

    private sealed class Answers : IDialogs
    {
        public PromptAnswer? Next { get; set; }
        public PromptAnswer? Ask(Prompt prompt) => Next;
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }
}
