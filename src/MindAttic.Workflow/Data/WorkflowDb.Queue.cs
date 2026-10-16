using MindAttic.Workflow.Tickets;

namespace MindAttic.Workflow.Data;

/// <summary>An open ticket's place in the work queue: whether it's approved for work, and its priority (higher runs sooner).</summary>
public sealed record QueueEntry(string Repo, Ticket Ticket, string Approval, long Priority);

public sealed partial class WorkflowDb
{
    public const string Proposed = "proposed";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> Approvals = [Proposed, Approved, Rejected];

    /// <summary>Open tickets (optionally for one repo) with approval and priority: highest priority first, then oldest.</summary>
    public IReadOnlyList<QueueEntry> Queue(string? repo = null) =>
        [.. Rows(
            "SELECT t.repo, t.id, t.date, t.type, t.status, t.summary, t.ref, COALESCE(q.approval, 'proposed'), COALESCE(q.priority, 0) " +
            "FROM tickets t LEFT JOIN ticket_queue q ON q.ticket_id = t.id " +
            "WHERE t.status = 'open' AND ($r IS NULL OR t.repo = $r COLLATE NOCASE) " +
            "ORDER BY COALESCE(q.priority, 0) DESC, t.date, t.repo COLLATE NOCASE, t.ordinal",
            r => new QueueEntry(r.GetString(0), new Ticket(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)),
                r.GetString(7), r.GetInt64(8)),
            ("$r", repo))];

    /// <summary>Approval and priority for any ticket, open or not (a ticket never queued is proposed at 0).</summary>
    public (string Approval, long Priority) QueueState(string id) =>
        Rows("SELECT approval, priority FROM ticket_queue WHERE ticket_id = $id", r => (r.GetString(0), r.GetInt64(1)), ("$id", id))
            .DefaultIfEmpty((Proposed, 0L)).First();

    /// <summary>Approves an open ticket for work.</summary>
    public void Approve(string id) => InWrite(() => SetApprovalCore(RequireOpen(id).Id, Approved));

    /// <summary>Puts a ticket back to proposed (neither approved nor rejected).</summary>
    public void Unapprove(string id) => InWrite(() => SetApprovalCore(RequireTicket(id).Id, Proposed));

    /// <summary>Rejects an open ticket: approval becomes rejected and it closes as wont-do with <paramref name="reason"/> as its ref.</summary>
    public Ticket Reject(string id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection needs a reason.", nameof(reason));
        Ticket closed = null!;
        InWrite(() =>
        {
            var t = RequireOpen(id);
            SetApprovalCore(t.Id, Rejected);
            closed = t with { Status = TicketRules.WontDo, Ref = reason.Trim() };
            Exec("UPDATE tickets SET status = $s, ref = $ref WHERE id = $id", null, ("$s", closed.Status), ("$ref", closed.Ref), ("$id", t.Id));
        });
        return closed;
    }

    /// <summary>Sets one ticket's priority (higher runs sooner).</summary>
    public void SetPriority(string id, long priority) => InWrite(() => SetPriorityCore(RequireTicket(id).Id, priority));

    /// <summary>
    /// Orders <paramref name="idsInOrder"/> first-to-last by giving them descending priorities (n … 1), in one transaction;
    /// tickets left out keep theirs. Only tickets whose priority actually changes are written and recorded.
    /// </summary>
    public void Reorder(IReadOnlyList<string> idsInOrder)
    {
        if (idsInOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != idsInOrder.Count)
            throw new ArgumentException("Each ticket may appear only once.", nameof(idsInOrder));
        InWrite(() =>
        {
            for (var i = 0; i < idsInOrder.Count; i++)
                SetPriorityCore(RequireTicket(idsInOrder[i]).Id, idsInOrder.Count - i);
        });
    }

    /// <summary>
    /// Moves <paramref name="id"/> to its place in <paramref name="newOrder"/> (a list as shown, already rearranged) by
    /// writing as few priorities as it can: the ticket goes one above its new lower neighbour (or below its upper one),
    /// then only the neighbours that would tie or invert are nudged, up the list or down it, whichever touches fewer.
    /// Tickets not in the list, and the rest of it, keep their priority, so one drag doesn't hand everything a manual rank.
    /// </summary>
    public void Move(string id, IReadOnlyList<string> newOrder)
    {
        var at = newOrder.ToList().FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (at < 0) throw new ArgumentException($"{id} isn't in the new order.", nameof(newOrder));
        InWrite(() =>
        {
            var ids = newOrder.Select(x => RequireTicket(x).Id).ToList();
            var p = ids.Select(x => QueueState(x).Priority).ToList();
            var up = Placement(p, at, upward: true);
            var down = Placement(p, at, upward: false);
            foreach (var (index, priority) in up.Count <= down.Count ? up : down)
                SetPriorityCore(ids[index], priority);
        });
    }

    /// <summary>The priorities to write (index → value) so item <paramref name="at"/> sits strictly between its neighbours.</summary>
    private static List<(int Index, long Priority)> Placement(List<long> p, int at, bool upward)
    {
        var writes = new List<(int, long)>();
        long mine;
        if (upward)
        {
            mine = at + 1 < p.Count ? p[at + 1] + 1 : at > 0 ? p[at - 1] - 1 : p[at];
            writes.Add((at, mine));
            var needed = mine + 1;
            for (var j = at - 1; j >= 0 && p[j] < needed; j--, needed++) writes.Add((j, needed));
        }
        else
        {
            mine = at > 0 ? p[at - 1] - 1 : at + 1 < p.Count ? p[at + 1] + 1 : p[at];
            writes.Add((at, mine));
            var needed = mine - 1;
            for (var j = at + 1; j < p.Count && p[j] > needed; j++, needed--) writes.Add((j, needed));
        }
        return [.. writes.Where(w => p[w.Item1] != w.Item2)];
    }

    /// <summary>Approval and priority of every ticket that has any (others are proposed at 0).</summary>
    public IReadOnlyDictionary<string, (string Approval, long Priority)> QueueStates() =>
        Rows("SELECT ticket_id, approval, priority FROM ticket_queue", r => (r.GetString(0), r.GetString(1), r.GetInt64(2)))
            .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3), StringComparer.OrdinalIgnoreCase);

    private void SetApprovalCore(string id, string approval)
    {
        var (old, priority) = QueueState(id);
        if (old == approval) return;
        Upsert(id, approval, priority);
        Event(id, "approval", old, approval);
    }

    private void SetPriorityCore(string id, long priority)
    {
        var (approval, old) = QueueState(id);
        if (old == priority) return;
        Upsert(id, approval, priority);
        Event(id, "priority", old.ToString(System.Globalization.CultureInfo.InvariantCulture), priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Upsert(string id, string approval, long priority) =>
        Exec("INSERT INTO ticket_queue (ticket_id, approval, priority, updated) VALUES ($id, $a, $p, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')) " +
             "ON CONFLICT (ticket_id) DO UPDATE SET approval = excluded.approval, priority = excluded.priority, updated = excluded.updated",
            null, ("$id", id), ("$a", approval), ("$p", priority));

    private void Event(string id, string field, string? old, string? value) =>
        Exec("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), $f, $o, $n)",
            null, ("$id", id), ("$f", field), ("$o", old), ("$n", value));

    private Ticket RequireTicket(string id) => Ticket(id)?.Ticket ?? throw new KeyNotFoundException($"No ticket {id}.");

    private Ticket RequireOpen(string id)
    {
        var t = RequireTicket(id);
        return t.Status == TicketRules.Open ? t : throw new InvalidOperationException($"{t.Id} is {t.Status}; only open tickets can be approved or rejected.");
    }

    private void InWrite(Action work)
    {
        Exec("BEGIN IMMEDIATE");
        try
        {
            work();
            Exec("COMMIT");
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }
    }
}
