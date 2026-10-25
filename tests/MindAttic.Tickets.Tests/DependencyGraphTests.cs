using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The dependency graph (M.TIX-T83): requires chains both ways, blockers marked, repeats and cycles not followed.</summary>
public sealed class DependencyGraphTests
{
    private static Ticket T(string id, string status = "open") => new(id, "2026-10-01", "feature", status, id.ToLowerInvariant(), "—");
    private static Link Requires(string from, string to) => new(from, to, "requires", null);

    [Test]
    public void Walks_both_ways_marking_open_blockers_and_what_it_blocks()
    {
        var tickets = new[] { T("A-T1"), T("A-T2"), T("A-T3", "closed"), T("A-T4"), T("A-T5"), T("A-T6") };
        var links = new[]
        {
            Requires("A-T1", "A-T2"), Requires("a-t2", "A-T3"), // A-T1 → A-T2 (open) → A-T3 (closed); IDs match without case
            Requires("A-T4", "A-T1"), Requires("A-T5", "A-T4"), // A-T5 → A-T4 → A-T1
            new Link("A-T6", "A-T1", "related", null),          // not a requires link: not in the graph
        };
        var g = DependencyGraph.Build("a-t1", tickets, links);

        Assert.That(g.Id, Is.EqualTo("A-T1"));
        Assert.That(g.Upstream.Select(n => (n.Id, n.Depth, n.Blocking)), Is.EqualTo(new[] { ("A-T2", 1, true), ("A-T3", 2, false) }));
        Assert.That(g.Downstream.Select(n => (n.Id, n.Depth, n.Blocking)), Is.EqualTo(new[] { ("A-T4", 1, true), ("A-T5", 2, true) }));
        Assert.That(g.Blockers, Is.EqualTo(new[] { "A-T2" }));
        Assert.That(g.Blocked, Is.EqualTo(new[] { "A-T4", "A-T5" }));
        Assert.That(g.TextLines(), Is.EqualTo(new[]
        {
            "A-T1, open: a-t1",
            "Waits for (1 open blocker):",
            "  A-T2, open, blocking: a-t2",
            "    A-T3, closed: a-t3",
            "Needed by (2 blocked by it):",
            "  A-T4, open, blocked by it: a-t4",
            "    A-T5, open, blocked by it: a-t5",
        }));
    }

    [Test]
    public void A_closed_ticket_is_blocked_by_nothing_and_blocks_nothing()
    {
        var g = DependencyGraph.Build("A-T1", [T("A-T1", "closed"), T("A-T2"), T("A-T3")], [Requires("A-T1", "A-T2"), Requires("A-T3", "A-T1")]);
        Assert.That(g.Upstream.Single().Blocking, Is.False);
        Assert.That(g.Downstream.Single().Blocking, Is.False);
        Assert.That((g.Blockers.Count, g.Blocked.Count), Is.EqualTo((0, 0)));
    }

    [Test]
    public void A_diamond_lists_the_shared_ticket_once_and_a_cycle_stops()
    {
        // A-T1 → A-T2 → A-T4, A-T1 → A-T3 → A-T4 (a diamond), and A-T4 → A-T1 closing a cycle; A-T9 isn't a ticket.
        var g = DependencyGraph.Build("A-T1", [T("A-T1"), T("A-T2"), T("A-T3"), T("A-T4")],
            [Requires("A-T1", "A-T2"), Requires("A-T1", "A-T3"), Requires("A-T2", "A-T4"), Requires("A-T3", "A-T4"), Requires("A-T4", "A-T1"), Requires("A-T4", "A-T9")]);

        Assert.That(g.Upstream.Select(n => (n.Id, n.Depth, n.Repeat, n.Cycle)), Is.EqualTo(new[]
        {
            ("A-T2", 1, false, false), ("A-T4", 2, false, false), ("A-T1", 3, false, true), ("A-T9", 3, false, false),
            ("A-T3", 1, false, false), ("A-T4", 2, true, false),
        }));
        Assert.That(g.Upstream.Single(n => n.Id == "A-T9").Known, Is.False);
        Assert.That(g.Blockers, Is.EqualTo(new[] { "A-T2", "A-T4", "A-T3" }));
        Assert.That(DependencyGraph.Layers(g.Upstream).Select(l => l.Select(n => n.Id)),
            Is.EqualTo(new[] { new[] { "A-T2", "A-T3" }, new[] { "A-T4" }, new[] { "A-T9" } }));
        Assert.That(g.TextLines(), Has.Some.EqualTo("      A-T1, open: a-t1 (cycle: leads back round)")
            .And.Some.EqualTo("    A-T4, open, blocking: a-t4 (listed above)").And.Some.EqualTo("      A-T9, not a ticket"));
    }

    [Test]
    public void No_requires_links_is_an_empty_graph()
    {
        var g = DependencyGraph.Build("A-T1", [T("A-T1")], []);
        Assert.That(g.IsEmpty, Is.True);
        Assert.That(g.TextLines(), Is.EqualTo(new[] { "A-T1, open: a-t1", "Waits for nothing.", "Nothing waits for it." }));
    }
}
