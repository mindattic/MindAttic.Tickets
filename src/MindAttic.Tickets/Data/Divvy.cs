namespace MindAttic.Tickets.Data;

/// <summary>An open ticket as <see cref="Divvy"/> sees it: its estimate (null: none), who it's assigned to and whether a session
/// is working it now.</summary>
public sealed record DivvyTicket(string Id, long? Estimate, string? Assignee, bool Working);

/// <summary>One assignment a divvy makes: <paramref name="TicketId"/> from <paramref name="From"/> (null: nobody) to
/// <paramref name="To"/>, carrying <paramref name="Weight"/> tokens of estimated work.</summary>
public sealed record DivvyMove(string TicketId, string? From, string To, long Weight);

/// <summary>Each member's share after the divvy: how many open tickets and how many estimated tokens they hold.</summary>
public sealed record DivvyShare(string Member, int Tickets, long Weight);

/// <summary>What a divvy changes (<see cref="Moves"/>) and where every member stands afterwards (<see cref="Shares"/>), with the
/// tickets it left alone because they're someone else's or already being worked.</summary>
public sealed record DivvyPlan(IReadOnlyList<DivvyMove> Moves, IReadOnlyList<DivvyShare> Shares, IReadOnlyList<string> LeftAlone);

/// <summary>
/// Splits a repo's open tickets between members' logins (M.TIX-T326) so two (or more) people can work one repo side by side
/// without taking the same ticket: each ticket is assigned to one member, and the Orchestrator then dispatches it only on that
/// member's login (M.ORCH-T668). Pure: the database half is <see cref="TicketsDb.Divvy"/>.
/// <list type="bullet">
/// <item>Shares are balanced by estimated tokens (a ticket without an estimate weighs the median of those with one, or 1 when
/// none has one), largest work first, each to whoever holds least so far; ties go to the member named first.</item>
/// <item>Tickets joined by <c>requires</c> links travel together, so one member works a prerequisite chain in order instead of
/// waiting on the other's half of it. A chain one member already holds part of goes to them.</item>
/// <item>A ticket being worked right now, or pinned to someone who isn't one of the members, is never moved.</item>
/// <item>Without <c>rebalance</c> only unassigned tickets are handed out. With it, unstarted tickets the members already hold
/// may move too, but only as many as it takes to even the shares: a chain moves from the fullest share to the emptiest while
/// that narrows the gap, so whoever ran dry picks up the other's unstarted work and nothing else is shuffled.</item>
/// </list>
/// </summary>
public static class Divvy
{
    public static DivvyPlan Plan(IReadOnlyList<DivvyTicket> open, IEnumerable<(string From, string To)> requires, IReadOnlyList<string> members, bool rebalance = false)
    {
        var who = members.Select(m => TicketsDb.NormalizeEmail(m) ?? throw new ArgumentException($"\"{m}\" isn't an email address.", nameof(members)))
            .Distinct().ToList();
        if (who.Count == 0) throw new ArgumentException("Name at least one member to divvy the tickets between.", nameof(members));
        bool IsMember(string? e) => e is not null && who.Contains(e);

        var known = open.Where(t => t.Estimate is > 0).Select(t => t.Estimate!.Value).Order().ToList();
        var fallback = known.Count == 0 ? 1 : known[known.Count / 2];
        long Weight(DivvyTicket t) => t.Estimate is > 0 ? t.Estimate.Value : fallback;

        // Chains: tickets joined by requires links (either way) within the open set, found with union-find.
        var byId = open.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var parent = open.ToDictionary(t => t.Id, t => t.Id, StringComparer.OrdinalIgnoreCase);
        string Root(string x) { while (!string.Equals(parent[x], x, StringComparison.OrdinalIgnoreCase)) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (from, to) in requires)
            if (parent.ContainsKey(from) && parent.ContainsKey(to) && Root(from) is var a && Root(to) is var b && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                parent[b] = a;

        var load = who.ToDictionary(m => m, _ => 0L);
        var count = who.ToDictionary(m => m, _ => 0);
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // final assignee of every ticket a member holds
        var leftAlone = new List<string>();
        var chains = new List<Chain>();

        foreach (var group in open.GroupBy(t => Root(t.Id), StringComparer.OrdinalIgnoreCase))
        {
            var tickets = group.OrderBy(t => t.Id, IdOrder.Instance).ToList();
            // Pinned in place: being worked, or someone else's. They still count towards a member's share when theirs.
            var fixedHere = tickets.Where(t => t.Working || (t.Assignee is not null && !IsMember(t.Assignee))).ToList();
            foreach (var t in fixedHere)
            {
                leftAlone.Add(t.Id);
                if (IsMember(t.Assignee)) { load[t.Assignee!] += Weight(t); count[t.Assignee!]++; }
            }
            var free = tickets.Except(fixedHere).ToList();
            if (free.Count == 0) continue;
            // The member who already holds most of this chain (by weight, fixed tickets included), if any.
            var holder = tickets.Where(t => IsMember(t.Assignee)).GroupBy(t => t.Assignee!)
                .OrderByDescending(g => g.Sum(Weight)).ThenBy(g => who.IndexOf(g.Key)).Select(g => g.Key).FirstOrDefault();
            var anchored = fixedHere.Any(t => IsMember(t.Assignee)); // part of it is being worked by a member: it can't move
            chains.Add(new Chain(tickets[0].Id, free, free.Sum(Weight), holder, anchored));
        }

        void Give(Chain c, string m) { c.To = m; load[m] += c.Weight; count[m] += c.Tickets.Count; }

        // Chains a member already holds stay with them first (all of them without rebalance; with it, they may move below).
        foreach (var c in chains.Where(c => c.Holder is not null)) Give(c, c.Holder!);
        // Then the unassigned ones, largest first, each to the lightest share.
        foreach (var c in chains.Where(c => c.Holder is null).OrderByDescending(c => c.Weight).ThenBy(c => c.First, IdOrder.Instance))
            Give(c, who.OrderBy(m => load[m]).ThenBy(who.IndexOf).First());

        if (rebalance && who.Count > 1)
            while (true)
            {
                var heavy = who.OrderByDescending(m => load[m]).ThenBy(who.IndexOf).First();
                var light = who.OrderBy(m => load[m]).ThenBy(who.IndexOf).First();
                var gap = load[heavy] - load[light];
                // The biggest movable chain that still narrows the gap (moving w closes it to |gap - 2w|, better while w < gap).
                var pick = chains.Where(c => c.To == heavy && !c.Anchored && c.Weight < gap)
                    .OrderByDescending(c => c.Weight).ThenBy(c => c.First, IdOrder.Instance).FirstOrDefault();
                if (pick is null) break;
                load[heavy] -= pick.Weight; count[heavy] -= pick.Tickets.Count;
                Give(pick, light);
            }

        var moves = new List<DivvyMove>();
        foreach (var c in chains)
            foreach (var t in c.Tickets)
                if (t.Assignee != c.To) moves.Add(new DivvyMove(t.Id, t.Assignee, c.To!, Weight(t)));
        moves.Sort((x, y) => IdOrder.Instance.Compare(x.TicketId, y.TicketId));
        leftAlone.Sort(IdOrder.Instance);
        return new DivvyPlan(moves, [.. who.Select(m => new DivvyShare(m, count[m], load[m]))], leftAlone);
    }

    private sealed class Chain(string first, List<DivvyTicket> tickets, long weight, string? holder, bool anchored)
    {
        public string First { get; } = first;
        public List<DivvyTicket> Tickets { get; } = tickets;
        public long Weight { get; } = weight;
        public string? Holder { get; } = holder;
        public bool Anchored { get; } = anchored;
        public string? To { get; set; }
    }

    /// <summary>Ticket IDs in their natural order: prefix, then the number after the last 'T' numerically (US-T9 before US-T10).</summary>
    private sealed class IdOrder : IComparer<string>
    {
        public static readonly IdOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var (px, nx) = Split(x ?? "");
            var (py, ny) = Split(y ?? "");
            var c = string.Compare(px, py, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : nx != ny ? nx.CompareTo(ny) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static (string, long) Split(string id)
        {
            var i = id.Length;
            while (i > 0 && char.IsDigit(id[i - 1])) i--;
            return i < id.Length && long.TryParse(id[i..], out var n) ? (id[..i], n) : (id, -1);
        }
    }
}

public sealed partial class TicketsDb
{
    /// <summary>
    /// Divvies <paramref name="repo"/>'s open tickets between <paramref name="members"/> (see <see cref="Data.Divvy"/>) and, unless
    /// <paramref name="dryRun"/>, assigns them: each move is an ordinary assignment, recorded in the ticket's history.
    /// </summary>
    public DivvyPlan Divvy(string repo, IReadOnlyList<string> members, bool rebalance = false, bool dryRun = false)
    {
        var work = WorkStates();
        var open = QueueRows(repo).Select(q =>
        {
            var w = work.GetValueOrDefault(q.Ticket.Id);
            return new DivvyTicket(q.Ticket.Id, q.Estimate, w?.Assignee, w?.IsWorking == true);
        }).ToList();
        var requires = AllLinks().Where(l => l.Kind == "requires").Select(l => (l.From, l.To));
        var plan = Data.Divvy.Plan(open, requires, members, rebalance);
        if (!dryRun)
            foreach (var m in plan.Moves) Assign(m.TicketId, m.To);
        return plan;
    }
}
