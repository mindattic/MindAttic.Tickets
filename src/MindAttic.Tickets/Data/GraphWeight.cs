using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// An open ticket's graph weight (M.TIX-T316): how much the rest of the work leans on it, worked out from the node graph, so the
/// queue organises itself. Every count is of open tickets only (a closed dependent counts zero):
/// <list type="bullet">
/// <item><see cref="Unblocks"/>: tickets that wait for it, directly or through others (its required-by closure);</item>
/// <item><see cref="Repos"/>: distinct repos among those other than its own (a cross-project blocker ranks higher); <see cref="Spread"/>
/// counts every distinct repo among them, its own included, for the "why";</item>
/// <item><see cref="Descendants"/>: tickets part of it at any depth (its part-of children closure);</item>
/// <item><see cref="InEffort"/>: whether it's part of an open parent;</item>
/// <item><see cref="Related"/>: open tickets linked to it any other way (related, follow-up-of, supersedes, duplicates, implements).</item>
/// </list>
/// <see cref="Value"/> = <see cref="UnblockWeight"/>·Unblocks + <see cref="RepoWeight"/>·Repos + <see cref="ChildWeight"/>·Descendants
/// + <see cref="ParentWeight"/>·InEffort + <see cref="RelatedWeight"/>·Related. Deterministic: the same graph always gives the same weights.
/// </summary>
public sealed record GraphWeight(string Id, int Unblocks, int Repos, int Descendants, bool InEffort, int Related, int Spread = 0)
{
    public const int UnblockWeight = 10;
    public const int RepoWeight = 20;
    public const int ChildWeight = 3;
    public const int ParentWeight = 2;
    public const int RelatedWeight = 1;

    public static GraphWeight None(string id) => new(id, 0, 0, 0, false, 0);

    public long Value => UnblockWeight * (long)Unblocks + RepoWeight * (long)Repos + ChildWeight * (long)Descendants
                         + (InEffort ? ParentWeight : 0) + RelatedWeight * (long)Related;

    /// <summary>Why, in one line: "unblocks 7 tickets in 3 repos, 2 open children, 1 related"; empty when it weighs nothing.</summary>
    public string Why
    {
        get
        {
            var parts = new List<string>();
            if (Unblocks > 0)
                parts.Add($"unblocks {Plural(Unblocks, "ticket")}" + (Spread > 1 || Repos > 0 ? $" in {Plural(Spread, "repo")}" : ""));
            if (Descendants > 0) parts.Add($"{Descendants} open {(Descendants == 1 ? "child" : "children")}");
            if (InEffort) parts.Add("part of an open parent");
            if (Related > 0) parts.Add($"{Related} related");
            return string.Join(", ", parts);
        }
    }

    private static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    /// <summary>Every open ticket's weight in <paramref name="graph"/>, by ID (any case).</summary>
    public static IReadOnlyDictionary<string, GraphWeight> Of(TicketGraph graph)
    {
        var weights = new Dictionary<string, GraphWeight>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in graph.Tickets.Where(n => n.IsOpen))
        {
            var dependents = graph.Dependents(n.Id).Where(d => d.IsOpen).ToList();
            var spread = dependents.Select(d => d.Repo).Where(r => r is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var repos = spread.Count(r => !string.Equals(r, n.Repo, StringComparison.OrdinalIgnoreCase));
            var descendants = graph.Descendants(n.Id).Count(d => d.IsOpen);
            var inEffort = graph.Parents(n.Id).Any(p => p.IsOpen);
            var related = graph.Out(n.Id).Where(e => e.Kind is not (TicketsDb.RequiresKind or TicketsDb.PartOf)).Select(e => e.To)
                .Concat(graph.In(n.Id).Where(e => e.Kind is not (TicketsDb.RequiresKind or TicketsDb.PartOf)).Select(e => e.From))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(id => graph.Node(id).IsOpen);
            weights[n.Id] = new GraphWeight(n.Id, dependents.Count, repos, descendants, inEffort, related, spread.Count);
        }
        return weights;
    }

    /// <summary>
    /// The queue order (M.TIX-T316): explicit priority first (the author's word always wins), then graph weight, heaviest first;
    /// ties keep the order <paramref name="entries"/> came in (the smallest-estimate rule, then oldest). The Orchestrator orders
    /// its dispatch with this same rule.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> entries, Func<T, long> priority, Func<T, long> weight) =>
        [.. entries.OrderByDescending(priority).ThenByDescending(weight)];
}
