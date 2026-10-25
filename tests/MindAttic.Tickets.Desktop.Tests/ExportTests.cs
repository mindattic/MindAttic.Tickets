using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>M.TIX-T87: Export saves the tickets the current view shows, filters applied, as CSV or markdown.</summary>
public sealed class ExportTests
{
    private string root = "";
    private MainViewModel model = null!;
    private readonly SaveTo dialogs = new();

    private sealed class SaveTo : IDialogs
    {
        public string? Path { get; set; }
        public string? Suggested { get; private set; }
        public PromptAnswer? Ask(Prompt prompt) => null;
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
        public string? SaveFile(string title, string fileName, string filter) { Suggested = fileName; return Path; }
    }

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-export-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | fix | open | alpha, one | — |\n| AL-T2 | 2026-10-02 | feature | open | alpha two | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.AddLabels("AL-T1", ["ui"]);
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), dialogs) { SelectedView = MainViewModel.QueueView };
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

    [Test]
    public async Task Export_writes_the_shown_tickets_as_csv_or_markdown_by_extension()
    {
        model.TypeFilter = "fix";
        dialogs.Path = Path.Combine(root, "out.csv");
        Assert.That(model.ExportCommand.CanExecute(null), Is.True);
        await model.ExportAsync();
        Assert.That(dialogs.Suggested, Does.StartWith("tickets-queue-").And.EndWith(".csv"));
        var csv = File.ReadAllText(dialogs.Path);
        Assert.That(csv, Does.StartWith("Repo,ID,Date,Type,Status,Approval,Summary,Ref,Labels\r\n"));
        Assert.That(csv, Does.Contain("Alpha,AL-T1,2026-10-01,fix,open,proposed,\"alpha, one\",—,ui\r\n").And.Not.Contain("AL-T2"));
        Assert.That(model.StatusMessage, Does.Contain("Exported 1 ticket(s)"));

        model.TypeFilter = MainViewModel.AnyType;
        dialogs.Path = Path.Combine(root, "out.md");
        await model.ExportAsync();
        var md = File.ReadAllText(dialogs.Path);
        Assert.That(md, Does.StartWith("| Repo | ID |").And.Contain("| AL-T1 |").And.Contain("| AL-T2 |"));
    }

    [Test]
    public async Task Cancelling_writes_nothing_and_audit_has_nothing_to_export()
    {
        dialogs.Path = null;
        await model.ExportAsync();
        Assert.That(Directory.GetFiles(root), Is.Empty);

        model.SelectedView = MainViewModel.AuditView;
        Assert.That(model.ExportCommand.CanExecute(null), Is.False);
    }
}
