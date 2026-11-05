using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// One item in a <see cref="TicketGraph"/> (M.TIX-T314): a ticket (with its repo, type, status, summary and friendly name), or an
/// endpoint the database holds as a story or epic, or names nothing (<see cref="IsTicket"/> false, those fields null).
/// </summary>
public sealed record GraphNode(string Id, string? Repo, string? Type, string? Status, string? Summary, string? Name)
{
    public bool IsTicket => Status is not null;
    public bool IsOpen => Status == TicketRules.Open;

    /// <summary>"ID · name" (or its summary cut short).</summary>
    public string Caption => TicketsDb.Caption(Id, Name, Summary);

    /// <summary>"ID · name · status": how <c>ticket show</c> lists a neighbour.</summary>
    public string Line => Caption + " · " + (Status ?? "not a ticket");
}

/// <summary>A stored edge: <see cref="From"/> <see cref="Kind"/> <see cref="To"/> ("A requires B", "A part-of B").</summary>
public sealed record GraphEdge(string From, string To, string Kind, string? Note = null)
{
    /// <summary>The same edge read from its other end: "required-by", "parent-of", …</summary>
    public string Inverse => TicketsDb.InverseKind(Kind);
}

/// <summary>A neighbour of a ticket as read from that ticket's side: its role ("parent", "child", "requires", "required-by",
/// "related", or another kind or its inverse), the neighbour, and the stored edge.</summary>
public sealed record Relation(string Role, GraphNode Node, GraphEdge Edge);

/// <summary>
/// Every ticket and link as one unlimited node graph (M.TIX-T314): parent/child at any depth (<c>part-of</c> is the child → parent
/// edge; an epic is just a ticket with children), prerequisites (<c>requires</c>, read back as <c>required-by</c>), related and the
/// other kinds, across repos. Each edge is stored once with its direction; its inverse is derived (<see cref="TicketsDb.InverseKind"/>).
/// IDs match without case. Built in memory from a snapshot, so a query never goes back to the database.
/// </summary>
public sealed class TicketGraph
{
    private readonly Dictionary<string, GraphNode> nodes;
    private readonly ILookup<string, GraphEdge> outgoing;
    private readonly ILookup<string, GraphEdge> incoming;

    private TicketGraph(Dictionary<string, GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        this.nodes = nodes;
        Edges = edges;
        outgoing = edges.ToLookup(e => e.From, StringComparer.OrdinalIgnoreCase);
        incoming = edges.ToLookup(e => e.To, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Builds the graph from every ticket and link (and friendly names, when given).</summary>
    public static TicketGraph Build(IEnumerable<(string Repo, Ticket Ticket)> tickets, IEnumerable<Link> links, IReadOnlyDictionary<string, string>? names = null)
    {
        var byId = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repo, t) in tickets)
            byId.TryAdd(t.Id, new GraphNode(t.Id, repo, t.Type, t.Status, t.Summary, names?.GetValueOrDefault(t.Id)));
        // Each stored edge once, without case: "BE-T1" and "be-t1" are one ticket.
        var edges = links
            .Select(l => new GraphEdge(byId.TryGetValue(l.From, out var f) ? f.Id : l.From, byId.TryGetValue(l.To, out var t) ? t.Id : l.To, l.Kind, l.Note))
            .DistinctBy(e => (e.From.ToUpperInvariant(), e.To.ToUpperInvariant(), e.Kind))
            .ToList();
        return new TicketGraph(byId, edges);
    }

    /// <summary>Every stored edge.</summary>
    public IReadOnlyList<GraphEdge> Edges { get; }

    /// <summary>Every ticket.</summary>
    public IEnumerable<GraphNode> Tickets => nodes.Values;

    /// <summary>The node for <paramref name="id"/>: its ticket, or a bare node when no ticket has that ID.</summary>
    public GraphNode Node(string id) => nodes.TryGetValue(id, out var n) ? n : new GraphNode(id, null, null, null, null, null);

    public bool Contains(string id) => nodes.ContainsKey(id);

    /// <summary>The edges leaving <paramref name="id"/> (of <paramref name="kind"/>, or every kind).</summary>
    public IEnumerable<GraphEdge> Out(string id, string? kind = null) => outgoing[id].Where(e => kind is null || e.Kind == kind);

    /// <summary>The edges arriving at <paramref name="id"/> (of <paramref name="kind"/>, or every kind).</summary>
    public IEnumerable<GraphEdge> In(string id, string? kind = null) => incoming[id].Where(e => kind is null || e.Kind == kind);

    /// <summary>What it's directly part of.</summary>
    public IReadOnlyList<GraphNode> Parents(string id) => [.. Out(id, TicketsDb.PartOf).Select(e => Node(e.To))];

    /// <summary>What's directly part of it.</summary>
    public IReadOnlyList<GraphNode> Children(string id) => [.. In(id, TicketsDb.PartOf).Select(e => Node(e.From))];

    /// <summary>What it waits for, directly.</summary>
    public IReadOnlyList<GraphNode> Prerequisites(string id) => [.. Out(id, TicketsDb.RequiresKind).Select(e => Node(e.To))];

    /// <summary>What waits for it, directly.</summary>
    public IReadOnlyList<GraphNode> RequiredBy(string id) => [.. In(id, TicketsDb.RequiresKind).Select(e => Node(e.From))];

    /// <summary>Everything below it in the part-of tree, at any depth (each once).</summary>
    public IReadOnlyList<GraphNode> Descendants(string id) => [.. Reach(id, TicketsDb.PartOf, down: true).Select(Node)];

    /// <summary>Everything that waits for it, directly or through others (each once): the required-by closure.</summary>
    public IReadOnlyList<GraphNode> Dependents(string id) => [.. Reach(id, TicketsDb.RequiresKind, down: true).Select(Node)];

    /// <summary>Every ID reachable from <paramref name="id"/> along <paramref name="kind"/> edges, read backwards (To → From) when
    /// <paramref name="down"/>, else forwards; <paramref name="id"/> itself left out.</summary>
    public IReadOnlyList<string> Reach(string id, string kind, bool down)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        var found = new List<string>();
        var next = new Queue<string>([id]);
        while (next.TryDequeue(out var x))
            foreach (var y in down ? In(x, kind).Select(e => e.From) : Out(x, kind).Select(e => e.To))
                if (seen.Add(y)) { found.Add(y); next.Enqueue(y); }
        return found;
    }

    /// <summary>
    /// Every neighbour of <paramref name="id"/>, read from its side: parents, children, what it requires, what requires it,
    /// related, then the other kinds (outgoing by kind, incoming by its inverse), each group by ID.
    /// </summary>
    public IReadOnlyList<Relation> Relations(string id)
    {
        static int Order(string role) => role switch
        {
            "parent" => 0, "child" => 1, "requires" => 2, "required-by" => 3, "related" => 4, _ => 5,
        };
        var list = Out(id).Select(e => new Relation(e.Kind == TicketsDb.PartOf ? "parent" : e.Kind, Node(e.To), e))
            .Concat(In(id).Select(e => new Relation(e.Kind == TicketsDb.PartOf ? "child" : e.Inverse, Node(e.From), e)));
        return [.. list.OrderBy(r => Order(r.Role)).ThenBy(r => r.Role, StringComparer.Ordinal).ThenBy(r => r.Node.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The shortest path from <paramref name="from"/> to <paramref name="to"/> along <paramref name="kind"/> edges
    /// (both ends included), or null when there is none.</summary>
    public IReadOnlyList<string>? Path(string kind, string from, string to)
    {
        var back = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = from };
        var next = new Queue<string>([from]);
        while (next.TryDequeue(out var x))
        {
            if (string.Equals(x, to, StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<string> { x };
                while (!string.Equals(path[^1], from, StringComparison.OrdinalIgnoreCase)) path.Add(back[path[^1]]);
                path.Reverse();
                return path;
            }
            foreach (var e in Out(x, kind))
                if (back.TryAdd(e.To, x)) next.Enqueue(e.To);
        }
        return null;
    }

    /// <summary>
    /// Every cycle along <paramref name="kind"/> edges, one per strongly connected group of two or more items, each as a closed
    /// path (its first ID repeated at its end), smallest first ID first.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Cycles(string kind)
    {
        var cycles = new List<IReadOnlyList<string>>();
        foreach (var group in StronglyConnected(kind).Where(g => g.Count > 1))
        {
            var start = group.Min(StringComparer.OrdinalIgnoreCase)!;
            // Round the group: from start along an edge that stays in it, then the shortest way back.
            var members = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var step = Out(start, kind).Select(e => e.To).First(members.Contains);
            var back = Path(kind, step, start) ?? [step, start];
            cycles.Add([start, .. back]);
        }
        return [.. cycles.OrderBy(c => c[0], StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Tarjan's strongly connected components over <paramref name="kind"/> edges, iteratively (no deep recursion).</summary>
    private List<List<string>> StronglyConnected(string kind)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var groups = new List<List<string>>();
        var counter = 0;
        var all = Edges.Where(e => e.Kind == kind).SelectMany(e => new[] { e.From, e.To }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in all)
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(string Node, IEnumerator<string> Next)>();
            void Open(string v)
            {
                index[v] = low[v] = counter++;
                stack.Push(v);
                onStack.Add(v);
                work.Push((v, Out(v, kind).Select(e => e.To).ToList().GetEnumerator()));
            }
            Open(root);
            while (work.Count > 0)
            {
                var (v, next) = work.Peek();
                if (next.MoveNext())
                {
                    var w = next.Current;
                    if (!index.ContainsKey(w)) Open(w);
                    else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                    continue;
                }
                work.Pop();
                if (work.Count > 0) low[work.Peek().Node] = Math.Min(low[work.Peek().Node], low[v]);
                if (low[v] != index[v]) continue;
                var group = new List<string>();
                string x;
                do { x = stack.Pop(); onStack.Remove(x); group.Add(x); } while (!string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
                groups.Add(group);
            }
        }
        return groups;
    }

    /// <summary>
    /// The items within <paramref name="hops"/> steps of <paramref name="id"/> along any edge, either way (only the edges
    /// <paramref name="keep"/> accepts, when given), and every edge between them: what a drawn neighbourhood shows.
    /// </summary>
    public (IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges) Neighbourhood(string id, int hops, Func<GraphEdge, bool>? keep = null)
    {
        var start = Node(id).Id;
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [start] = 0 };
        var next = new Queue<string>([start]);
        while (next.TryDequeue(out var x))
        {
            if (depth[x] >= hops) continue;
            foreach (var e in Out(x).Concat(In(x)).Where(e => keep is null || keep(e)))
            {
                var y = string.Equals(e.From, x, StringComparison.OrdinalIgnoreCase) ? e.To : e.From;
                if (depth.TryAdd(y, depth[x] + 1)) next.Enqueue(y);
            }
        }
        var edges = Edges.Where(e => depth.ContainsKey(e.From) && depth.ContainsKey(e.To) && (keep is null || keep(e))).ToList();
        return ([.. depth.Keys.Select(Node)], edges);
    }

    /// <summary>A closed ticket that still has open tickets part of it: its parent was closed before its children (each
    /// open child listed).</summary>
    public IReadOnlyList<(GraphNode Parent, IReadOnlyList<GraphNode> OpenChildren)> ClosedParentsWithOpenChildren() =>
        [.. nodes.Values.Where(n => n.IsTicket && !n.IsOpen)
            .Select(n => (Parent: n, OpenChildren: (IReadOnlyList<GraphNode>)[.. Children(n.Id).Where(c => c.IsOpen)]))
            .Where(x => x.OpenChildren.Count > 0)
            .OrderBy(x => x.Parent.Id, StringComparer.OrdinalIgnoreCase)];
}
