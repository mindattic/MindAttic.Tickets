using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>M.TIX-T74: rows show their labels as chips, the label filter narrows the lists, and the detail pane adds and
/// removes labels.</summary>
public sealed class LabelTests
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
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-labels-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | fix | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.AddLabels("AL-T1", ["ui", "component:desktop"]);
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(answers));
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
    public void Rows_carry_their_labels_and_the_filter_offers_every_label_in_use()
    {
        var row = model.QueueRows.Single(r => r.Id == "AL-T1");
        Assert.That(row.TicketLabels, Is.EqualTo(new[] { "component:desktop", "ui" }));
        Assert.That(row.LabelsText, Is.EqualTo("component:desktop, ui"));
        Assert.That(row.SpokenName, Does.Contain("labels component:desktop, ui"));
        Assert.That(model.LabelChoices, Is.EqualTo(new[] { MainViewModel.AnyLabel, "component:desktop", "ui" }));
    }

    [Test]
    public void The_label_filter_and_search_narrow_the_lists_to_labelled_tickets()
    {
        model.LabelFilter = "ui";
        Assert.That(model.QueueRows.Select(r => r.Id), Is.EqualTo(new[] { "AL-T1" }));
        Assert.That(model.IsFiltered, Is.True);

        model.LabelFilter = MainViewModel.AnyLabel;
        Assert.That(model.QueueRows.Count, Is.EqualTo(2));

        model.Search = "component:desk";
        Assert.That(model.QueueRows.Select(r => r.Id), Is.EqualTo(new[] { "AL-T1" }));
    }

    [Test]
    public async Task The_detail_pane_adds_and_removes_labels_and_a_chip_filters_by_its_label()
    {
        Assert.That(await model.OpenTicketAsync("AL-T2"), Is.True);
        Assert.That(model.Detail!.TicketLabels, Is.Empty);

        answers.Enqueue(new PromptAnswer(null, "Backend, ui"));
        model.AddLabelCommand.Execute(null);
        await WaitFor(() => model.Detail?.TicketLabels.Count == 2);
        Assert.That(model.Detail!.TicketLabels, Is.EqualTo(new[] { "backend", "ui" }));
        Assert.That(model.LabelChoices, Does.Contain("backend"));

        model.RemoveLabelCommand.Execute("ui");
        await WaitFor(() => model.Detail?.TicketLabels.Count == 1);
        Assert.That(model.QueueRows.Single(r => r.Id == "AL-T2").TicketLabels, Is.EqualTo(new[] { "backend" }));

        answers.Enqueue(new PromptAnswer(null, "not;ok bad label!"));
        model.AddLabelCommand.Execute(null);
        Assert.That(model.Detail!.Error, Does.Contain("isn't a label"));

        model.FilterByLabelCommand.Execute("backend");
        Assert.That(model.LabelFilter, Is.EqualTo("backend"));
        Assert.That(model.QueueRows.Select(r => r.Id), Is.EqualTo(new[] { "AL-T2" }));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.That(condition(), Is.True, "timed out");
    }
}
