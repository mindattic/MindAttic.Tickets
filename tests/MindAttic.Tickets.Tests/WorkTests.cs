using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using MindAttic.Tickets.Repos;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The ticket functions taken over from the Orchestrator (M.TIX-T134): bump, assign, reserve, the session working a
/// ticket, block, hygiene and GitHub intake, all kept in the ticket database.</summary>
public sealed class WorkTests
{
    private string root = "";
    private string alpha = "";
    private TicketsDb db = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-work-" + Guid.NewGuid().ToString("N"));
        alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | one | — |\n| AL-T2 | 2026-10-02 | fix | open | two | — |\n" +
            "| AL-T3 | 2026-10-03 | fix | closed | three | — |\n| AL-T4 | 2026-10-04 | fix | open | AL-T3 came back again | — |\n");
        db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        db.Import([new Repo("Alpha", alpha)]);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [Test]
    public void Bump_moves_the_priority_by_the_step_and_records_it()
    {
        Assert.That(db.Bump("al-t1", 1), Is.EqualTo(1));
        Assert.That(db.Bump("AL-T1", -3), Is.EqualTo(-2));
        Assert.That(db.QueueState("AL-T1").Priority, Is.EqualTo(-2));
        Assert.That(db.History("AL-T1").Count(e => e.Field == "priority"), Is.EqualTo(2));
        Assert.That(() => db.Bump("AL-T99", 1), Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void Assign_normalizes_the_email_reassigns_and_unassigns_with_history()
    {
        Assert.That(db.Assign("al-t1", " Ryan@Example.com ").Text, Is.EqualTo("AL-T1 is assigned to ryan@example.com: it's dispatched only on that login."));
        Assert.That(db.Work("AL-T1").Assignee, Is.EqualTo("ryan@example.com"));
        Assert.That(db.Assign("AL-T1", "ryan@example.com").Text, Does.Contain("already assigned"));
        Assert.That(db.Assign("AL-T1", "kim@example.com").Text, Does.Contain("reassigned from ryan@example.com to kim@example.com"));
        Assert.That(db.Assignees(), Is.EqualTo(new Dictionary<string, string> { ["AL-T1"] = "kim@example.com" }));
        Assert.That(db.Redelegate("AL-T1").Text, Does.Contain("no longer assigned to kim@example.com"));
        Assert.That(db.Work("AL-T1"), Is.EqualTo(TicketWork.None), "an empty row is dropped");
        Assert.That(db.History("AL-T1").Where(e => e.Field == "assignee").Select(e => (e.Old, e.New)),
            Is.EqualTo(new[] { ((string?)null, (string?)"ryan@example.com"), ("ryan@example.com", "kim@example.com"), ("kim@example.com", null) }));
        Assert.That(() => db.Assign("AL-T1", "Ryan"), Throws.ArgumentException);
        Assert.That(() => db.Assign("AL-T99", "a@b.co"), Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void Reserve_sets_and_releases_tokens_and_keeps_the_assignee()
    {
        db.Assign("AL-T2", "a@b.co");
        db.Reserve("AL-T2", 2_000_000);
        Assert.That(db.Work("AL-T2"), Is.EqualTo(new TicketWork("a@b.co", 2_000_000, null, null, null)));
        Assert.That(db.Reservations(), Is.EqualTo(new Dictionary<string, long> { ["AL-T2"] = 2_000_000 }));
        db.Reserve("AL-T2", 0);
        Assert.That(db.Work("AL-T2").Reserved, Is.Zero);
        Assert.That(db.Work("AL-T2").Assignee, Is.EqualTo("a@b.co"));
        Assert.That(() => db.Reserve("AL-T2", -1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => db.Reserve("AL-T99", 5), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(() => db.Reserve("AL-T99", 0), Throws.Nothing, "releasing one for a ticket gone since works");
    }

    [Test]
    public void Working_is_recorded_and_cleared()
    {
        db.SetWorking("AL-T1", "Ryan@Example.com", "abc-123");
        var w = db.Work("AL-T1");
        Assert.That((w.IsWorking, w.Login, w.Session, w.Since is not null), Is.EqualTo((true, "ryan@example.com", "abc-123", true)));
        db.ClearWorking("AL-T1");
        Assert.That(db.Work("AL-T1").IsWorking, Is.False);
        Assert.That(db.History("AL-T1").Where(e => e.Field == "working").Select(e => e.New), Is.EqualTo(new[] { "ryan@example.com (abc-123)", null }));
        Assert.That(() => db.SetWorking("AL-T1", null, " "), Throws.ArgumentException);
    }

    [Test]
    public void Block_checks_everything_before_changing_anything()
    {
        Assert.That(db.Block("AL-T1", ["AL-T2"]), Is.EqualTo(new[] { "AL-T2" }));
        Assert.That(() => db.Block("AL-T2", ["AL-T1"]), Throws.InvalidOperationException.With.Message.Contains("cycle"));
        Assert.That(() => db.Block("AL-T1", ["AL-T1"]), Throws.InvalidOperationException);
        Assert.That(() => db.Block("AL-T1", ["AL-T4", "AL-T99"], clear: true), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(db.Requires("AL-T1"), Is.EqualTo(new[] { "AL-T2" }), "nothing changed, the clear included");
        Assert.That(db.Block("AL-T1", ["AL-T4"], clear: true), Is.EqualTo(new[] { "AL-T4" }));
        Assert.That(db.Block("AL-T1", [], clear: true), Is.Empty);
    }

    [Test]
    public void Reassigning_a_ticket_id_carries_its_work_and_the_dump_lists_it()
    {
        db.Assign("AL-T1", "a@b.co");
        db.ReassignTicketId("AL-T1", "AL-T10", "test");
        Assert.That(db.Work("AL-T10").Assignee, Is.EqualTo("a@b.co"));
        Assert.That(db.Work("AL-T1").Assignee, Is.EqualTo("a@b.co"), "the old id still resolves");
        Assert.That(db.Dump(), Does.Contain("INSERT INTO ticket_work (ticket_id, assignee, reserved, login, session, since, updated) VALUES ('AL-T10', 'a@b.co', 0, NULL, NULL, NULL, '"));
    }

    [Test]
    public void Hygiene_flags_a_closed_fix_with_no_ref_and_one_that_came_back()
    {
        var found = TicketHygiene.Check(db.Tickets());
        Assert.That(found.Select(f => (f.TicketId, f.Why.StartsWith("closed with no Ref", StringComparison.Ordinal) ? "no ref" : f.Why[..5])),
            Is.EqualTo(new[] { ("AL-T3", "no ref"), ("AL-T3", "AL-T4") }));
    }

    [Test]
    public void Intake_files_each_issue_and_ci_failure_once()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        (int, string) Gh(string[] a) => a[0] == "issue"
            ? (0, """[{"number":7,"title":"Crash on save","labels":[{"name":"Feature"}],"url":"https://github.com/o/alpha/issues/7"}]""")
            : a[5] == "main"
                ? (0, """[{"databaseId":99,"name":"CI","headBranch":"main","createdAt":"2026-10-23T08:00:00Z","url":"u"},{"databaseId":98,"name":"CI","headBranch":"main","createdAt":"2026-10-20T08:00:00Z","url":"u"}]""")
                : (1, "");
        var ws = new Workspace(root);
        var first = GitHubIntake.Intake(ws, db, gh: Gh, remote: _ => "git@github.com:o/alpha.git", nowUtc: now).ToList();
        Assert.That(first.Select(x => (x.Repo, x.Ticket.Type, x.Ticket.Summary)),
            Is.EqualTo(new[] { ("Alpha", "feature", "GH#7: Crash on save"), ("Alpha", "fix", "CI#99: CI failed on main") }));
        Assert.That(GitHubIntake.Intake(ws, db, gh: Gh, remote: _ => "https://github.com/o/alpha", nowUtc: now), Is.Empty, "filed once");
        Assert.That(GitHubIntake.Slug("https://gitlab.com/o/x"), Is.Null);
    }
}
