using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The command palette (Ctrl+K, M.TIX-T81): a fuzzy search over every command that can run now, every ticket and every story;
/// the chosen entry jumps to its ticket, opens its story or runs its command, and every entry reads as one spoken sentence.
/// </summary>
public sealed class CommandPaletteTests
{
    private string root = "";
    private MainViewModel model = null!;
    private readonly Answers dialogs = new();

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "tix-palette-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Epic A — Palette\n- **AL-US-A1 🟡** As a Dev, I can jump anywhere from one box.\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | export the board as csv | — |\n| AL-T2 | 2026-10-02 | fix | open | crash on save | — |\n" +
            "| AL-T3 | 2026-10-03 | chore | closed | tidy the logs | abc123 |\n| AL-T21 | 2026-10-04 | fix | open | another one | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.AddLink("AL-T1", "AL-US-A1", "implements");
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

    private CommandPaletteViewModel Palette(string query) => new(model.PaletteItems()) { Query = query };

    [TestCase("apr", "Approve")]
    [TestCase("ctl", "Close the detail pane")]
    [TestCase("exp csv", "export the board as csv")]
    public void Letters_in_order_match_with_gaps(string query, string text) =>
        Assert.That(Fuzzy.Score(query, text), Is.Not.Null);

    [TestCase("xyz", "Approve")]
    [TestCase("eva", "Approve")]
    [TestCase("csv export", "export the board")]
    public void Letters_out_of_order_or_missing_do_not_match(string query, string text) =>
        Assert.That(Fuzzy.Score(query, text), Is.Null);

    [Test]
    public void A_prefix_scores_above_a_scattered_match() =>
        Assert.That(Fuzzy.Score("sync", "Sync the ticket database"), Is.GreaterThan(Fuzzy.Score("sync", "Show your new commits")!.Value));

    [Test]
    public void Lists_tickets_and_stories_with_the_commands()
    {
        var items = model.PaletteItems();

        Assert.That(items.Where(i => i.Kind == PaletteKind.Ticket).Select(i => i.Key), Is.EquivalentTo(new[] { "AL-T1", "AL-T2", "AL-T3", "AL-T21" }));
        Assert.That(items.Where(i => i.Kind == PaletteKind.Story).Select(i => i.Key), Does.Contain("AL-US-A1"));
        Assert.That(items.Where(i => i.Kind == PaletteKind.Command).Select(i => i.Title), Does.Contain("Refresh").And.Contain("New ticket"));
    }

    [Test]
    public void An_exact_id_ranks_first_even_when_a_longer_id_starts_with_it()
    {
        var palette = Palette("al-t2");

        Assert.That(palette.Selected?.Key, Is.EqualTo("AL-T2"));
        Assert.That(palette.Results.Select(r => r.Key), Does.Contain("AL-T21"));
    }

    [Test]
    public void Text_finds_a_ticket_by_its_summary() =>
        Assert.That(Palette("crash save").Selected?.Key, Is.EqualTo("AL-T2"));

    [Test]
    public void Nothing_typed_lists_only_commands_and_a_prefix_narrows_the_kind()
    {
        Assert.That(Palette("").Results.Select(r => r.Kind).Distinct(), Is.EqualTo(new[] { PaletteKind.Command }));
        Assert.That(Palette(">t").Results.All(r => r.Kind == PaletteKind.Command), Is.True);
        Assert.That(Palette("#a").Results.All(r => r.Kind != PaletteKind.Command), Is.True);
        Assert.That(Palette("#a").Results, Is.Not.Empty);
    }

    [Test]
    public void Commands_follow_the_selected_ticket_and_hide_what_cannot_run()
    {
        Assert.That(model.PaletteItems().Select(i => i.Title), Has.None.StartsWith("Approve"), "nothing selected yet");

        model.SelectedQueueRow = model.QueueRows.First(r => r.Id == "AL-T1");

        Assert.That(model.PaletteItems().Select(i => i.Title), Does.Contain("Approve AL-T1").And.Contain("Reject AL-T1"));
        Assert.That(model.PaletteItems().Select(i => i.Title), Has.None.StartsWith("Back to proposed"), "it isn't approved");
    }

    [Test]
    public async Task Running_a_command_does_what_it_says()
    {
        model.SelectedQueueRow = model.QueueRows.First(r => r.Id == "AL-T1");

        await Palette("approve al-t1").Selected!.Run();

        Assert.That(model.QueueRows.First(r => r.Id == "AL-T1").Approval, Is.EqualTo(TicketsDb.Approved));
        Assert.That(model.Announcement, Is.EqualTo("AL-T1 approved."));
    }

    [Test]
    public async Task Choosing_a_ticket_jumps_to_it_with_its_detail_open()
    {
        model.SelectedView = MainViewModel.BoardView;

        await Palette("tidy logs").Selected!.Run();

        Assert.That(model.SelectedView, Is.EqualTo(MainViewModel.TicketsView), "a closed ticket opens in Repos");
        Assert.That(model.SelectedTicketRow?.Id, Is.EqualTo("AL-T3"));
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T3"));
    }

    [Test]
    public async Task Choosing_a_story_shows_it_and_opens_the_linked_ticket_picked()
    {
        dialogs.Next = new PromptAnswer("AL-T1", "");

        await Palette("al-us-a1").Selected!.Run();

        Assert.That(dialogs.Asked?.Title, Is.EqualTo("AL-US-A1"));
        Assert.That(dialogs.Asked?.Message, Does.Contain("jump anywhere from one box").And.Contain("AL-T1 implements AL-US-A1"));
        Assert.That(dialogs.Asked?.Choices, Is.EqualTo(new[] { "AL-T1" }));
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T1"));
    }

    [Test]
    public void A_linked_story_id_opens_the_story_instead_of_failing()
    {
        model.GoTo("AL-US-A1");

        Assert.That(() => dialogs.Asked?.Title, Is.EqualTo("AL-US-A1").After(5000, 20));
    }

    [Test]
    public void Up_and_Down_move_the_selection_and_stop_at_the_ends()
    {
        var palette = Palette(">");
        Assert.That(palette.Selected, Is.SameAs(palette.Results[0]));

        palette.Move(-1);
        Assert.That(palette.Selected, Is.SameAs(palette.Results[0]));
        palette.Move(+1);
        Assert.That(palette.Selected, Is.SameAs(palette.Results[1]));
        palette.MoveToEnd(last: true);
        palette.Move(+1);
        Assert.That(palette.Selected, Is.SameAs(palette.Results[^1]));
    }

    [Test]
    public void Results_and_the_selection_are_said_in_words()
    {
        var palette = Palette("crash save");

        Assert.That(palette.Summary, Is.EqualTo("1 result."));
        Assert.That(palette.SelectedSpoken, Is.EqualTo("Ticket AL-T2: crash on save, Alpha, fix, open, proposed, 1 of 1"));
        Assert.That(Palette("zzzzqqq").Summary, Is.EqualTo("No matches."));
        Assert.That(Palette("zzzzqqq").SelectedSpoken, Is.Empty);
        Assert.That(Palette(">refresh").Selected?.SpokenName, Is.EqualTo("Command Refresh, shortcut F5"));
    }

    private sealed class Answers : IDialogs
    {
        public PromptAnswer? Next { get; set; }
        public Prompt? Asked { get; private set; }
        public PromptAnswer? Ask(Prompt prompt) { Asked = prompt; return Next; }
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }
}
