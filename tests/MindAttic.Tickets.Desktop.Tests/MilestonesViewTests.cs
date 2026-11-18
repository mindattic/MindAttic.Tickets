using System.IO;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// M.TIX-T336: the Milestones view lists every milestone that isn't dropped, in order with the reached ones last, each with
/// its state, repos, progress, acceptance checks and growth since activation; the selected one shows its checks, the epics
/// and tickets under it, the additions waiting for approval (approved or declined as this window's author) and its history;
/// Activate and Send to acceptance act on it; and a link to an active milestone says it waits for approval.
/// </summary>
public sealed class MilestonesViewTests
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
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-milestones-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Epic A — Core {#core}\n- **AL-US-A1 ⬜** As a user, I can start.\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | closed | build the start | StartTests |\n" +
            "| AL-T2 | 2026-10-02 | feature | open | build the stop | — |\n" +
            "| AL-T3 | 2026-10-03 | fix | open | fix the export | — |\n" +
            "| AL-T4 | 2026-10-04 | chore | open | tidy the build | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.AddLink("AL-T1", "AL-EPIC-A", TicketsDb.PartOf);
            db.AddLink("AL-T2", "AL-EPIC-A", TicketsDb.PartOf);
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), new Answers(answers))
        {
            ReadGit = _ => new RepoGitState("main", 0, 0, 0),
            SelectedView = MainViewModel.MilestonesView,
        };
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

    /// <summary>A change made through its own connection, as the CLI or an agent would, then read by the window.</summary>
    private async Task Agent(Action<TicketsDb> change)
    {
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) change(db);
        await model.RefreshAsync();
    }

    private T Read<T>(Func<TicketsDb, T> read)
    {
        using var db = TicketsDb.Open(TicketsDb.DefaultPath(root));
        return read(db);
    }

    private string[] Ids => [.. model.MilestoneRows.Select(r => r.Id)];

    private MilestoneRowViewModel Row(string id) => model.MilestoneRows.Single(r => r.Id == id);

    /// <summary>MS-1, active, with the epic (AL-T1 closed, AL-T2 open) on it since before it was activated.</summary>
    private Task ActiveMilestone() => Agent(db =>
    {
        var id = db.AddMilestone("Core ships", "The core can start and stop.", ["Alpha"]).Id;
        db.AddToMilestone(id, ["AL-EPIC-A"]);
        db.ActivateMilestone(id);
    });

    [Test]
    public void With_no_milestones_the_view_says_so_and_how_to_create_one()
    {
        Assert.That(model.Views, Does.Contain(MainViewModel.MilestonesView));
        Assert.That(model.IsMilestonesView, Is.True);
        Assert.That(model.MilestoneRows, Is.Empty);
        Assert.That(model.MilestonesEmptyText, Is.EqualTo("No milestones yet. Create one with: tickets milestone new --name ... --repo ..."));
        Assert.That(model.StatusMessage, Is.EqualTo(MainViewModel.NoMilestonesText));
        Assert.That(model.ShowsMilestonePane, Is.False);
        Assert.That(model.ShowsTicketFilters, Is.False, "the ticket filters don't apply to milestones");
    }

    [Test]
    public async Task Lists_milestones_in_order_with_the_reached_ones_last_and_the_dropped_ones_left_out()
    {
        await Agent(db =>
        {
            db.AddMilestone("First", null, ["Alpha"]);
            var reached = db.AddMilestone("Second", null, ["Alpha"]).Id;
            db.ActivateMilestone(reached);
            db.AcceptMilestone(reached, reached: true, by: "owner");
            db.DropMilestone(db.AddMilestone("Third", null, ["Alpha"]).Id, "not needed");
            db.ActivateMilestone(db.AddMilestone("Fourth", null, ["Alpha"]).Id);
        });

        Assert.That(Ids, Is.EqualTo(new[] { "MS-1", "MS-4", "MS-2" }), "milestone order, the reached one last, the dropped one not listed");
        Assert.That(Row("MS-1").StateText, Is.EqualTo("○ planned"));
        Assert.That(Row("MS-4").StateText, Is.EqualTo("● active"));
        Assert.That(Row("MS-2").StateText, Is.EqualTo("✓ reached"));
        Assert.That(Row("MS-2").IsReached, Is.True);
        Assert.That(Row("MS-2").DatesText, Does.Contain("Reached").And.Contain("by owner"));
        Assert.That(model.StatusMessage, Does.StartWith("Milestones: 3 of 3 shown, 1 active"));

        model.Search = "fourth";
        Assert.That(Ids, Is.EqualTo(new[] { "MS-4" }), "search narrows the list by name");
        model.Search = "nothing like it";
        Assert.That(model.MilestoneRows, Is.Empty);
        Assert.That(model.MilestonesEmptyText, Is.EqualTo("No milestones match."));
    }

    [Test]
    public async Task A_milestone_shows_its_repos_progress_checks_and_the_tickets_under_it_grouped_by_epic()
    {
        await Agent(db =>
        {
            var id = db.AddMilestone("Core ships", "The core can start and stop.", ["Alpha"]).Id;
            db.AddToMilestone(id, ["AL-EPIC-A", "AL-T3"]);
            db.AddMilestoneCheck(id, MilestoneCheckKinds.Build, "Alpha builds on main", ["Alpha"]);
            db.AddMilestoneCheck(id, MilestoneCheckKinds.Signoff, "The owner signs off");
            db.SetMilestoneCheckResult(id, 1, MilestoneCheckResults.Pass, "commit abc1234");
        });

        var row = Row("MS-1");
        Assert.That((row.Name, row.ReposText, row.Goal), Is.EqualTo(("Core ships", "Alpha", "The core can start and stop.")));
        Assert.That((row.Done, row.Total), Is.EqualTo((1, 3)));
        Assert.That(row.ProgressText, Is.EqualTo("1 of 3 tickets done"));
        Assert.That(row.DoneFraction, Is.EqualTo(1d / 3).Within(1e-9));
        Assert.That(row.ChecksText, Is.EqualTo("Checks: 1 of 2 pass"));
        Assert.That(row.GrowthText, Is.EqualTo(""), "never activated: nothing about growth");
        Assert.That(row.SpokenName, Does.Contain("MS-1").And.Contain("planned").And.Contain("1 of 3 tickets done"));

        Assert.That(row.Groups.Select(g => g.Heading), Is.EqualTo(new[] { "Linked to the milestone itself", "AL-EPIC-A — Core" }));
        Assert.That(row.Groups[0].Members.Select(m => m.Id), Is.EqualTo(new[] { "AL-T3" }));
        Assert.That(row.Groups[1].Members.Select(m => m.Id), Is.EqualTo(new[] { "AL-T1", "AL-T2", "AL-US-A1" }), "its tickets, then the story the epic also holds");
        Assert.That(row.Groups[1].ProgressText, Is.EqualTo("1 of 2 done"));
        Assert.That(row.Groups[1].Members[0].StatusText, Is.EqualTo("✓ closed"), "each ticket's status is a word, not colour alone");

        Assert.That(row.Checks.Select(c => c.ResultText), Is.EqualTo(new[] { "✓ pass", "? unknown" }), "a result is always its word");
        Assert.That(row.Checks[0].Title, Is.EqualTo("#1 build"));
        Assert.That(row.Checks[0].LooksAt, Is.EqualTo("In Alpha"));
        Assert.That(row.Checks[0].CheckedText, Does.StartWith("Checked ").And.EndWith(": commit abc1234"));
        Assert.That(row.Checks[1].CheckedText, Is.EqualTo("Not checked yet."));

        Assert.That(row.History[^1].Text, Is.EqualTo("Created."), "most recent first: its creation is last");
        Assert.That(row.History[0].Text, Does.StartWith("Check result:"));
    }

    [Test]
    public void Growth_since_activation_is_said_in_words_and_not_at_all_when_it_has_not_grown()
    {
        static MilestoneGrowth Growth(int added, int pending) => new("MS-1", "2026-11-01T00:00:00Z", 2, added, 0, 2 + added, pending, pending);

        Assert.That(MilestoneWords.Growth(null), Is.EqualTo(""), "never activated");
        Assert.That(MilestoneWords.Growth(Growth(0, 0)), Is.EqualTo(""), "not grown");
        Assert.That(MilestoneWords.Growth(Growth(3, 2)), Is.EqualTo("Grown by 3 tickets since activation, 2 waiting for approval"));
        Assert.That(MilestoneWords.Growth(Growth(1, 0)), Is.EqualTo("Grown by 1 ticket since activation"));
        Assert.That(MilestoneWords.Growth(Growth(0, 1)), Is.EqualTo("Not grown since activation, 1 waiting for approval"));
    }

    [Test]
    public async Task An_active_milestones_row_says_how_much_it_grew_and_what_waits()
    {
        await ActiveMilestone();
        Assert.That(Row("MS-1").GrowthText, Is.EqualTo(""), "what was on it at activation isn't growth");

        await Agent(db =>
        {
            db.AddToMilestone("MS-1", ["AL-T3"], approvedBy: "owner");
            db.AddToMilestone("MS-1", ["AL-T4"], by: "an agent");
        });

        var row = Row("MS-1");
        Assert.That(row.GrowthText, Is.EqualTo("Grown by 1 ticket since activation, 1 waiting for approval"));
        Assert.That(row.Pending.Select(p => p.Item.Id), Is.EqualTo(new[] { "AL-T4" }));
        Assert.That(row.Pending[0].ProposedText, Does.StartWith("Proposed ").And.EndWith(" by an agent"));
        Assert.That(row.Total, Is.EqualTo(3), "a pending addition isn't a member yet");
        Assert.That(model.StatusMessage, Does.EndWith("1 addition waiting for approval."));
    }

    [Test]
    public async Task Approving_a_pending_addition_makes_it_a_member_as_this_windows_author_and_keeps_the_selection()
    {
        await ActiveMilestone();
        await Agent(db => db.AddToMilestone("MS-1", ["AL-T3"], by: "an agent"));
        var row = Row("MS-1");
        model.SelectedMilestoneRow = row;
        Assert.That(model.ShowsMilestonePane, Is.True);
        var pending = row.Pending.Single();
        Assert.That(model.ApproveAdditionCommand.CanExecute(pending), Is.True);

        await model.ApproveAdditionAsync(pending);

        Assert.That(model.SelectedMilestoneRow, Is.SameAs(row), "the row is kept across the refresh, so the selection is");
        Assert.That(row.Pending, Is.Empty);
        Assert.That(row.Groups.SelectMany(g => g.Members).Select(m => m.Id), Does.Contain("AL-T3"));
        Assert.That(row.Error, Is.EqualTo(""));
        Assert.That(model.StatusMessage, Is.EqualTo($"AL-T3 is on MS-1, approved by {TicketStore.Author}."));
        Assert.That(Read(db => db.MilestoneHistory("MS-1")).Last(e => e.Field == "approved").New, Is.EqualTo($"AL-T3 by {TicketStore.Author}"));
    }

    [Test]
    public async Task Declining_a_pending_addition_asks_for_the_reason_and_records_who_declined()
    {
        await ActiveMilestone();
        await Agent(db => db.AddToMilestone("MS-1", ["AL-T3"], by: "an agent"));
        var row = Row("MS-1");
        model.SelectedMilestoneRow = row;

        answers.Enqueue(null);
        await model.DeclineAdditionAsync(row.Pending.Single());
        Assert.That(row.Pending, Has.Count.EqualTo(1), "cancelled: it still waits");

        answers.Enqueue(new PromptAnswer(null, "out of scope"));
        await model.DeclineAdditionAsync(row.Pending.Single());

        Assert.That(model.SelectedMilestoneRow, Is.SameAs(row));
        Assert.That(row.Pending, Is.Empty);
        Assert.That(row.Groups.SelectMany(g => g.Members).Select(m => m.Id), Does.Not.Contain("AL-T3"), "it never joined");
        Assert.That(model.StatusMessage, Is.EqualTo($"AL-T3 was declined for MS-1 by {TicketStore.Author}."));
        Assert.That(Read(db => db.MilestoneHistory("MS-1")).Last(e => e.Field == "declined").New, Is.EqualTo($"AL-T3 by {TicketStore.Author}: out of scope"));
        Assert.That(row.History[0].Text, Does.StartWith("Declined: AL-T3"));
    }

    [Test]
    public async Task Activate_then_send_to_acceptance_move_the_selected_milestone_on()
    {
        await Agent(db => db.AddMilestone("Core ships", null, ["Alpha"]));
        var row = Row("MS-1");
        model.SelectedMilestoneRow = row;
        Assert.That(model.ActivateMilestoneCommand.CanExecute(null), Is.True);
        Assert.That(model.SendMilestoneToAcceptanceCommand.CanExecute(null), Is.False, "only an active milestone goes to acceptance");

        await model.ActivateMilestoneAsync();

        Assert.That(row.State, Is.EqualTo(MilestoneStates.Active));
        Assert.That(model.StatusMessage, Is.EqualTo("MS-1 is active."));
        Assert.That(model.ActivateMilestoneCommand.CanExecute(null), Is.False);
        Assert.That(model.SendMilestoneToAcceptanceCommand.CanExecute(null), Is.True);

        await model.SendMilestoneToAcceptanceAsync();

        Assert.That(row.State, Is.EqualTo(MilestoneStates.Accepting));
        Assert.That(row.StateText, Is.EqualTo("◐ accepting"));
        Assert.That(model.SelectedMilestoneRow, Is.SameAs(row));
        Assert.That(row.CliHint, Does.Contain("tickets milestone accept MS-1 --reached"), "reaching it stays with the CLI");
    }

    [Test]
    public async Task An_action_the_database_refuses_shows_its_message_on_the_milestone()
    {
        await Agent(db => db.AddMilestone("Core ships", null, ["Alpha"]));
        var row = Row("MS-1");
        model.SelectedMilestoneRow = row;
        // Dropped from the CLI since the window last read it: the row still says planned.
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root))) db.DropMilestone("MS-1");

        await model.ActivateMilestoneAsync();

        Assert.That(row.Error, Does.Contain("MS-1 is dropped"));
        Assert.That(model.StatusMessage, Is.EqualTo(row.Error));
        Assert.That(model.Busy, Is.False);
    }

    [Test]
    public async Task A_refresh_after_an_agents_change_updates_the_selected_milestone_in_place()
    {
        await ActiveMilestone();
        var row = Row("MS-1");
        model.SelectedMilestoneRow = row;
        Assert.That(row.ProgressText, Is.EqualTo("1 of 2 tickets done"));

        await Agent(db =>
        {
            db.Close("AL-T2", TicketRules.Closed, "StopTests");
            db.AddMilestoneCheck("MS-1", MilestoneCheckKinds.Signoff, "The owner signs off");
        });

        Assert.That(Row("MS-1"), Is.SameAs(row));
        Assert.That(model.SelectedMilestoneRow, Is.SameAs(row));
        Assert.That(row.ProgressText, Is.EqualTo("2 of 2 tickets done"));
        Assert.That(row.ChecksText, Is.EqualTo("Checks: 0 of 1 pass"));
    }

    [Test]
    public async Task A_ticket_under_a_milestone_opens_in_Repos_with_its_detail()
    {
        await ActiveMilestone();
        model.SelectedMilestoneRow = Row("MS-1");
        Assert.That(model.ShowsDetailPane, Is.False);

        await model.OpenTicketAsync("AL-T2");

        Assert.That(model.IsMilestonesView, Is.False, "Milestones lists no ticket rows: the ticket opens where it's listed");
        Assert.That(model.Detail?.Id, Is.EqualTo("AL-T2"));
        Assert.That(model.ShowsDetailPane, Is.True);
        Assert.That(model.ShowsMilestonePane, Is.False);
    }

    [Test]
    public async Task Linking_a_ticket_to_an_active_milestone_says_it_waits_for_the_owners_approval()
    {
        await ActiveMilestone();
        Assert.That(await model.OpenTicketAsync("AL-T3"), Is.True);

        answers.Enqueue(new PromptAnswer(TicketsDb.PartOf, "ms-1"));
        await model.AddLinkAsync();

        Assert.That(model.StatusMessage, Does.StartWith("AL-T3 is waiting for the owner's approval before it joins MS-1"));
        Assert.That(model.CanUndo, Is.False, "nothing was linked, so there's nothing to undo");
        var pending = Read(db => db.MilestonePending("MS-1")).Single();
        Assert.That((pending.Item, pending.By), Is.EqualTo(("AL-T3", TicketStore.Author)));
        Assert.That(Read(db => db.MilestoneMembers("MS-1")).Select(m => m.Id), Does.Not.Contain("AL-T3"));
    }

    [Test]
    public async Task Linking_a_ticket_to_a_planned_milestone_links_it_at_once()
    {
        await Agent(db => db.AddMilestone("Core ships", null, ["Alpha"]));
        Assert.That(await model.OpenTicketAsync("AL-T3"), Is.True);

        answers.Enqueue(new PromptAnswer(TicketsDb.PartOf, "MS-1"));
        await model.AddLinkAsync();

        Assert.That(model.StatusMessage, Is.EqualTo("AL-T3 part-of MS-1."));
        Assert.That(Read(db => db.MilestoneMembers("MS-1")).Select(m => m.Id), Does.Contain("AL-T3"));
    }
}
