using System.Globalization;
using System.Text;

namespace MindAttic.Tickets.Data;

/// <summary>A node placed for drawing (M.TIX-T315): its centre, its size (from its graph weight) and whether it sits in a part-of tree.</summary>
public sealed record LaidNode(GraphNode Node, double X, double Y, double Size, long Weight, bool InTree);

/// <summary>An edge placed for drawing, from its source's centre to its target's.</summary>
public sealed record LaidEdge(GraphEdge Edge, double X1, double Y1, double X2, double Y2);

/// <summary>A drawn graph: its nodes and edges and the area they take.</summary>
public sealed record GraphPicture(IReadOnlyList<LaidNode> Nodes, IReadOnlyList<LaidEdge> Edges, double Width, double Height);

/// <summary>
/// Lays a ticket neighbourhood out for drawing (M.TIX-T315): part-of trees hierarchically (parents above their children, rows by
/// depth, each row ordered by its parents' places), everything else force-directed beside them. Deterministic, seeded by each
/// ticket's ID rather than at random, so the same graph lands the same way on every refresh. Node size grows with graph weight,
/// so hubs stand out.
/// </summary>
public static class GraphLayout
{
    public const double RowGap = 120, ColumnGap = 210, Margin = 80, MinSize = 30, MaxSize = 76;

    /// <summary>A node's drawn size for <paramref name="weight"/>: <see cref="MinSize"/> at 0, growing with its log, capped.</summary>
    public static double Size(long weight) => Math.Min(MaxSize, MinSize + 7 * Math.Log2(1 + Math.Max(0, weight)));

    public static GraphPicture Compute(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, IReadOnlyDictionary<string, GraphWeight>? weights = null)
    {
        var ids = nodes.Select(n => n.Id).ToList();
        var known = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        edges = [.. edges.Where(e => known.Contains(e.From) && known.Contains(e.To))];
        var pos = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);

        // Part-of trees, hierarchically: a row per depth (the longest way down from a root), parents above children.
        var partOf = edges.Where(e => e.Kind == TicketsDb.PartOf).ToList();
        var tree = partOf.SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parentsOf = partOf.ToLookup(e => e.From, e => e.To, StringComparer.OrdinalIgnoreCase);
        var depth = tree.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < tree.Count; pass++) // longest path; bounded by the node count, so a cycle can't loop forever
        {
            var moved = false;
            foreach (var e in partOf)
                if (depth[e.From] < depth[e.To] + 1 && depth[e.To] + 1 < tree.Count + 1) { depth[e.From] = depth[e.To] + 1; moved = true; }
            if (!moved) break;
        }
        var rows = depth.GroupBy(d => d.Value).OrderBy(g => g.Key)
            .Select(g => g.Select(d => d.Key).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList()).ToList();
        var place = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // Each row ordered by where its parents sit (their mean place), ties by ID: children gather under their parents.
            var ordered = row.OrderBy(id => parentsOf[id].Where(place.ContainsKey).Select(p => place[p]).DefaultIfEmpty(double.MaxValue).Average())
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < ordered.Count; i++) place[ordered[i]] = i;
        }
        var widest = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        foreach (var row in rows)
        {
            var offset = (widest - row.Count) / 2.0;
            foreach (var id in row) pos[id] = (Margin + (place[id] + offset) * ColumnGap, Margin + depth[id] * RowGap);
        }
        var treeWidth = widest * ColumnGap;

        // The rest, force-directed beside the trees: seeded from each ID, a fixed number of steps, trees held still as anchors.
        var free = ids.Where(id => !tree.Contains(id)).ToList();
        if (free.Count > 0)
        {
            var side = Math.Max(1, Math.Ceiling(Math.Sqrt(free.Count)));
            var left = Margin + treeWidth + (tree.Count > 0 ? ColumnGap : 0);
            var box = side * ColumnGap;
            foreach (var id in free)
            {
                var h = Hash(id);
                pos[id] = (left + (h % 10_000) / 10_000.0 * box, Margin + (h / 10_000 % 10_000) / 10_000.0 * box);
            }
            var k = ColumnGap * 0.9;
            for (var step = 0; step < 150; step++)
            {
                var temperature = k * (1 - step / 150.0) * 0.5 + 1;
                var push = free.ToDictionary(id => id, _ => (X: 0.0, Y: 0.0), StringComparer.OrdinalIgnoreCase);
                foreach (var a in free)
                    foreach (var b in ids)
                    {
                        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
                        var (dx, dy) = (pos[a].X - pos[b].X, pos[a].Y - pos[b].Y);
                        var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                        var f = k * k / d;
                        push[a] = (push[a].X + dx / d * f, push[a].Y + dy / d * f);
                    }
                foreach (var e in edges)
                {
                    var (dx, dy) = (pos[e.From].X - pos[e.To].X, pos[e.From].Y - pos[e.To].Y);
                    var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                    var f = d * d / k;
                    if (push.ContainsKey(e.From)) push[e.From] = (push[e.From].X - dx / d * f, push[e.From].Y - dy / d * f);
                    if (push.ContainsKey(e.To)) push[e.To] = (push[e.To].X + dx / d * f, push[e.To].Y + dy / d * f);
                }
                foreach (var id in free)
                {
                    var (px, py) = push[id];
                    var len = Math.Max(1, Math.Sqrt(px * px + py * py));
                    var move = Math.Min(len, temperature);
                    pos[id] = (pos[id].X + px / len * move, pos[id].Y + py / len * move);
                }
            }
        }

        // Shift everything so the top-left node sits at the margin.
        if (pos.Count > 0)
        {
            var (minX, minY) = (pos.Values.Min(p => p.X), pos.Values.Min(p => p.Y));
            foreach (var id in ids) pos[id] = (pos[id].X - minX + Margin, pos[id].Y - minY + Margin);
        }
        long WeightOf(string id) => weights is not null && weights.TryGetValue(id, out var w) ? w.Value : 0;
        var laid = nodes.Select(n => new LaidNode(n, pos[n.Id].X, pos[n.Id].Y, Size(WeightOf(n.Id)), WeightOf(n.Id), tree.Contains(n.Id))).ToList();
        var lines = edges.Select(e => new LaidEdge(e, pos[e.From].X, pos[e.From].Y, pos[e.To].X, pos[e.To].Y)).ToList();
        var width = pos.Count == 0 ? 2 * Margin : pos.Values.Max(p => p.X) + Margin + ColumnGap / 2;
        var height = pos.Count == 0 ? 2 * Margin : pos.Values.Max(p => p.Y) + Margin;
        return new GraphPicture(laid, lines, width, height);
    }

    /// <summary>A stable hash of an ID (FNV-1a over its upper-case form): the same in every process, unlike string.GetHashCode.</summary>
    internal static long Hash(string id)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in id.ToUpperInvariant()) { h ^= c; h *= 16777619; }
            return h;
        }
    }

    /// <summary>A repo's colour, the same every time (picked from a fixed palette by its name's hash).</summary>
    public static string RepoColour(string? repo) => repo is null ? "#9E9E9E" : Palette[(int)(Hash(repo) % Palette.Length)];

    private static readonly string[] Palette =
        ["#4E79A7", "#F28E2B", "#59A14F", "#E15759", "#76B7B2", "#EDC948", "#B07AA1", "#FF9DA7", "#9C755F", "#3D9970", "#7F7FDB", "#D4A017"];
}

/// <summary>A neighbourhood written out for other tools (M.TIX-T315): Graphviz DOT and Mermaid.</summary>
public static class GraphExport
{
    /// <summary>Graphviz DOT: nodes labelled "ID · name", filled by repo, outlined by status; requires a solid arrow, part-of a
    /// bold parent → child tree edge, related dashed, the rest dotted and labelled.</summary>
    public static string Dot(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, IReadOnlyDictionary<string, GraphWeight>? weights = null, string? focus = null)
    {
        var sb = new StringBuilder("digraph tickets {\n  rankdir=TB;\n  node [shape=box, style=\"rounded,filled\", fontname=\"Segoe UI\"];\n");
        foreach (var n in nodes)
        {
            var w = weights is not null && weights.TryGetValue(n.Id, out var gw) ? gw.Value : 0;
            sb.Append("  ").Append(Q(n.Id)).Append(" [label=").Append(Q(n.Caption + (n.Status is { } s ? $"\n{s}" : "")))
              .Append(", fillcolor=").Append(Q(GraphLayout.RepoColour(n.Repo) + "55"))
              .Append(", color=").Append(Q(n.IsOpen ? "#B25E00" : n.IsTicket ? "#2E7D32" : "#9E9E9E"))
              .Append(", penwidth=").Append((1 + Math.Log2(1 + w) / 2).ToString("0.##", CultureInfo.InvariantCulture))
              .Append(string.Equals(n.Id, focus, StringComparison.OrdinalIgnoreCase) ? ", peripheries=2" : "")
              .Append(n.Status is null ? ", style=\"rounded,dashed\"" : n.IsOpen ? "" : ", fontcolor=\"#6B6B66\"")
              .Append("];\n");
        }
        foreach (var e in edges)
            sb.Append("  ").Append(e.Kind switch
            {
                TicketsDb.RequiresKind => $"{Q(e.From)} -> {Q(e.To)} [label=\"requires\"]",
                TicketsDb.PartOf => $"{Q(e.To)} -> {Q(e.From)} [style=bold, arrowhead=none, arrowtail=odiamond, dir=both]",
                "related" => $"{Q(e.From)} -> {Q(e.To)} [style=dashed, dir=none, label=\"related\"]",
                _ => $"{Q(e.From)} -> {Q(e.To)} [style=dotted, label={Q(e.Kind)}]",
            }).Append(";\n");
        return sb.Append("}\n").ToString();
    }

    /// <summary>Mermaid flowchart: the same nodes and edge styles (requires -->, part-of parent ==> child, related -.-, others -.->).</summary>
    public static string Mermaid(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        static string Key(string id) => "n_" + new string([.. id.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        static string Text(string s) => s.Replace("\"", "#quot;", StringComparison.Ordinal);
        var sb = new StringBuilder("graph TD\n");
        foreach (var n in nodes)
            sb.Append("  ").Append(Key(n.Id)).Append("[\"").Append(Text(n.Caption)).Append(n.Status is { } s ? $"<br/>{s}" : "").Append("\"]:::")
              .Append(n.IsOpen ? "open" : n.IsTicket ? "closed" : "other").Append('\n');
        foreach (var e in edges)
            sb.Append("  ").Append(e.Kind switch
            {
                TicketsDb.RequiresKind => $"{Key(e.From)} -->|requires| {Key(e.To)}",
                TicketsDb.PartOf => $"{Key(e.To)} ==>|contains| {Key(e.From)}",
                "related" => $"{Key(e.From)} -.-|related| {Key(e.To)}",
                _ => $"{Key(e.From)} -.->|{e.Kind}| {Key(e.To)}",
            }).Append('\n');
        sb.Append("  classDef open stroke:#B25E00,stroke-width:2px;\n  classDef closed stroke:#2E7D32,color:#6B6B66;\n  classDef other stroke-dasharray:4 3;\n");
        return sb.ToString();
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
