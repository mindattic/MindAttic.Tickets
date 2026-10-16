using MindAttic.Workflow.Data;
using NUnit.Framework;

namespace MindAttic.Workflow.Tests;

[TestFixture]
public sealed class SyncCommitTests
{
    private const string Before =
        "-- tickets\n" +
        "INSERT INTO tickets (id, repo, number, date, type, status, summary, ref, ordinal, row_md) VALUES ('M.FLOW-T1', 'MindAttic.Workflow', 1, '2026-10-12', 'docs', 'open', 's', '—', 0, NULL);\n" +
        "-- ticket_events\n" +
        "INSERT INTO ticket_events (seq, ticket_id, at, field, old, new) VALUES (1, 'M.FLOW-T1', '2026-10-12T00:00:00Z', 'created', NULL, 'open');\n" +
        "-- stories\n" +
        "INSERT INTO stories (uid, repo, story_id) VALUES (1, 'MindAttic.Workflow', 'M.FLOW-US-A1');\n";

    [Test]
    public void Tickets_with_a_new_row_or_history_event_are_the_ones_named()
    {
        var after = Before
            .Replace("'docs', 'open'", "'docs', 'closed'")
            .Replace("-- stories", "INSERT INTO ticket_events (seq, ticket_id, at, field, old, new) VALUES (2, 'M.FLOW-T1', 'x', 'status', 'open', 'closed');\n" +
                                   "INSERT INTO tickets (id, repo, number, date, type, status, summary, ref, ordinal, row_md) VALUES ('U.SPC-T3', 'UnitySolarSystem', 3, '2026-10-16', 'fix', 'open', 's', '—', 0, NULL);\n-- stories");

        Assert.That(SyncCommit.ChangedTickets(Before, after), Is.EqualTo(new[] { "M.FLOW-T1", "U.SPC-T3" }));
        Assert.That(SyncCommit.Subject(SyncCommit.ChangedTickets(Before, after)), Is.EqualTo("Sync the workflow database for M.FLOW-T1 and U.SPC-T3 (U.SPC-T3)"));
    }

    [Test]
    public void A_story_edit_alone_names_no_ticket_and_so_makes_no_subject()
    {
        var after = Before.Replace("'M.FLOW-US-A1'", "'M.FLOW-US-A2'");

        Assert.That(SyncCommit.ChangedTickets(Before, after), Is.Empty);
        Assert.That(SyncCommit.Subject([]), Is.Null);
    }

    [Test]
    public void An_explicit_ticket_names_the_commit_and_the_first_sync_counts_every_ticket()
    {
        Assert.That(SyncCommit.Subject([], "M.ORCH-T7"), Is.EqualTo("Sync the workflow database (M.ORCH-T7)"));
        Assert.That(SyncCommit.ChangedTickets(null, Before), Is.EqualTo(new[] { "M.FLOW-T1" }));
        Assert.That(SyncCommit.Subject(["A-T1", "B-T2", "C-T3", "D-T4", "E-T5", "F-T6", "G-T7"]),
            Is.EqualTo("Sync the workflow database for A-T1, B-T2, C-T3, D-T4, E-T5, 1 more and G-T7 (G-T7)"));
    }
}
