using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>Drawing the node graph (M.TIX-T315): a stable layout with parents above children, and the DOT and Mermaid exports.</summary>
public sealed class GraphLayoutTests
{
    private static (string, Ticket) T(string repo, string id, string status = "open") => (repo, new Ticket(id, "2026-10-01", "feature", status, id.ToLowerInvariant(), "—"));

    private static TicketGraph Sample() => TicketGraph.Build(
        [T("A", "A-T1"), T("A", "A-T2"), T("A", "A-T3"), T("B", "B-T1"), T("B", "B-T2", "closed")],
        [new Link("A-T2", "A-T1", "part-of", null), new Link("A-T3", "A-T1", "part-of", null), new Link("B-T1", "A-T2", "requires", null), new Link("B-T2", "B-T1", "related", null)],
        new Dictionary<string, string> { ["A-T1"] = "Epic one" });

    [Test]
    public void Parents_sit_above_their_children_and_the_layout_is_the_same_every_time()
    {
        var g = Sample();
        var (nodes, edges) = g.Neighbourhood("A-T1", 3);
        var first = GraphLayout.Compute(nodes, edges);
        var again = GraphLayout.Compute(nodes, edges);
        var at = first.Nodes.ToDictionary(n => n.Node.Id, n => (n.X, n.Y));

        Assert.That(at["A-T1"].Y, Is.LessThan(at["A-T2"].Y));
        Assert.That(at["A-T2"].Y, Is.EqualTo(at["A-T3"].Y));
        Assert.That(first.Nodes.Single(n => n.Node.Id == "B-T1").InTree, Is.False);
        Assert.That(again.Nodes.Select(n => (n.Node.Id, n.X, n.Y)), Is.EqualTo(first.Nodes.Select(n => (n.Node.Id, n.X, n.Y))));
        Assert.That(GraphLayout.Size(100), Is.GreaterThan(GraphLayout.Size(0)));
    }

    [Test]
    public void Dot_and_mermaid_carry_every_node_and_edge_styled_by_kind()
    {
        var g = Sample();
        var (nodes, edges) = g.Neighbourhood("A-T1", 3);
        var dot = GraphExport.Dot(nodes, edges, focus: "A-T1");
        Assert.That(dot, Does.StartWith("digraph tickets {").And.Contain("\"A-T1 · Epic one\\nopen\"")
            .And.Contain("\"B-T1\" -> \"A-T2\" [label=\"requires\"]").And.Contain("\"A-T1\" -> \"A-T2\" [style=bold").And.Contain("style=dashed"));
        var mermaid = GraphExport.Mermaid(nodes, edges);
        Assert.That(mermaid, Does.StartWith("graph TD").And.Contain("n_B_T1 -->|requires| n_A_T2").And.Contain("n_A_T1 ==>|contains| n_A_T3").And.Contain("-.-|related|"));
    }
}
