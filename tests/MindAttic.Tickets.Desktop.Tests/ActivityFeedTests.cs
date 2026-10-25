using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>M.TIX-T84: the Activity view shows every ticket change across the workspace as it happens, and a toast says when an
/// agent closes a ticket.</summary>
public sealed class ActivityFeedTests
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
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-activity-" + Guid.NewGuid().ToString("N"));
        Repo Make(string name, string code, string rows)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            Directory.CreateDirectory(Path.Combine(dir, "docs"));
            File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
                $"---\ncodex: 1\nproject: {name}\ncode: {code}\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# {name} — User Stories\n\n" +
                "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" + rows);
            return new Repo(name, dir);
        }
        var alpha = Make("Alpha", "AL", "| AL-T1 | 2026-10-01 | fix | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n");
        var beta = Make("Beta", "BE", "| BE-T1 | 2026-10-01 | fix | open | beta one | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([alpha, beta]);
            db.Approve("AL-T1"); // history from before the window opened
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(answers)) { SelectedView = MainViewModel.ActivityView };
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

    /// <summary>Another process (an agent's CLI) changes the database; the window then refreshes as its file watcher makes it.</summary>
    private async Task Elsewhere(Action<TicketsDb> change)
    {
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) change(db);
        await model.RefreshAsync();
    }

    [Test]
    public async Task The_feed_shows_history_at_once_then_every_repos_changes_as_they_happen_newest_first_without_toasting_the_past()
    {
        Assert.That(model.Views, Does.Contain(MainViewModel.ActivityView));
        Assert.That(model.ShowsTicketFilters, Is.False);
        Assert.That(model.ActivityRows.Select(r => (r.TicketId, r.Text)), Does.Contain(("AL-T1", "approval: proposed → approved")));
        Assert.That(model.Toasts, Is.Empty);

        await Elsewhere(db => db.Approve("BE-T1"));
        await Elsewhere(db => db.Close("AL-T2", TicketRules.Closed, "abc123"));

        Assert.That(model.ActivityRows[0].TicketId, Is.EqualTo("AL-T2"));
        Assert.That(model.ActivityRows.Select(r => r.Seq), Is.Ordered.Descending);
        var beta = model.ActivityRows.First(r => r.TicketId == "BE-T1");
        Assert.That((beta.Repo, beta.Text, beta.Summary), Is.EqualTo(("Beta", "approval: proposed → approved", "beta one")));
        Assert.That(model.ActivityRows.Single(r => r is { TicketId: "AL-T2", IsClose: true }).Text, Is.EqualTo("closed"));
        Assert.That(model.Toasts, Is.Empty, "nobody was working AL-T2: a hand close isn't an agent's");
        Assert.That(model.StatusMessage, Does.StartWith("Activity: "));

        // The repo filter and search narrow the feed.
        model.RepoFilter = "Beta";
        Assert.That(model.ActivityRows.Select(r => r.Repo).Distinct(), Is.EqualTo(new[] { "Beta" }));
        model.RepoFilter = MainViewModel.AllRepos;
        model.Search = "alpha two";
        Assert.That(model.ActivityRows.Select(r => r.TicketId).Distinct(), Is.EqualTo(new[] { "AL-T2" }));
    }

    [Test]
    public async Task An_agent_closing_a_ticket_toasts_who_closed_what_and_opening_the_toast_shows_the_ticket()
    {
        await Elsewhere(db => db.SetWorking("BE-T1", "agent@example.com", "sess-7"));
        Assert.That(model.Toasts, Is.Empty);

        await Elsewhere(db => db.Close("BE-T1", TicketRules.Closed, "def456"));

        var toast = model.Toasts.Single();
        Assert.That((toast.TicketId, toast.Title, toast.Body), Is.EqualTo(("BE-T1", "agent@example.com closed BE-T1", "Beta: beta one")));
        Assert.That(model.Announcement, Is.EqualTo(toast.Spoken));
        Assert.That(model.ActivityRows[0].TicketId, Is.EqualTo("BE-T1"));
        Assert.That(model.ActivityRows.Single(r => r is { TicketId: "BE-T1", IsClose: true }).Text, Is.EqualTo("closed by agent@example.com"));

        // Another refresh doesn't toast it twice.
        await model.RefreshAsync();
        Assert.That(model.Toasts, Has.Count.EqualTo(1));

        model.OpenToastCommand.Execute(toast);
        for (var i = 0; i < 100 && model.Detail?.Id != "BE-T1"; i++) await Task.Delay(20);
        Assert.That(model.Detail?.Id, Is.EqualTo("BE-T1"));
        Assert.That(model.Toasts, Is.Empty);
    }

    [Test]
    public async Task Closing_a_worked_ticket_in_this_window_shows_in_the_feed_but_doesnt_toast()
    {
        await Elsewhere(db => db.SetWorking("AL-T1", "agent@example.com", "sess-1"));
        model.SelectedView = MainViewModel.QueueView;
        model.SelectedQueueRow = model.QueueRows.Single(r => r.Id == "AL-T1");
        for (var i = 0; i < 100 && model.Detail?.Id != "AL-T1"; i++) await Task.Delay(20);

        answers.Enqueue(new PromptAnswer(null, "abc123"));
        await model.CloseTicketAsync(TicketRules.Closed);

        Assert.That(model.ActivityRows.Any(r => r is { TicketId: "AL-T1", IsClose: true }), Is.True);
        Assert.That(model.Toasts, Is.Empty);
    }

    [Test]
    public async Task At_most_a_few_toasts_show_and_one_can_be_dismissed()
    {
        await Elsewhere(db =>
        {
            foreach (var id in new[] { "AL-T1", "AL-T2", "BE-T1" }) db.SetWorking(id, null, "sess-" + id);
        });
        await Elsewhere(db =>
        {
            foreach (var id in new[] { "AL-T1", "AL-T2", "BE-T1" }) db.Close(id, TicketRules.Closed, "x");
        });
        Assert.That(model.Toasts.Select(t => t.TicketId), Is.EqualTo(new[] { "AL-T1", "AL-T2", "BE-T1" }));
        Assert.That(model.Toasts[0].Title, Is.EqualTo("sess-AL-T1 closed AL-T1"));

        model.DismissToastCommand.Execute(model.Toasts[1]);
        Assert.That(model.Toasts.Select(t => t.TicketId), Is.EqualTo(new[] { "AL-T1", "BE-T1" }));
        Assert.That(MainViewModel.MaxToasts, Is.GreaterThanOrEqualTo(3));
    }
}
