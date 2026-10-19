using Microsoft.Data.Sqlite;
using MindAttic.Workflow.Data;
using NUnit.Framework;

namespace MindAttic.Workflow.Tests;

public sealed class QueueTests
{
    private string root = "";
    private WorkflowDb db = null!;

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
        db = WorkflowDb.Open(Path.Combine(root, "MindAttic.Workflow", "data", "workflow.db"));
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
        Assert.That(db.Dump(), Does.Contain("INSERT INTO ticket_queue (ticket_id, approval, priority, updated) VALUES ('AL-T1', 'approved', 7, '"));
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
        db = WorkflowDb.Open(path);
        Assert.That(db.Dump(), Does.Contain($"('schema_version', '{WorkflowDb.SchemaVersion}')"));
        db.Approve("AL-T1");
        Assert.That(db.QueueState("AL-T1").Approval, Is.EqualTo("approved"));
    }
}
