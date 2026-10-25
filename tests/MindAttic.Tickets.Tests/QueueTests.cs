using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

public sealed class QueueTests
{
    private string root = "";
    private TicketsDb db = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-queue-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | one | — |\n| AL-T2 | 2026-10-02 | fix | open | two | — |\n" +
            "| AL-T3 | 2026-10-03 | chore | open | three | — |\n| AL-T4 | 2026-10-04 | chore | closed | done | — |\n");
        db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        db.Import([new Repo("Alpha", dir)]);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private IEnumerable<string> Order() => db.Queue().Select(q => q.Ticket.Id);

    [Test]
    public void Open_tickets_start_proposed_at_priority_zero_oldest_first()
    {
        Assert.That(db.Queue().Select(q => (q.Ticket.Id, q.Approval, q.Priority)),
            Is.EqualTo(new[] { ("AL-T1", "proposed", 0L), ("AL-T2", "proposed", 0L), ("AL-T3", "proposed", 0L) }));
        Assert.That(db.QueueState("AL-T4"), Is.EqualTo(("proposed", 0L)));
    }

    [Test]
    public void Approve_and_undo_are_recorded_in_history()
    {
        db.Approve("al-t2");
        Assert.That(db.QueueState("AL-T2").Approval, Is.EqualTo("approved"));
        db.Approve("AL-T2"); // no change, no event
        db.Unapprove("AL-T2");
        Assert.That(db.History("AL-T2").Where(e => e.Field == "approval").Select(e => (e.Old, e.New)),
            Is.EqualTo(new[] { ((string?)"proposed", (string?)"approved"), ("approved", "proposed") }));
    }

    [Test]
    public void Reject_needs_a_reason_and_closes_the_ticket_wont_do_with_the_reason_as_ref()
    {
        Assert.That(() => db.Reject("AL-T1", " "), Throws.ArgumentException);
        var t = db.Reject("AL-T1", "not needed");
        Assert.That(db.Ticket("AL-T1")!.Value.Ticket, Has.Property("Status").EqualTo("wont-do").And.Property("Ref").EqualTo("not needed"));
        Assert.That(t.Status, Is.EqualTo("wont-do"));
        Assert.That(db.QueueState("AL-T1").Approval, Is.EqualTo("rejected"));
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T2", "AL-T3" }));
        Assert.That(db.History("AL-T1").Select(e => e.Field), Is.SupersetOf(new[] { "approval", "status", "ref" }));
    }

    [Test]
    public void Only_open_tickets_can_be_approved_or_rejected()
    {
        Assert.That(() => db.Approve("AL-T4"), Throws.InvalidOperationException);
        Assert.That(() => db.Reject("AL-T4", "x"), Throws.InvalidOperationException);
        Assert.That(() => db.Approve("AL-T99"), Throws.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Reorder_gives_descending_priorities_and_records_only_changes()
    {
        db.Reorder(["AL-T3", "AL-T1", "AL-T2"]);
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T3", "AL-T1", "AL-T2" }));
        Assert.That(db.Queue().Select(q => q.Priority), Is.EqualTo(new[] { 3L, 2L, 1L }));

        db.Reorder(["AL-T3", "AL-T2", "AL-T1"]); // T3 stays at 3
        Assert.That(db.History("AL-T3").Count(e => e.Field == "priority"), Is.EqualTo(1));
        Assert.That(db.History("AL-T2").Where(e => e.Field == "priority").Select(e => e.New), Is.EqualTo(new[] { "1", "2" }));
    }

    [Test]
    public void Reorder_is_all_or_nothing()
    {
        db.Reorder(["AL-T1", "AL-T2"]);
        Assert.That(() => db.Reorder(["AL-T2", "AL-T1", "AL-T99"]), Throws.InstanceOf<KeyNotFoundException>());
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T1", "AL-T2", "AL-T3" }), "the failed reorder changed nothing");
        Assert.That(() => db.Reorder(["AL-T1", "al-t1"]), Throws.ArgumentException);
    }

    [Test]
    public void Approval_survives_a_priority_change_and_queue_state_is_in_the_dump()
    {
        db.Approve("AL-T1");
        db.SetPriority("AL-T1", 7);
        Assert.That(db.QueueState("AL-T1"), Is.EqualTo(("approved", 7L)));
        Assert.That(db.Dump(), Does.Contain("INSERT INTO ticket_queue (ticket_id, approval, priority, updated, estimate) VALUES ('AL-T1', 'approved', 7, '"));
    }

    // ── Effort estimates (M.TIX-T75) ────────────────────────────────────────

    [Test]
    public void Within_a_priority_the_smallest_estimate_runs_first_and_tickets_without_one_last()
    {
        db.SetEstimate("AL-T3", 100_000);
        db.SetEstimate("AL-T2", 500_000);
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T3", "AL-T2", "AL-T1" }));
        Assert.That(db.Queue().Select(q => q.Estimate), Is.EqualTo(new long?[] { 100_000, 500_000, null }));
        db.SetPriority("AL-T1", 1); // priority still comes first
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T1", "AL-T3", "AL-T2" }));
    }

    [Test]
    public void An_estimate_is_set_cleared_and_recorded_without_touching_approval_or_priority()
    {
        db.Approve("AL-T1");
        db.SetPriority("AL-T1", 4);
        db.SetEstimate("al-t1", 300_000);
        db.SetEstimate("AL-T1", 300_000); // no change, no event
        Assert.That(db.Estimate("AL-T1"), Is.EqualTo(300_000));
        Assert.That(db.QueueState("AL-T1"), Is.EqualTo(("approved", 4L)));
        db.SetPriority("AL-T1", 5);
        Assert.That(db.Estimate("AL-T1"), Is.EqualTo(300_000), "a priority change keeps the estimate");
        Assert.That(db.Dump(), Does.Contain("VALUES ('AL-T1', 'approved', 5, '").And.Contain("', 300000);"));
        db.SetEstimate("AL-T1", null);
        Assert.That(db.Estimate("AL-T1"), Is.Null);
        Assert.That(db.History("AL-T1").Where(e => e.Field == "estimate").Select(e => (e.Old, e.New)),
            Is.EqualTo(new[] { ((string?)null, (string?)"300000"), ("300000", null) }));
        Assert.That(() => db.SetEstimate("AL-T1", -1), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => db.SetEstimate("AL-T99", 1), Throws.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void An_estimate_on_a_ticket_never_queued_starts_it_proposed_at_zero()
    {
        db.SetEstimate("AL-T2", 42);
        Assert.That(db.QueueState("AL-T2"), Is.EqualTo(("proposed", 0L)));
        Assert.That(db.Estimates(), Is.EquivalentTo(new Dictionary<string, long> { ["AL-T2"] = 42 }));
    }

    [Test]
    public void A_schema_6_file_gains_the_estimate_column_and_keeps_its_queue()
    {
        db.Approve("AL-T1");
        var path = db.Path;
        db.Dispose();
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "ALTER TABLE ticket_queue DROP COLUMN estimate; UPDATE meta SET value = '6' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }
        db = TicketsDb.Open(path);
        Assert.That(db.Dump(), Does.Contain($"('schema_version', '{TicketsDb.SchemaVersion}')"));
        Assert.That(db.QueueState("AL-T1").Approval, Is.EqualTo("approved"));
        db.SetEstimate("AL-T1", 7);
        Assert.That(db.Estimate("AL-T1"), Is.EqualTo(7));
    }

    [Test]
    public void Seeding_gives_open_tickets_without_one_the_median_of_tickets_like_them()
    {
        PastCost[] costs =
        [
            // Three chores in Alpha: their median wins for AL-T3.
            new("AL-T10", "chore", "Alpha", 100), new("AL-T11", "chore", "Alpha", 300), new("AL-T12", "chore", "Alpha", 200),
            // Fixes: only two in Alpha, so the median of every fix (four) for AL-T2. Two sessions on one ticket add up.
            new("AL-T20", "fix", "Alpha", 1_000), new("AL-T21", "fix", "Alpha", 2_000), new("B-T1", "fix", "Beta", 3_000), new("B-T1", "fix", "Beta", 1_000),
            new("B-T2", "fix", "Beta", 5_000),
            // Features: none anywhere near three, so AL-T1 gets the median of every ticket.
        ];
        db.SetEstimate("AL-T1", 9); // already has one: kept
        var given = db.SeedEstimates(costs, dryRun: true);
        Assert.That(given.Select(g => (g.TicketId, g.Estimate.Tokens, g.Estimate.Basis, g.Estimate.Of)),
            Is.EquivalentTo(new[] { ("AL-T2", 4_000L, 4, "fix tickets"), ("AL-T3", 200L, 3, "chore tickets in Alpha") }));
        Assert.That(db.Estimate("AL-T2"), Is.Null, "a dry run writes nothing");

        db.SeedEstimates(costs);
        Assert.That((db.Estimate("AL-T1"), db.Estimate("AL-T2"), db.Estimate("AL-T3")), Is.EqualTo(((long?)9, (long?)4_000, (long?)200)));
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T1", "AL-T3", "AL-T2" }));

        var again = db.SeedEstimates(costs, overwrite: true);
        Assert.That(again.Select(g => (g.TicketId, g.Estimate.Tokens, g.Estimate.Of)), Is.EqualTo(new[] { ("AL-T1", 1_000L, "tickets") }),
            "overwriting re-estimates only what changes; every ticket's median is 1000 of 7");
        Assert.That(db.SeedEstimates([]), Is.Empty, "no costs, no estimates");
    }

    [Test]
    public void The_orchestrators_ticket_costs_are_read_from_its_file()
    {
        var file = Path.Combine(root, "orchestrator.db");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE ticket_costs (session_id TEXT PRIMARY KEY, ticket_id TEXT NOT NULL, type TEXT, repo TEXT NOT NULL, fresh INTEGER NOT NULL, " +
                              "last_utc TEXT NOT NULL, own_share INTEGER NOT NULL DEFAULT 0, model TEXT); " +
                              "INSERT INTO ticket_costs VALUES ('s1', 'AL-T10', 'chore', 'Alpha', 1234, '2026-10-01T00:00:00Z', 1, 'opus'), ('s2', 'AL-T11', NULL, 'Alpha', 5, '2026-10-01T00:00:00Z', 1, NULL);";
            cmd.ExecuteNonQuery();
        }
        var was = (Environment.GetEnvironmentVariable(OrchestratorCosts.DbEnvVar), Environment.GetEnvironmentVariable(OrchestratorCosts.ServerEnvVar));
        try
        {
            Environment.SetEnvironmentVariable(OrchestratorCosts.DbEnvVar, file);
            Environment.SetEnvironmentVariable(OrchestratorCosts.ServerEnvVar, null);
            var (costs, source) = OrchestratorCosts.Load(root);
            Assert.That(source, Is.EqualTo(Path.GetFullPath(file)));
            Assert.That(costs, Is.EquivalentTo(new[] { new PastCost("AL-T10", "chore", "Alpha", 1234), new PastCost("AL-T11", null, "Alpha", 5) }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OrchestratorCosts.DbEnvVar, was.Item1);
            Environment.SetEnvironmentVariable(OrchestratorCosts.ServerEnvVar, was.Item2);
            SqliteConnection.ClearAllPools();
        }
    }

    private int Writes(string id) => db.History(id).Count(e => e.Field == "priority");

    [Test]
    public void Move_to_the_top_of_an_unranked_queue_writes_only_the_moved_ticket()
    {
        db.Move("AL-T3", ["AL-T3", "AL-T1", "AL-T2"]);
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T3", "AL-T1", "AL-T2" }));
        Assert.That((Writes("AL-T1"), Writes("AL-T2"), Writes("AL-T3")), Is.EqualTo((0, 0, 1)));
    }

    [Test]
    public void Move_into_the_middle_nudges_only_the_shorter_side()
    {
        db.Move("AL-T1", ["AL-T2", "AL-T1", "AL-T3"]);
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T2", "AL-T1", "AL-T3" }));
        Assert.That(Writes("AL-T1") + Writes("AL-T2") + Writes("AL-T3"), Is.EqualTo(2), "the moved ticket and the one above it");
    }

    [Test]
    public void Move_to_the_bottom_and_back_keeps_the_order_shown()
    {
        db.Move("AL-T1", ["AL-T2", "AL-T3", "AL-T1"]);
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T2", "AL-T3", "AL-T1" }));
        db.Move("AL-T1", ["AL-T1", "AL-T2", "AL-T3"]);
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T1", "AL-T2", "AL-T3" }));
        db.Move("AL-T3", ["AL-T1", "AL-T3", "AL-T2"]);
        Assert.That(Order(), Is.EqualTo(new[] { "AL-T1", "AL-T3", "AL-T2" }));
        Assert.That(() => db.Move("AL-T9", ["AL-T1"]), Throws.ArgumentException);
    }

    [Test]
    public void Queue_states_lists_only_tickets_with_a_row()
    {
        db.Approve("AL-T2");
        Assert.That(db.QueueStates(), Is.EquivalentTo(new Dictionary<string, (string, long)> { ["AL-T2"] = ("approved", 0) }));
    }

    [Test]
    public void A_version_one_database_upgrades_to_the_current_version()
    {
        var path = db.Path;
        db.Dispose();
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE ticket_queue; UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }
        db = TicketsDb.Open(path);
        Assert.That(db.Dump(), Does.Contain($"('schema_version', '{TicketsDb.SchemaVersion}')"));
        db.Approve("AL-T1");
        Assert.That(db.QueueState("AL-T1").Approval, Is.EqualTo("approved"));
    }
}
