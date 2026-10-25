using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// Live refresh and Sync (M.TIX-T70): the database file is watched (settled, with a poll behind it), a refresh syncs the
/// lists by ticket ID so the selected row (and with it focus) survives an agent's change, and Sync runs
/// <c>tickets db sync</c> and shows its result.
/// </summary>
public sealed class LiveRefreshTests
{
    private string root = "";
    private MainViewModel model = null!;

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-live-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) db.Import([new Repo("Alpha", alpha)]);

        model = Model(new TicketStore(root));
        await model.RefreshAsync();
    }

    private MainViewModel Model(TicketStore store) =>
        new(store, new DesktopSettings()) { ReadGit = _ => new RepoGitState("main", 0, 0, 0) };

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

    /// <summary>An agent's change, made through its own connection as the CLI would.</summary>
    private void Agent(Action<TicketsDb> change)
    {
        using var db = TicketsDb.Open(TicketsDb.DefaultPath(root));
        change(db);
    }

    [Test]
    public async Task An_agents_change_updates_the_rows_in_place_and_keeps_the_selection()
    {
        model.SelectedView = MainViewModel.QueueView;
        var selected = model.QueueRows.Single(r => r.Id == "AL-T1");
        model.SelectedQueueRow = selected;
        var other = model.QueueRows.Single(r => r.Id == "AL-T2");

        Agent(db =>
        {
            db.Approve("AL-T1");
            db.Add("Alpha", "fix", TicketRules.Open, "filed by an agent", "—");
        });
        await model.RefreshAsync();

        Assert.That(model.QueueRows.Select(r => r.Summary), Does.Contain("filed by an agent"), "the new ticket appears");
        Assert.That(model.QueueRows.Single(r => r.Id == "AL-T1"), Is.SameAs(selected), "the row is kept, not re-created, so focus stays on it");
        Assert.That(model.QueueRows.Single(r => r.Id == "AL-T2"), Is.SameAs(other));
        Assert.That(model.SelectedQueueRow, Is.SameAs(selected));
        Assert.That(selected.Approval, Is.EqualTo(TicketsDb.Approved), "the kept row shows the change");
    }

    [Test]
    public void Keyed_sync_reuses_rows_by_key_reorders_adds_and_drops()
    {
        var a = new Row("a", 1); var b = new Row("b", 2); var c = new Row("c", 3);
        var rows = new ObservableCollection<Row> { a, b, c };

        rows.SyncBy([("c", 30), ("a", 10), ("d", 40)], s => s.Item1, r => r.Key, s => new Row(s.Item1, s.Item2), (r, s) => r.Value = s.Item2);

        Assert.That(rows.Select(r => r.Key), Is.EqualTo(new[] { "c", "a", "d" }));
        Assert.That(rows[0], Is.SameAs(c));
        Assert.That(rows[1], Is.SameAs(a));
        Assert.That(rows.Select(r => r.Value), Is.EqualTo(new[] { 30, 10, 40 }));
    }

    private sealed class Row(string key, int value)
    {
        public string Key { get; } = key;
        public int Value { get; set; } = value;
    }

    [Test]
    public async Task A_burst_of_writes_to_the_database_refreshes_once_it_settles()
    {
        var path = TicketsDb.DefaultPath(root);
        var fired = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = new DatabaseWatch(path, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan,
            () => { Interlocked.Increment(ref fired); first.TrySetResult(); });
        Assert.That(watch.Watching, Is.True);

        for (var i = 0; i < 3; i++) Agent(db => db.Approve("AL-T1"));
        Agent(db => db.Unapprove("AL-T1"));

        Assert.That(await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(first.Task), "the change was seen");
        await Task.Delay(800);
        Assert.That(fired, Is.EqualTo(1), "several writes, one refresh");
    }

    [Test]
    public async Task The_poll_refreshes_with_no_change_and_starts_watching_a_folder_that_appears()
    {
        var path = Path.Combine(root, "later", "data", "Tickets.db");
        var polls = 0;
        using var watch = new DatabaseWatch(path, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), () => Interlocked.Increment(ref polls));
        Assert.That(watch.Watching, Is.False, "no folder to watch yet");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((!watch.Watching || Volatile.Read(ref polls) < 2) && DateTime.UtcNow < until) await Task.Delay(50);

        Assert.That(polls, Is.GreaterThanOrEqualTo(2), "the poll runs without a change");
        Assert.That(watch.Watching, Is.True, "the poll starts the watcher once the folder exists");
    }

    [Test]
    public async Task Sync_runs_db_sync_and_shows_its_result_with_the_lists_current()
    {
        string? ran = null;
        var synced = Model(new TicketStore(root) { Sync = s => { ran = s.Root; return Task.FromResult((0, "Rendered 1 repo.\nPushed abc1234 to origin/main.\n")); } });
        await synced.RefreshAsync();
        Agent(db => db.Add("Alpha", "fix", TicketRules.Open, "filed before the sync", "—"));

        await synced.SyncAsync();

        Assert.That(ran, Is.EqualTo(Path.GetFullPath(root)), "the window's workspace is synced");
        Assert.That(synced.StatusMessage, Is.EqualTo("Synced. Pushed abc1234 to origin/main."));
        Assert.That(synced.Announcement, Is.EqualTo(synced.StatusMessage), "a screen reader hears the result");
        Assert.That(synced.TicketRows.Select(r => r.Summary), Does.Contain("filed before the sync"));
        Assert.That(synced.SyncCommand.CanExecute(null), Is.True, "not left busy");
    }

    [Test]
    public async Task A_failed_sync_says_so_with_its_exit_code()
    {
        var failing = Model(new TicketStore(root) { Sync = _ => Task.FromResult((1, "git push rejected: main moved")) });
        await failing.RefreshAsync();

        await failing.SyncAsync();

        Assert.That(failing.StatusMessage, Is.EqualTo("Sync failed (1): git push rejected: main moved"));
    }

    [Test]
    public async Task A_sync_that_cant_start_says_why()
    {
        var missing = Model(new TicketStore(root) { Sync = _ => throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.") });
        await missing.RefreshAsync();

        await missing.SyncAsync();

        Assert.That(missing.StatusMessage, Does.StartWith("Couldn't run 'tickets db sync'"));
        Assert.That(missing.SyncCommand.CanExecute(null), Is.True);
    }
}
