using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The Tickets view (M.TIX-T67, the Repos tab): every repo's tickets in one list, narrowed by the repo, status, type and
/// approval filters and the text search, ordered by clicking a column header, with closed and wont-do tickets archived
/// until the show-archived toggle (or a status filter) brings them back.
/// </summary>
public sealed class TicketsViewTests
{
    private string root = "";
    private MainViewModel model = null!;

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-view-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL",
            "| AL-T2 | 2026-10-02 | fix | open | parser crash | — |\n| AL-T9 | 2026-10-09 | feature | open | export to csv | — |\n" +
            "| AL-T10 | 2026-10-10 | feature | open | import wizard | Story-7 |\n| AL-T3 | 2026-10-03 | chore | closed | tidy build | — |\n");
        var beta = MakeRepo("Beta", "BE",
            "| BE-T1 | 2026-10-04 | feature | open | beta dashboard | — |\n| BE-T2 | 2026-10-05 | docs | wont-do | old docs | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);
            db.Approve("AL-T9");
            db.Approve("BE-T1");
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings()) { SelectedView = MainViewModel.TicketsView, GroupByRepo = false };
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

    private string[] Shown() => [.. model.TicketRows.Select(r => r.Id)];

    [Test]
    public void Lists_every_repos_open_tickets_with_archived_ones_behind_the_toggle()
    {
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T2", "AL-T9", "AL-T10", "BE-T1" }), "by ID: repo, then number (T9 before T10)");

        model.ShowArchived = true;
        Assert.That(Shown(), Is.EquivalentTo(new[] { "AL-T2", "AL-T3", "AL-T9", "AL-T10", "BE-T1", "BE-T2" }));
        Assert.That(model.StatusMessage, Does.Contain("6 of 6"));

        model.ShowArchived = false;
        Assert.That(Shown(), Does.Not.Contain("AL-T3").And.Not.Contain("BE-T2"));
    }

    [Test]
    public void A_status_filter_shows_that_status_even_when_archived_is_off()
    {
        model.StatusFilter = TicketRules.WontDo;
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T2" }));

        model.StatusFilter = TicketRules.Open;
        model.ShowArchived = true;
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T2", "AL-T9", "AL-T10", "BE-T1" }), "the status filter wins over the toggle");
    }

    [Test]
    public void Repo_type_and_approval_filters_combine()
    {
        model.RepoFilter = "Alpha";
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T2", "AL-T9", "AL-T10" }));

        model.TypeFilter = "feature";
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T9", "AL-T10" }));

        model.ApprovalFilter = TicketsDb.Approved;
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T9" }));

        model.RepoFilter = MainViewModel.AllRepos;
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T9", "BE-T1" }));
        Assert.That(model.IsFiltered, Is.True);
    }

    [Test]
    public void Search_matches_id_summary_ref_and_repo_ignoring_case()
    {
        model.Search = "CSV";
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T9" }), "summary");

        model.Search = "story-7";
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T10" }), "ref");

        model.Search = "be-t";
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T1" }), "id; the wont-do BE-T2 stays archived");

        model.Search = "beta";
        model.ShowArchived = true;
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T1", "BE-T2" }), "repo");

        model.Search = "";
        Assert.That(Shown(), Has.Length.EqualTo(6));
    }

    [Test]
    public void Clicking_a_column_header_sorts_by_it_and_again_reverses()
    {
        model.ToggleSortCommand.Execute("Date");
        Assert.That((model.SortBy, model.SortDescending), Is.EqualTo(("Date", false)));
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T2", "BE-T1", "AL-T9", "AL-T10" }));
        Assert.That(model.Announcement, Is.EqualTo("Sorted by Date, ascending."));

        model.ToggleSortCommand.Execute("Date");
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T10", "AL-T9", "BE-T1", "AL-T2" }));
        Assert.That(model.Announcement, Is.EqualTo("Sorted by Date, descending."));

        model.ToggleSortCommand.Execute("Summary");
        Assert.That(model.SortDescending, Is.False, "a new column starts ascending");
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T1", "AL-T9", "AL-T10", "AL-T2" }));
    }

    [Test]
    public void Sorting_by_id_descending_reverses_the_numbers_too()
    {
        model.ToggleSortCommand.Execute("Type");
        model.ToggleSortCommand.Execute("ID");
        model.ToggleSortCommand.Execute("ID");

        Assert.That(model.SortDescending, Is.True);
        Assert.That(Shown(), Is.EqualTo(new[] { "BE-T1", "AL-T10", "AL-T9", "AL-T2" }));

        model.GroupByRepo = true;
        Assert.That(Shown(), Is.EqualTo(new[] { "AL-T10", "AL-T9", "AL-T2", "BE-T1" }), "grouped: repos in order, numbers reversed within each");
    }
}
