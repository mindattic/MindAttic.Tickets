using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The new-ticket dialog (Ctrl+N, M.TIX-T69) through the window's view model: it offers every repo, starts on the last
/// repo and type used (remembered across sessions in the per-user settings), and files the repo's next free ID with
/// the summary and ref given, then selects it. Cancelling files nothing and remembers nothing.
/// </summary>
public sealed class NewTicketTests
{
    private string root = "";
    private string dbPath = "";
    private string settingsPath = "";
    private readonly Answers dialogs = new();

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-newticket-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL", "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n");
        var beta = MakeRepo("Beta", "BE", "| BE-T1 | 2026-10-04 | feature | open | beta one | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath)) db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);
        settingsPath = Path.Combine(root, "desktop.json");
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

    private async Task<MainViewModel> Open()
    {
        var model = new MainViewModel(new TicketStore(root), DesktopSettings.Load(settingsPath), dialogs);
        await model.RefreshAsync();
        return model;
    }

    private Ticket? InDb(string id)
    {
        using var db = TicketsDb.Open(dbPath);
        return db.Ticket(id)?.Ticket;
    }

    [Test]
    public async Task Files_the_repos_next_ticket_with_summary_and_ref_and_selects_it()
    {
        var model = await Open();
        dialogs.Next = new NewTicket("Beta", "fix", "beta crashes on save", "BE-US-1");

        Assert.That(model.NewTicketCommand.CanExecute(null), Is.True);
        await model.NewTicketAsync();

        Assert.That(dialogs.Repos, Does.Contain("Alpha").And.Contain("Beta"));
        var t = InDb("BE-T2");
        Assert.That(t, Is.Not.Null);
        Assert.That((t!.Type, t.Status, t.Summary, t.Ref), Is.EqualTo(("fix", TicketRules.Open, "beta crashes on save", "BE-US-1")));
        Assert.That(model.StatusMessage, Is.EqualTo("Filed BE-T2."));
        Assert.That((model.IsQueueView ? model.SelectedQueueRow : model.SelectedTicketRow)?.Id, Is.EqualTo("BE-T2"));
    }

    [Test]
    public async Task Starts_on_the_last_repo_and_type_used_even_after_a_restart()
    {
        var first = await Open();
        Assert.That((dialogs.OfferedRepo, dialogs.OfferedType), Is.EqualTo(((string?)null, "feature")), "nothing remembered yet");
        dialogs.Next = new NewTicket("Beta", "chore", "tidy beta", "");
        await first.NewTicketAsync();
        Assert.That((dialogs.OfferedRepo, dialogs.OfferedType), Is.EqualTo(((string?)null, "feature")));

        dialogs.Next = null;
        await first.NewTicketAsync();
        Assert.That((dialogs.OfferedRepo, dialogs.OfferedType), Is.EqualTo(("Beta", "chore")));

        // A fresh window (new settings load) still remembers them.
        var second = await Open();
        await second.NewTicketAsync();
        Assert.That((dialogs.OfferedRepo, dialogs.OfferedType), Is.EqualTo(("Beta", "chore")));
    }

    [Test]
    public async Task A_repo_filter_picks_the_repo_over_the_remembered_one()
    {
        var model = await Open();
        dialogs.Next = new NewTicket("Beta", "docs", "beta docs", "");
        await model.NewTicketAsync();

        model.RepoFilter = "Alpha";
        dialogs.Next = null;
        await model.NewTicketAsync();
        Assert.That((dialogs.OfferedRepo, dialogs.OfferedType), Is.EqualTo(("Alpha", "docs")));
    }

    [Test]
    public async Task Cancelling_files_nothing_and_remembers_nothing()
    {
        var model = await Open();
        dialogs.Next = null;
        await model.NewTicketAsync();

        Assert.That(InDb("AL-T2"), Is.Null);
        Assert.That(InDb("BE-T2"), Is.Null);
        Assert.That(File.Exists(settingsPath), Is.False);
    }

    private sealed class Answers : IDialogs
    {
        public NewTicket? Next { get; set; }
        public IReadOnlyList<string> Repos { get; private set; } = [];
        public string? OfferedRepo { get; private set; }
        public string? OfferedType { get; private set; }

        public PromptAnswer? Ask(Prompt prompt) => null;

        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type)
        {
            (Repos, OfferedRepo, OfferedType) = (repos, repo, type);
            return Next;
        }
    }
}
