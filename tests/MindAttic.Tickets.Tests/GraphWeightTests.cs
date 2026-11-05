using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The node graph (M.TIX-T314) and the graph weight that orders the queue (M.TIX-T316), on small graphs.</summary>
public sealed class GraphWeightTests
{
    private static (string, Ticket) T(string repo, string id, string status = "open") => (repo, new Ticket(id, "2026-10-01", "feature", status, id.ToLowerInvariant(), "—"));
    private static Link L(string from, string kind, string to) => new(from, to, kind, null);

    [Test]
    public void A_cross_repo_blocker_outweighs_a_same_repo_one_and_closed_dependents_count_zero()
    {
        var graph = TicketGraph.Build(
            [T("A", "A-T1"), T("A", "A-T2"), T("B", "B-T1"), T("C", "C-T1"), T("A", "A-T3"), T("A", "A-T4"), T("A", "A-T5", "closed")],
            [
                L("B-T1", "requires", "A-T1"), L("C-T1", "requires", "B-T1"), // A-T1 unblocks B-T1 and, through it, C-T1: 2 other repos
                L("A-T3", "requires", "A-T2"), L("A-T4", "requires", "A-T2"), // A-T2 unblocks two in its own repo
                L("A-T5", "requires", "A-T4"),                               // a closed dependent counts zero
            ]);
        var w = GraphWeight.Of(graph);

        Assert.That((w["A-T1"].Unblocks, w["A-T1"].Repos, w["A-T1"].Spread), Is.EqualTo((2, 2, 2)));
        Assert.That(w["A-T1"].Value, Is.EqualTo(2 * GraphWeight.UnblockWeight + 2 * GraphWeight.RepoWeight));
        Assert.That((w["A-T2"].Unblocks, w["A-T2"].Repos), Is.EqualTo((2, 0)));
        Assert.That(w["A-T1"].Value, Is.GreaterThan(w["A-T2"].Value));
        Assert.That(w["A-T4"].Unblocks, Is.Zero);
        Assert.That(w["A-T1"].Why, Is.EqualTo("unblocks 2 tickets in 2 repos"));
        Assert.That(w.ContainsKey("A-T5"), Is.False, "only open tickets are weighed");
    }

    [Test]
    public void Children_parents_and_related_edges_add_smaller_weights()
    {
        var graph = TicketGraph.Build(
            [T("A", "A-T1"), T("A", "A-T2"), T("A", "A-T3"), T("A", "A-T4", "closed"), T("A", "A-T5")],
            [L("A-T2", "part-of", "A-T1"), L("A-T3", "part-of", "A-T2"), L("A-T4", "part-of", "A-T1"), L("A-T5", "related", "A-T1")]);
        var w = GraphWeight.Of(graph);

        Assert.That(w["A-T1"].Descendants, Is.EqualTo(2), "open children at any depth; the closed one counts zero");
        Assert.That(w["A-T1"].Related, Is.EqualTo(1));
        Assert.That(w["A-T1"].Value, Is.EqualTo(2 * GraphWeight.ChildWeight + GraphWeight.RelatedWeight));
        Assert.That(w["A-T3"].InEffort, Is.True);
        Assert.That(w["A-T3"].Value, Is.EqualTo(GraphWeight.ParentWeight));
    }

    [Test]
    public void Explicit_priority_always_beats_graph_weight_and_ties_keep_their_order()
    {
        var entries = new[] { ("low-heavy", 0L, 500L), ("high-light", 5L, 0L), ("low-light-first", 0L, 0L), ("low-light-second", 0L, 0L) };
        var ordered = GraphWeight.Order(entries, e => e.Item2, e => e.Item3).Select(e => e.Item1);
        Assert.That(ordered, Is.EqualTo(new[] { "high-light", "low-heavy", "low-light-first", "low-light-second" }));
    }

    [Test]
    public void Graph_finds_cycles_paths_relations_and_neighbourhoods()
    {
        var graph = TicketGraph.Build(
            [T("A", "A-T1"), T("A", "A-T2"), T("B", "B-T1"), T("A", "A-T9", "closed")],
            [L("A-T1", "part-of", "A-T2"), L("A-T2", "part-of", "B-T1"), L("B-T1", "part-of", "A-T1"), L("A-T1", "requires", "A-T9")]);

        Assert.That(graph.Cycles("part-of").Single(), Is.EqualTo(new[] { "A-T1", "A-T2", "B-T1", "A-T1" }));
        Assert.That(graph.Cycles("requires"), Is.Empty);
        Assert.That(graph.Path("part-of", "A-T1", "B-T1"), Is.EqualTo(new[] { "A-T1", "A-T2", "B-T1" }));
        Assert.That(graph.Relations("A-T1").Select(r => (r.Role, r.Node.Id)),
            Is.EqualTo(new[] { ("parent", "A-T2"), ("child", "B-T1"), ("requires", "A-T9") }));
        Assert.That(graph.Neighbourhood("A-T9", 1).Nodes.Select(n => n.Id), Is.EquivalentTo(new[] { "A-T9", "A-T1" }));
        Assert.That(TicketsDb.ResolveLinkKind("required-by"), Is.EqualTo(("requires", true)));
        Assert.That(TicketsDb.ResolveLinkKind("contains"), Is.EqualTo(("part-of", true)));
        Assert.That(TicketsDb.InverseKind("part-of"), Is.EqualTo("parent-of"));
    }
}
