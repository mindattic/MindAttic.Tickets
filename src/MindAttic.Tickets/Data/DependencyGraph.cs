using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// One ticket in a <see cref="DependencyGraph"/>, as reached from the graph's ticket. <see cref="Depth"/> counts the
/// <c>requires</c> steps from it (1 is a direct link). <see cref="Blocking"/>: it holds work up now, either the graph's
/// ticket waits for it while both are open (upstream), or it waits for the graph's open ticket (downstream).
/// <see cref="Repeat"/> marks a ticket already listed through another path (not expanded again); <see cref="Cycle"/> one
/// that leads back round to a ticket on its own path. <see cref="Summary"/> and <see cref="Status"/> are null when the
/// link names no ticket the database holds (a story, or a dangling ID).
/// </summary>
public sealed record DependencyNode(string Id, string? Summary, string? Status, int Depth, bool Blocking, bool Repeat = false, bool Cycle = false)
{
    public bool Known => Status is not null;
    public bool IsOpen => Status == TicketRules.Open;

    /// <summary>Its state in words: "open", "closed", "unknown", with "blocks it" / "blocked by it" when it holds work up.</summary>
    public string StateText(bool upstream) =>
        (Status ?? "not a ticket") + (Blocking ? upstream ? ", blocking" : ", blocked by it" : "");

    /// <summary>One line for a text list: "M.TIX-T5, open, blocking: summary", with a repeat or cycle said in words.</summary>
    public string Text(bool upstream) =>
        $"{Id}, {StateText(upstream)}" + (Summary is { Length: > 0 } s ? $": {s}" : "")
        + (Cycle ? " (cycle: leads back round)" : Repeat ? " (listed above)" : "");
}

/// <summary>
/// A ticket's <c>requires</c> chains both ways (M.TIX-T83): what it waits for, all the way down (<see cref="Upstream"/>),
/// and what waits for it, all the way up (<see cref="Downstream"/>), each in depth-first order so a text list reads as an
/// indented tree, with what holds work up now marked. IDs match without case, as everywhere else in the database.
/// </summary>
public sealed class DependencyGraph
{
    private DependencyGraph(string id, Ticket? ticket, IReadOnlyList<DependencyNode> upstream, IReadOnlyList<DependencyNode> downstream)
    {
        Id = id;
        Ticket = ticket;
        Upstream = upstream;
        Downstream = downstream;
    }

    /// <summary>An empty graph: nothing waits either way.</summary>
    public static DependencyGraph Empty(string id) => new(id, null, [], []);

    public string Id { get; }

    /// <summary>The graph's ticket, or null when the database doesn't hold it.</summary>
    public Ticket? Ticket { get; }

    public bool IsOpen => Ticket?.Status == TicketRules.Open;

    /// <summary>What it waits for, depth first.</summary>
    public IReadOnlyList<DependencyNode> Upstream { get; }

    /// <summary>What waits for it, depth first.</summary>
    public IReadOnlyList<DependencyNode> Downstream { get; }

    public bool IsEmpty => Upstream.Count == 0 && Downstream.Count == 0;

    /// <summary>The open tickets it waits for, directly or through others (empty when it's closed): what blocks it.</summary>
    public IReadOnlyList<string> Blockers => [.. Upstream.Where(n => n.Blocking && !n.Repeat && !n.Cycle).Select(n => n.Id)];

    /// <summary>The open tickets that wait for it while it's open, directly or through others.</summary>
    public IReadOnlyList<string> Blocked => [.. Downstream.Where(n => n.Blocking && !n.Repeat && !n.Cycle).Select(n => n.Id)];

    /// <summary>
    /// Each distinct ticket on a side, grouped by how many steps away it is (the fewest), nearest first: the layers a
    /// drawn graph puts in rows.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DependencyNode>> Layers(IEnumerable<DependencyNode> side)
    {
        var nearest = new Dictionary<string, DependencyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in side.Where(n => !n.Repeat && !n.Cycle))
            if (!nearest.TryGetValue(n.Id, out var had) || n.Depth < had.Depth) nearest[n.Id] = n;
        return [.. nearest.Values.GroupBy(n => n.Depth).OrderBy(g => g.Key).Select(g => (IReadOnlyList<DependencyNode>)[.. g])];
    }

    /// <summary>Builds the graph around <paramref name="id"/> from every ticket and link (only <c>requires</c> links count).</summary>
    public static DependencyGraph Build(string id, IEnumerable<Ticket> tickets, IEnumerable<Link> links)
    {
        var byId = new Dictionary<string, Ticket>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tickets) byId.TryAdd(t.Id, t);
        var requires = links.Where(l => l.Kind == "requires").ToList();
        var down = requires.ToLookup(l => l.From, l => l.To, StringComparer.OrdinalIgnoreCase);
        var up = requires.ToLookup(l => l.To, l => l.From, StringComparer.OrdinalIgnoreCase);
        var root = byId.GetValueOrDefault(id);
        var rootId = root?.Id ?? id;
        var rootOpen = root?.Status == TicketRules.Open;
        return new DependencyGraph(rootId, root,
            Walk(rootId, down, byId, rootOpen),
            Walk(rootId, up, byId, rootOpen));
    }

    /// <summary>Depth first from <paramref name="start"/>: each ticket expanded once, a repeat or a cycle listed but not followed.</summary>
    private static List<DependencyNode> Walk(string start, ILookup<string, string> edges, Dictionary<string, Ticket> byId, bool rootOpen)
    {
        var result = new List<DependencyNode>();
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };

        void Visit(string from, int depth)
        {
            foreach (var to in edges[from].Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var t = byId.GetValueOrDefault(to);
                var blocking = rootOpen && t?.Status == TicketRules.Open;
                var id = t?.Id ?? to;
                // A cycle back to a ticket already on the path says nothing new about what blocks: listed, not marked.
                if (path.Contains(id)) { result.Add(new DependencyNode(id, t?.Summary, t?.Status, depth, Blocking: false, Cycle: true)); continue; }
                if (!expanded.Add(id)) { result.Add(new DependencyNode(id, t?.Summary, t?.Status, depth, blocking, Repeat: true)); continue; }
                result.Add(new DependencyNode(id, t?.Summary, t?.Status, depth, blocking));
                path.Add(id);
                Visit(id, depth + 1);
                path.Remove(id);
            }
        }

        Visit(start, 1);
        return result;
    }

    /// <summary>
    /// The graph as plain text, one line per ticket, indented two spaces a step: the text alternative to a drawn graph,
    /// what <c>tickets ticket graph</c> prints and what a screen reader reads.
    /// </summary>
    public IReadOnlyList<string> TextLines()
    {
        var lines = new List<string>
        {
            $"{Id}, {Ticket?.Status ?? "not a ticket"}" + (Ticket is { } t ? $": {t.Summary}" : ""),
            Upstream.Count == 0 ? "Waits for nothing."
                : $"Waits for ({Plural(Blockers.Count, "open blocker")}):",
        };
        lines.AddRange(Upstream.Select(n => new string(' ', n.Depth * 2) + n.Text(upstream: true)));
        lines.Add(Downstream.Count == 0 ? "Nothing waits for it."
            : $"Needed by ({Blocked.Count} blocked by it):");
        lines.AddRange(Downstream.Select(n => new string(' ', n.Depth * 2) + n.Text(upstream: false)));
        return lines;
    }

    private static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}
