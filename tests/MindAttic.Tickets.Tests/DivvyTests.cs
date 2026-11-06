using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Repos;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>M.TIX-T326: splitting a repo's open tickets between members' logins.</summary>
public sealed class DivvyTests
{
    private const string Ryan = "ryan@example.com";
    private const string Amy = "amy@example.com";
    private static readonly string[] Both = [Ryan, Amy];

    private static DivvyTicket T(string id, long? est = null, string? who = null, bool working = false) => new(id, est, who, working);

    private static Dictionary<string, string> Owners(DivvyPlan plan, params DivvyTicket[] open) =>
        open.ToDictionary(t => t.Id, t => plan.Moves.FirstOrDefault(m => m.TicketId == t.Id)?.To ?? t.Assignee ?? "");

    [Test]
    public void Unassigned_tickets_are_split_evenly_by_estimate_largest_first()
    {
        DivvyTicket[] open = [T("US-T1", 500), T("US-T2", 300), T("US-T3", 200), T("US-T4", 100)];
        var plan = Divvy.Plan(open, [], Both);
        var owners = Owners(plan, open);
        Assert.That(owners["US-T1"], Is.EqualTo(Ryan));
        Assert.That(owners["US-T2"], Is.EqualTo(Amy));
        Assert.That(owners["US-T3"], Is.EqualTo(Amy));
        Assert.That(owners["US-T4"], Is.EqualTo(Ryan));
        Assert.That(plan.Shares.Select(s => s.Weight), Is.EqualTo(new[] { 600L, 500L }));
        Assert.That(plan.Shares.Select(s => s.Tickets), Is.EqualTo(new[] { 2, 2 }));
    }

    [Test]
    public void Tickets_without_an_estimate_weigh_the_median()
    {
        DivvyTicket[] open = [T("US-T1", 100), T("US-T2", 300), T("US-T3"), T("US-T4")];
        var plan = Divvy.Plan(open, [], Both);
        Assert.That(plan.Moves.Single(m => m.TicketId == "US-T3").Weight, Is.EqualTo(300));
        Assert.That(plan.Shares.Sum(s => s.Weight), Is.EqualTo(1000));
    }

    [Test]
    public void A_prerequisite_chain_goes_to_one_member()
    {
        DivvyTicket[] open = [T("US-T1", 100), T("US-T2", 100), T("US-T3", 100), T("US-T4", 100)];
        var plan = Divvy.Plan(open, [("US-T2", "US-T1"), ("US-T3", "US-T2")], Both);
        var owners = Owners(plan, open);
        Assert.That(owners["US-T1"], Is.EqualTo(owners["US-T2"]).And.EqualTo(owners["US-T3"]));
        Assert.That(owners["US-T4"], Is.Not.EqualTo(owners["US-T1"]));
    }

    [Test]
    public void A_chain_someone_already_holds_part_of_stays_with_them()
    {
        DivvyTicket[] open = [T("US-T1", 100, Amy), T("US-T2", 100), T("US-T3", 100)];
        var plan = Divvy.Plan(open, [("US-T2", "US-T1")], Both);
        Assert.That(plan.Moves.Single(m => m.TicketId == "US-T2").To, Is.EqualTo(Amy));
        Assert.That(plan.Moves.Single(m => m.TicketId == "US-T3").To, Is.EqualTo(Ryan));
    }

    [Test]
    public void Working_tickets_and_other_peoples_pins_never_move_but_count_towards_a_share()
    {
        DivvyTicket[] open = [T("US-T1", 400, Ryan, working: true), T("US-T2", 100, "kim@example.com"), T("US-T3", 200), T("US-T4", 200)];
        var plan = Divvy.Plan(open, [], Both);
        Assert.That(plan.Moves.Select(m => m.TicketId), Is.EquivalentTo(new[] { "US-T3", "US-T4" }));
        Assert.That(plan.Moves.All(m => m.To == Amy), Is.True, "Ryan already carries 400 being worked");
        Assert.That(plan.LeftAlone, Is.EqualTo(new[] { "US-T1", "US-T2" }));
    }

    [Test]
    public void Without_rebalance_held_tickets_stay_put()
    {
        DivvyTicket[] open = [T("US-T1", 100, Ryan), T("US-T2", 100, Ryan), T("US-T3", 100, Ryan)];
        Assert.That(Divvy.Plan(open, [], Both).Moves, Is.Empty);
    }

    [Test]
    public void Rebalance_moves_just_enough_unstarted_work_to_whoever_ran_dry()
    {
        DivvyTicket[] open = [T("US-T1", 100, Ryan, working: true), T("US-T2", 100, Ryan), T("US-T3", 100, Ryan), T("US-T4", 100, Ryan)];
        var plan = Divvy.Plan(open, [], Both, rebalance: true);
        Assert.That(plan.Moves.Count, Is.EqualTo(2));
        Assert.That(plan.Moves.All(m => m.From == Ryan && m.To == Amy), Is.True);
        Assert.That(plan.Moves.Any(m => m.TicketId == "US-T1"), Is.False, "the ticket being worked stays");
        Assert.That(plan.Shares.Select(s => s.Weight), Is.EqualTo(new[] { 200L, 200L }));
    }

    [Test]
    public void Rebalance_leaves_a_chain_alone_while_a_member_is_working_part_of_it()
    {
        DivvyTicket[] open = [T("US-T1", 100, Ryan, working: true), T("US-T2", 300, Ryan)];
        var plan = Divvy.Plan(open, [("US-T2", "US-T1")], Both, rebalance: true);
        Assert.That(plan.Moves, Is.Empty);
    }

    [Test]
    public void Members_are_normalized_and_a_bad_address_throws()
    {
        var plan = Divvy.Plan([T("US-T1", 1)], [], [" Ryan@Example.com ", "ryan@example.com"]);
        Assert.That(plan.Shares.Single().Member, Is.EqualTo(Ryan));
        Assert.That(() => Divvy.Plan([], [], ["nobody"]), Throws.ArgumentException);
        Assert.That(() => Divvy.Plan([], [], []), Throws.ArgumentException);
    }

    [Test]
    public void Database_divvy_assigns_with_history_and_dry_run_writes_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-divvy-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | one | — |\n| AL-T2 | 2026-10-02 | fix | open | two | — |\n" +
            "| AL-T3 | 2026-10-03 | fix | closed | three | — |\n| AL-T4 | 2026-10-04 | fix | open | four | — |\n");
        var db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        try
        {
            db.Import([new Repo("Alpha", alpha)]);
            db.SetEstimate("AL-T1", 500);
            db.SetEstimate("AL-T2", 300);
            db.SetEstimate("AL-T4", 200);

            var preview = db.Divvy("Alpha", Both, dryRun: true);
            Assert.That(preview.Moves.Count, Is.EqualTo(3));
            Assert.That(db.Assignees(), Is.Empty);

            db.Divvy("Alpha", Both);
            Assert.That(db.Assignees(), Is.EqualTo(new Dictionary<string, string> { ["AL-T1"] = Ryan, ["AL-T2"] = Amy, ["AL-T4"] = Amy }));
            Assert.That(db.History("AL-T2").Count(e => e.Field == "assignee"), Is.EqualTo(1));
            Assert.That(db.Divvy("Alpha", Both).Moves, Is.Empty, "running it again changes nothing");
        }
        finally
        {
            db.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
