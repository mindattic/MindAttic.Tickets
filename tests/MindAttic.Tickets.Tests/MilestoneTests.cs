using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>Milestones (M.TIX-T335): their rows, repos, states, members (part-of links) and acceptance checks.</summary>
public sealed class MilestoneTests
{
    private string root = "";
    private TicketsDb db = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-milestones-" + Guid.NewGuid().ToString("N"));
        db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        db.Import([
            Repo("Alpha", "AL", "| AL-T1 | 2026-10-01 | feature | open | the epic | — |\n| AL-T2 | 2026-10-02 | fix | closed | two | — |\n" +
                                "| AL-T3 | 2026-10-03 | fix | open | three | — |\n| AL-T4 | 2026-10-04 | chore | wont-do | four | — |\n"),
            // A repo whose code is MS: its tickets are MS-T<n>, never a milestone's MS-<n>.
            Repo("Beta", "MS", "| MS-T1 | 2026-10-01 | feature | open | beta one | — |\n"),
        ]);
    }

    private Repo Repo(string name, string code, string rows)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            $"---\ncodex: 1\nproject: {name}\ncode: {code}\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# {name} — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" + rows);
        return new Repo(name, dir);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [TestCase("MS-1", true)]
    [TestCase(" ms-12 ", true)]
    [TestCase("MS-0", false)]
    [TestCase("MS-T1", false)]
    [TestCase("MS-US-A1", false)]
    [TestCase("MS-EPIC-A", false)]
    [TestCase("M.TIX-T335", false)]
    [TestCase("", false)]
    public void A_milestone_id_is_MS_n_and_never_a_ticket_story_or_epic_id(string id, bool expected) =>
        Assert.That(TicketsDb.IsMilestoneId(id), Is.EqualTo(expected));

    [Test]
    public void A_milestone_is_added_planned_with_the_next_id_its_repos_and_order()
    {
        var first = db.AddMilestone(" Ship  alpha ", "Alpha works end to end", ["beta", "Alpha", "ALPHA"]);
        var second = db.AddMilestone("Second", null, ["Beta"], order: 0);

        Assert.That(first.Id, Is.EqualTo("MS-1"));
        Assert.That(first.Name, Is.EqualTo("Ship alpha"));
        Assert.That(first.Repos, Is.EqualTo(new[] { "Alpha", "Beta" }), "by the names the database keys them by, once each");
        Assert.That((first.State, first.Order, first.Activated, first.Reached), Is.EqualTo((MilestoneStates.Planned, 1L, (string?)null, (string?)null)));
        Assert.That(second.Id, Is.EqualTo("MS-2"));
        Assert.That(db.Milestones().Select(m => m.Id), Is.EqualTo(new[] { "MS-2", "MS-1" }), "lower order first");
        Assert.That(db.Milestone("ms-1")!.Id, Is.EqualTo("MS-1"));
        Assert.That(db.Milestone("SHIP ALPHA")!.Id, Is.EqualTo("MS-1"), "found by its name too");
        Assert.That(db.Milestone("MS-T1"), Is.Null, "a ticket's ID is no milestone");
        Assert.That(db.MilestoneHistory("MS-1").Select(e => (e.Field, e.New)), Is.EqualTo(new[] { ("created", (string?)"planned") }));
    }

    [Test]
    public void A_milestone_needs_a_name_of_its_own_and_a_known_repo()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha"]);

        Assert.That(() => db.AddMilestone(" ", null, ["Alpha"]), Throws.ArgumentException);
        Assert.That(() => db.AddMilestone("MS-7", null, ["Alpha"]), Throws.ArgumentException);
        Assert.That(() => db.AddMilestone("ship ALPHA", null, ["Alpha"]), Throws.InvalidOperationException);
        Assert.That(() => db.AddMilestone("No repos", null, []), Throws.ArgumentException);
        Assert.That(() => db.AddMilestone("Bad repo", null, ["Nowhere"]), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(db.Milestones(), Has.Count.EqualTo(1));
    }

    [Test]
    public void A_milestone_moves_planned_active_accepting_reached_with_history()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha"]);

        Assert.That(() => db.AcceptMilestone("MS-1"), Throws.InvalidOperationException, "only an active one moves into acceptance");
        var active = db.ActivateMilestone("MS-1");
        Assert.That(active.State, Is.EqualTo(MilestoneStates.Active));
        Assert.That(active.Activated, Is.Not.Null);
        Assert.That(() => db.ActivateMilestone("MS-1"), Throws.InvalidOperationException);
        Assert.That(db.AcceptMilestone("MS-1").State, Is.EqualTo(MilestoneStates.Accepting));
        Assert.That(db.ActivateMilestone("MS-1").Activated, Is.EqualTo(active.Activated), "sent back, its first activation is kept");
        Assert.That(() => db.AcceptMilestone("MS-1", reached: true), Throws.ArgumentException, "reaching says who");

        var reached = db.AcceptMilestone("MS-1", reached: true, by: "owner");
        Assert.That((reached.State, reached.ReachedBy), Is.EqualTo((MilestoneStates.Reached, "owner")));
        Assert.That(reached.Reached, Is.Not.Null);
        Assert.That(() => db.DropMilestone("MS-1"), Throws.InvalidOperationException, "a reached milestone isn't dropped");
        Assert.That(db.MilestoneHistory("MS-1").Where(e => e.Field == "state").Select(e => (e.Old, e.New)), Is.EqualTo(new (string?, string?)[]
        {
            ("planned", "active"), ("active", "accepting"), ("accepting", "active"), ("active", "reached"),
        }));
    }

    [Test]
    public void Reaching_is_refused_while_a_check_is_fail_or_unknown_unless_forced()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha"]);
        db.ActivateMilestone("MS-1");
        var build = db.AddMilestoneCheck("MS-1", "build", "Builds on main", ["alpha"]);
        db.AddMilestoneCheck("MS-1", "signoff", "Owner signs off");

        Assert.That((build.Number, build.Result, build.Checked), Is.EqualTo((1L, MilestoneCheckResults.Unknown, (string?)null)));
        Assert.That(build.Repos, Is.EqualTo(new[] { "Alpha" }));
        Assert.That(() => db.AcceptMilestone("MS-1", reached: true, by: "owner"), Throws.InvalidOperationException.With.Message.Contains("#1 unknown"));

        var passed = db.SetMilestoneCheckResult("MS-1", 1, "pass", "abc123 built green");
        Assert.That((passed.Result, passed.Evidence), Is.EqualTo((MilestoneCheckResults.Pass, "abc123 built green")));
        Assert.That(passed.Checked, Is.Not.Null);
        db.SetMilestoneCheckResult("MS-1", 2, "fail");
        Assert.That(() => db.AcceptMilestone("MS-1", reached: true, by: "owner"), Throws.InvalidOperationException.With.Message.Contains("#2 fail"));
        Assert.That(db.Milestone("MS-1")!.State, Is.EqualTo(MilestoneStates.Active), "a refusal changes nothing");

        Assert.That(db.AcceptMilestone("MS-1", reached: true, by: "owner", force: true).State, Is.EqualTo(MilestoneStates.Reached));
        Assert.That(db.MilestoneHistory("MS-1").Single(e => e.Field == "accepted").New, Is.EqualTo("owner (forced past #2 fail)"));
    }

    [Test]
    public void A_check_names_what_its_kind_needs_and_can_be_removed()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha"]);

        Assert.That(() => db.AddMilestoneCheck("MS-1", "vibes", "x"), Throws.ArgumentException);
        Assert.That(() => db.AddMilestoneCheck("MS-1", "build", "no repos"), Throws.ArgumentException);
        Assert.That(() => db.AddMilestoneCheck("MS-1", "exists", "no target"), Throws.ArgumentException);
        Assert.That(() => db.AddMilestoneCheck("MS-1", "harness", "no state"), Throws.ArgumentException);
        Assert.That(db.MilestoneChecks("MS-1"), Is.Empty);

        var exists = db.AddMilestoneCheck("MS-1", "exists", "Package is published", target: "MindAttic.Alpha 1.0.0");
        var harness = db.AddMilestoneCheck("MS-1", "harness", "Soak reaches idle", ["Alpha"], "idle");
        Assert.That((exists.Kind, exists.Target, harness.Number, harness.Target), Is.EqualTo(("exists", "MindAttic.Alpha 1.0.0", 2L, "idle")));
        Assert.That(() => db.SetMilestoneCheckResult("MS-1", 9, "pass"), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(() => db.SetMilestoneCheckResult("MS-1", 1, "maybe"), Throws.ArgumentException);
        Assert.That(db.RemoveMilestoneCheck("MS-1", 1), Is.True);
        Assert.That(db.RemoveMilestoneCheck("MS-1", 1), Is.False);
        Assert.That(db.MilestoneChecks("MS-1").Select(c => c.Number), Is.EqualTo(new[] { 2L }));
    }

    [Test]
    public void Tickets_join_a_milestone_part_of_it_and_an_epics_children_come_with_it()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha", "Beta"]);
        db.AddLink("AL-T2", "AL-T1", "part-of");
        db.AddLink("AL-T4", "AL-T1", "part-of");

        var members = db.AddToMilestone("ms-1", ["al-t1", "MS-T1"]);

        Assert.That(members.Select(x => (x.Id, x.Direct)), Is.EqualTo(new[] { ("AL-T1", true), ("MS-T1", true), ("AL-T2", false), ("AL-T4", false) }));
        Assert.That(TicketsDb.MilestoneProgress(members), Is.EqualTo((1, 3)), "closed, of the tickets that aren't wont-do");
        Assert.That(db.Links("AL-T1").Select(l => (l.From, l.Kind, l.To)), Does.Contain(("AL-T1", "part-of", "MS-1")), "an ordinary part-of link, spelt as stored");
        Assert.That(db.History("AL-T1").Where(e => e.Field == "link").Select(e => e.New), Does.Contain("part-of MS-1"));
        Assert.That(db.MilestoneHistory("MS-1").Where(e => e.Field == "member").Select(e => e.New), Is.EqualTo(new[] { "AL-T1", "MS-T1" }));
        Assert.That(db.Check().Where(p => p.StartsWith("link ", StringComparison.Ordinal)), Is.Empty, "a link to a milestone doesn't dangle");

        Assert.That(db.MilestonesOf("AL-T2").Select(x => (x.Milestone.Id, x.Direct)), Is.EqualTo(new[] { ("MS-1", false) }), "through its epic");
        Assert.That(db.MilestonesOf("AL-T1").Select(x => (x.Milestone.Id, x.Direct)), Is.EqualTo(new[] { ("MS-1", true) }));
        Assert.That(db.MilestonesOf("AL-T3"), Is.Empty);

        Assert.That(db.RemoveFromMilestone("MS-1", ["MS-T1", "AL-T3"]).Select(x => x.Id), Is.EqualTo(new[] { "AL-T1", "AL-T2", "AL-T4" }));
        Assert.That(db.MilestoneHistory("MS-1").Last(e => e.Field == "member").Old, Is.EqualTo("MS-T1"));
    }

    [Test]
    public void Only_tickets_on_an_active_milestone_count_as_on_one()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha"]);
        db.AddLink("AL-T2", "AL-T1", "part-of");
        db.AddToMilestone("MS-1", ["AL-T1"]);

        Assert.That(db.IsOnActiveMilestone("AL-T2"), Is.False, "planned, not active yet");
        Assert.That(db.ActiveMilestoneTickets(), Is.Empty);

        db.ActivateMilestone("MS-1");

        Assert.That(db.IsOnActiveMilestone("AL-T2"), Is.True);
        Assert.That(db.IsOnActiveMilestone("AL-T3"), Is.False);
        var on = db.ActiveMilestoneTickets();
        Assert.That(on.Keys, Is.EquivalentTo(new[] { "AL-T1", "AL-T2" }));
        Assert.That(on["al-t2"], Is.EqualTo(new[] { "MS-1" }), "keyed without case");
    }

    [Test]
    public void A_milestone_is_only_the_parent_end_of_a_part_of_link_and_takes_nothing_once_closed()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha"]);
        db.AddMilestone("Other", null, ["Alpha"]);

        Assert.That(() => db.AddLink("MS-1", "AL-T1", "part-of"), Throws.ArgumentException);
        Assert.That(() => db.AddLink("MS-1", "MS-2", "part-of"), Throws.ArgumentException);
        Assert.That(() => db.AddLink("AL-T1", "MS-1", "requires"), Throws.ArgumentException);
        Assert.That(() => db.AddLink("AL-T1", "MS-9", "part-of"), Throws.ArgumentException, "no such milestone");
        Assert.That(() => db.AddToMilestone("MS-1", ["AL-T1", "AL-T99"]), Throws.ArgumentException);
        Assert.That(db.MilestoneMembers("MS-1"), Is.Empty, "all or nothing");

        db.AddLink("MS-1", "AL-T1", "contains");
        Assert.That(db.MilestoneMembers("MS-1").Select(x => x.Id), Is.EqualTo(new[] { "AL-T1" }), "'contains' is part-of read from the milestone's end");

        db.DropMilestone("MS-1", "folded into MS-2");
        Assert.That(() => db.AddToMilestone("MS-1", ["AL-T3"]), Throws.InvalidOperationException);
        Assert.That(() => db.AddLink("AL-T3", "MS-1", "part-of"), Throws.InvalidOperationException);
        Assert.That(() => db.AddMilestoneCheck("MS-1", "signoff", "x"), Throws.InvalidOperationException);
        Assert.That(db.MilestoneHistory("MS-1").Single(e => e.Field == "dropped").New, Is.EqualTo("folded into MS-2"));
    }

    [Test]
    public void Edits_are_recorded_and_a_renamed_repo_and_a_reassigned_ticket_are_followed()
    {
        db.AddMilestone("Ship alpha", null, ["Alpha"]);
        db.AddMilestoneCheck("MS-1", "build", "Builds on main", ["Alpha"]);
        db.AddToMilestone("MS-1", ["AL-T3"]);

        var edited = db.EditMilestone("MS-1", name: "Ship it", goal: "Done means done", order: 7, repos: ["Beta", "Alpha"]);
        Assert.That((edited.Name, edited.Goal, edited.Order), Is.EqualTo(("Ship it", "Done means done", 7L)));
        Assert.That(edited.Repos, Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(db.MilestoneHistory("MS-1").Select(e => e.Field), Is.SupersetOf(new[] { "name", "goal", "order", "repo" }));

        db.RenameRepo("Alpha", "Alpha.Next", "test");
        db.ReassignTicketId("AL-T3", "AL-T9", "test");

        Assert.That(db.Milestone("MS-1")!.Repos, Is.EqualTo(new[] { "Alpha.Next", "Beta" }));
        Assert.That(db.MilestoneChecks("MS-1")[0].Repos, Is.EqualTo(new[] { "Alpha.Next" }));
        Assert.That(db.MilestoneMembers("MS-1").Select(x => x.Id), Is.EqualTo(new[] { "AL-T9" }));
        Assert.That(db.MilestonesOf("AL-T3").Select(x => x.Milestone.Id), Is.EqualTo(new[] { "MS-1" }), "the old ID still resolves");
    }

    [Test]
    public void Milestones_are_in_the_dump()
    {
        db.AddMilestone("Ship alpha", "the goal", ["Alpha"]);
        db.AddMilestoneCheck("MS-1", "signoff", "Owner signs off");

        var dump = db.Dump();
        Assert.That(dump, Does.Contain("INSERT INTO milestones (id, number, name, goal, ordinal, state, created, updated, activated, reached, reached_by) VALUES ('MS-1', 1, 'Ship alpha', 'the goal', 1, 'planned', '"));
        Assert.That(dump, Does.Contain("INSERT INTO milestone_repos (milestone_id, repo) VALUES ('MS-1', 'Alpha');"));
        Assert.That(dump, Does.Contain("INSERT INTO milestone_checks (milestone_id, number, kind, description, repos, target, result, checked, evidence) VALUES ('MS-1', 1, 'signoff', 'Owner signs off', NULL, NULL, 'unknown', NULL, NULL);"));
        Assert.That(dump, Does.Contain("INSERT INTO milestone_events (seq, milestone_id, at, field, old, new) VALUES (1, 'MS-1', '"));
    }
}
