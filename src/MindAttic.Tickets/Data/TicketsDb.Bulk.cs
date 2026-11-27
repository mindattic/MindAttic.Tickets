using System.Data.Common;
using System.Globalization;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// Reads for many tickets or milestones at once (M.TIX-T413): each is a query or two however many it covers, where asking
/// one at a time is a query (or several) apiece.
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>How many IDs one <c>IN</c> list carries: well under SQL Server's 2,100 parameters.</summary>
    private const int InListSize = 500;

    /// <summary>
    /// The rows <paramref name="sql"/> reads, whose
    /// <c>{0}</c> is a parenthesised list of <paramref name="ids"/> as given and upper-cased, a few hundred at a time.
    /// </summary>
    private List<T> RowsIn<T>(string sql, IEnumerable<string> ids, Func<DbDataReader, T> read)
    {
        var all = new List<T>();
        foreach (var chunk in ids.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(InListSize))
        {
            var args = chunk.Select((x, i) => ("$b" + i.ToString(CultureInfo.InvariantCulture), (object?)x)).ToArray();
            var list = "(" + string.Join(", ", args.Select(a => $"{a.Item1}, UPPER({a.Item1})")) + ")";
            all.AddRange(Rows(string.Format(CultureInfo.InvariantCulture, sql, list), read, args));
        }
        return all;
    }

    /// <summary>The tickets with these IDs (as stored, without following reassignments), by ID in any case.</summary>
    public IReadOnlyDictionary<string, (string Repo, Ticket Ticket)> TicketsById(IEnumerable<string> ids) =>
        RowsIn("SELECT repo, id, date, type, status, summary, ref FROM tickets WHERE id IN {0}", ids, r => (Repo: r.GetString(0), Ticket: ReadTicket(r, 1)))
            .ToDictionary(x => x.Ticket.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The descriptions of these tickets (as stored, without following reassignments), by ID in any case; a ticket
    /// without one isn't there.</summary>
    public IReadOnlyDictionary<string, string> Descriptions(IEnumerable<string> ids) =>
        RowsIn("SELECT ticket_id, body FROM ticket_descriptions WHERE ticket_id IN {0}", ids, r => (Id: r.GetString(0), Body: r.GetString(1)))
            .ToDictionary(x => x.Id, x => x.Body, StringComparer.OrdinalIgnoreCase);

    /// <summary>Each of <paramref name="ids"/> → the ID it's known by now, as <see cref="CurrentId"/> answers, from one read of
    /// the ID history. An ID never reassigned maps to itself.</summary>
    public IReadOnlyDictionary<string, string> CurrentIds(IEnumerable<string> ids)
    {
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (old, @new) in Rows("SELECT old_id, new_id FROM id_history WHERE kind <> 'code' ORDER BY seq", r => (r.GetString(0), r.GetString(1))))
            next[old] = @new; // the newest reassignment of an ID wins, as CurrentId's ORDER BY seq DESC
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            var cur = id;
            while (next.TryGetValue(cur, out var n) && seen.Add(n)) cur = n;
            result[id] = cur;
        }
        return result;
    }

    /// <summary>Every milestone's members, as <see cref="MilestoneMembers"/> reads each, by milestone ID: one read of the
    /// milestones and (without <paramref name="graph"/>) one of the graph.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MilestoneMember>> MilestoneMembersAll(TicketGraph? graph = null)
    {
        var all = Milestones();
        if (all.Count == 0) return new Dictionary<string, IReadOnlyList<MilestoneMember>>(StringComparer.OrdinalIgnoreCase);
        graph ??= Graph();
        return all.ToDictionary(m => m.Id, m => (IReadOnlyList<MilestoneMember>)MembersOf(m.Id, graph), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every milestone's acceptance checks, as <see cref="MilestoneChecks"/> reads each, by milestone ID; a milestone
    /// without any isn't there.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MilestoneCheck>> MilestoneChecksAll() =>
        ReadChecks("ORDER BY milestone_id, number").GroupBy(c => c.MilestoneId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MilestoneCheck>)[.. g], StringComparer.OrdinalIgnoreCase);

    /// <summary>Every milestone's history, oldest first, by milestone ID; a milestone without any isn't there.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MilestoneEvent>> MilestoneHistoryAll() =>
        Rows("SELECT milestone_id, at, field, old, new FROM milestone_events ORDER BY seq",
                r => new MilestoneEvent(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)))
            .GroupBy(e => e.MilestoneId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MilestoneEvent>)[.. g], StringComparer.OrdinalIgnoreCase);

    /// <summary>Every activated milestone's growth, as <see cref="MilestoneGrowth(string, TicketGraph?)"/> reads each, by
    /// milestone ID: the milestones, their history and the pending additions read once each.</summary>
    public IReadOnlyDictionary<string, MilestoneGrowth> MilestoneGrowthAll(TicketGraph? graph = null)
    {
        var activated = Milestones().Where(m => m.Activated is not null).ToList();
        if (activated.Count == 0) return new Dictionary<string, MilestoneGrowth>(StringComparer.OrdinalIgnoreCase);
        graph ??= Graph();
        var history = MilestoneHistoryAll();
        var pending = MilestonePending().ToLookup(p => p.MilestoneId, StringComparer.OrdinalIgnoreCase);
        return activated.ToDictionary(m => m.Id, m => GrowthOf(m, graph, history.GetValueOrDefault(m.Id) ?? [], [.. pending[m.Id]]), StringComparer.OrdinalIgnoreCase);
    }
}
