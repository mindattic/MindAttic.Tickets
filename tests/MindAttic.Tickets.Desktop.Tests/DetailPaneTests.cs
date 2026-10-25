using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The detail pane (M.TIX-T68): a selected ticket's fields, history and links both ways with their blocked state, linked IDs
/// that focus that ticket, and close / won't do / set ref / correct type / add and remove link, each failing inline.
/// </summary>
public sealed class DetailPaneTests
{
    private string root = "";
    private string dbPath = "";
    private MainViewModel model = null!;
    private readonly Answers dialogs = new();

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "tix-detail-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n" +
            "| AL-T3 | 2026-10-03 | chore | closed | alpha done | — |\n| AL-T4 | 2026-10-04 | feature | open | alpha four | — |\n");
        dbPath = TicketsDb.DefaultPath(root);
        using (var db = TicketsDb.Open(dbPath))
        {
            db.Import([new Repo("Alpha", dir)]);
            db.AddLink("AL-T1", "AL-T2", "requires"); // AL-T1 waits for open AL-T2
            db.AddLink("AL-T1", "AL-T3", "requires"); // ... and for AL-T3, which is done
            db.AddLink("AL-T4", "AL-T1", "requires"); // AL-T4 waits for AL-T1
        }

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

    private async Task<DetailViewModel> Open(string id)
    {
        Assert.That(await model.OpenTicketAsync(id), Is.True);
        return model.Detail!;
    }

    private Ticket InDb(string id)
    {
        using var db = TicketsDb.Open(dbPath);
        return db.Ticket(id)!.Value.Ticket;
    }

    [Test]
    public async Task Shows_the_tickets_fields_and_history()
    {
        var d = await Open("AL-T1");
        Assert.That((d.Id, d.Repo, d.Type, d.Status, d.Summary, d.Date), Is.EqualTo(("AL-T1", "Alpha", "feature", TicketRules.Open, "alpha one", "2026-10-01")));

        // A ticket filed through the store has its filing as the first history line.
        model.Dialogs = new Filing();
        await model.NewTicketAsync();
        for (var i = 0; i < 100 && model.Detail?.Id is null or "AL-T1"; i++) await Task.Delay(20);
        Assert.That(model.Detail!.History.Select(h => h.Text).First(), Does.StartWith("Filed"));
    }

    private sealed class Filing : IDialogs
    {
        public PromptAnswer? Ask(Prompt prompt) => null;
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => new("Alpha", "fix", "filed here", "");
    }

    [Test]
    public async Task Dependency_graph_draws_the_requires_chains_with_blockers_and_a_text_list()
    {
        var d = await Open("AL-T1");
        Assert.That(d.HasDependencies, Is.True);
        // Rows top to bottom: what it waits for, the ticket, what waits for it; the open prerequisite is the blocker.
        Assert.That(d.GraphRows.Select(r => (r.Label, string.Join(" ", r.Chips.Select(c => c.Id + (c.Blocking ? "!" : ""))))), Is.EqualTo(new[]
        {
            ("Waits for", "AL-T2! AL-T3"), ("This ticket", "AL-T1"), ("Needed by", "AL-T4!"),
        }));
        Assert.That(d.GraphRows[0].Chips[0].SpokenName, Is.EqualTo("AL-T2, open, blocking: alpha two: go to it"));
        Assert.That(d.DependencySummary, Is.EqualTo("Waits for 2, 1 of them open and blocking it. Needed by 1, 1 of them blocked by it."));
        Assert.That(d.DependencyText.Split(Environment.NewLine), Is.EqualTo(new[]
        {
            "AL-T1, open: alpha one", "Waits for (1 open blocker):", "  AL-T2, open, blocking: alpha two", "  AL-T3, closed: alpha done",
            "Needed by (1 blocked by it):", "  AL-T4, open, blocked by it: alpha four",
        }));

        // A ticket no requires link touches has no graph section.
        using (var db = TicketsDb.Open(dbPath)) db.Add("Alpha", "fix", TicketRules.Open, "alone", "");
        await model.RefreshAsync();
        Assert.That((await Open("AL-T5")).HasDependencies, Is.False);
    }

    [Test]
    public async Task Links_show_both_ways_with_what_blocks_and_what_is_blocked()
    {
        var d = await Open("AL-T1");
        Assert.That(d.IsBlocked, Is.True);
        Assert.That(d.WorkText, Does.Contain("AL-T2"));

        var t2 = d.Links.Single(l => l.Other == "AL-T2");
        Assert.That((t2.Phrase, t2.Blocking, t2.State), Is.EqualTo(("Waits for", true, "open: blocks this")));
        var t3 = d.Links.Single(l => l.Other == "AL-T3");
        Assert.That((t3.Phrase, t3.Blocking), Is.EqualTo(("Waits for", false)));
        var t4 = d.Links.Single(l => l.Other == "AL-T4");
        Assert.That((t4.Phrase, t4.Blocking, t4.State), Is.EqualTo(("Needed by", true, "blocked by this")));
        Assert.That(t4.SpokenName, Is.EqualTo("Needed by AL-T4, blocked by this"));
    }

    [Test]
    public async Task A_linked_id_focuses_that_ticket()
    {
        await Open("AL-T1");
        TicketRowViewModel? focused = null;
        model.FocusRow += r => focused = r;

        model.GoTo("AL-T2");
        for (var i = 0; i < 100 && model.Detail?.Id != "AL-T2"; i++) await Task.Delay(20);

        Assert.That(focused?.Id, Is.EqualTo("AL-T2"));
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T2"));
    }

    [Test]
    public async Task Close_sets_status_and_ref()
    {
        await Open("AL-T2");
        dialogs.Next = new PromptAnswer(null, "SomeTest; abc123");
        await model.CloseTicketAsync(TicketRules.Closed);

        Assert.That((InDb("AL-T2").Status, InDb("AL-T2").Ref), Is.EqualTo((TicketRules.Closed, "SomeTest; abc123")));
        // AL-T1 no longer waits on it.
        var d = await Open("AL-T1");
        Assert.That(d.IsBlocked, Is.False);
        Assert.That(d.Links.Single(l => l.Other == "AL-T2").Blocking, Is.False);
    }

    [Test]
    public async Task Wont_do_closes_with_the_reason_as_ref()
    {
        await Open("AL-T4");
        dialogs.Next = new PromptAnswer(null, "superseded");
        await model.CloseTicketAsync(TicketRules.WontDo);
        Assert.That((InDb("AL-T4").Status, InDb("AL-T4").Ref), Is.EqualTo((TicketRules.WontDo, "superseded")));
    }

    [Test]
    public async Task Set_ref_changes_only_the_ref_and_is_recorded()
    {
        await Open("AL-T1");
        dialogs.Next = new PromptAnswer(null, " AL-US-A1 ");
        await model.SetRefAsync();

        Assert.That((InDb("AL-T1").Status, InDb("AL-T1").Ref), Is.EqualTo((TicketRules.Open, "AL-US-A1")));
        Assert.That(model.Detail!.Ref, Is.EqualTo("AL-US-A1"));
        Assert.That(model.Detail.History.Select(h => h.Text), Has.Some.Contains("ref:").And.Some.Contains("AL-US-A1"));
    }

    [Test]
    public async Task An_empty_ref_is_refused_inline()
    {
        await Open("AL-T1");
        dialogs.Next = new PromptAnswer(null, "   ");
        await model.SetRefAsync();

        Assert.That(model.Detail!.Error, Does.Contain("can't be empty"));
        Assert.That(InDb("AL-T1").Ref, Is.EqualTo("—"));
    }

    [Test]
    public async Task Correct_type_records_the_reason_and_needs_one()
    {
        await Open("AL-T1");
        dialogs.Next = new PromptAnswer("chore", "");
        await model.CorrectTypeAsync();
        Assert.That(model.Detail!.Error, Does.Contain("reason"));
        Assert.That(InDb("AL-T1").Type, Is.EqualTo("feature"));

        dialogs.Next = new PromptAnswer("chore", "it was housekeeping");
        await model.CorrectTypeAsync();
        Assert.That(InDb("AL-T1").Type, Is.EqualTo("chore"));
        Assert.That(model.Detail!.Error, Is.Empty);
        Assert.That(model.Detail.History.Select(h => h.Text), Has.Some.Contains("it was housekeeping"));
    }

    [Test]
    public async Task Links_are_added_and_removed_and_a_cycle_fails_inline()
    {
        await Open("AL-T2");
        dialogs.Next = new PromptAnswer("related", "al-t4");
        await model.AddLinkAsync();
        Assert.That(model.Detail!.Links.Select(l => (l.Phrase, l.Other)), Does.Contain(("Related to", "AL-T4")));

        // AL-T1 already requires AL-T2, so AL-T2 requiring AL-T1 would be a cycle.
        dialogs.Next = new PromptAnswer("requires", "AL-T1");
        await model.AddLinkAsync();
        Assert.That(model.Detail!.Error, Does.Contain("cycle"));

        await model.RemoveLinkAsync(model.Detail.Links.Single(l => l.Other == "AL-T4"));
        Assert.That(model.Detail!.Links.Select(l => l.Other), Does.Not.Contain("AL-T4"));
        Assert.That(model.Detail.Error, Is.Empty);
    }

    private sealed class Answers : IDialogs
    {
        public PromptAnswer? Next { get; set; }
        public PromptAnswer? Ask(Prompt prompt) => Next;
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }
}
