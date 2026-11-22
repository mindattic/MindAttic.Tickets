using System.Globalization;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>What undo restores of one ticket: its status, ref, approval, priority and the session working it.</summary>
public sealed record TicketState(string Id, string Status, string Ref, string Approval, long Priority, string? Login, string? Session);

/// <summary>
/// What one change did (M.TIX-T80), enough for <see cref="TicketsDb.Undo"/> to take it back with compensating changes: each
/// ticket it touched as it was before and after, and the links it added or removed. <see cref="Id"/> is the ticket it was
/// about, <see cref="What"/> says what it was in words. Empty when the change changed nothing.
/// </summary>
public sealed record TicketChange(string Id, string What, IReadOnlyList<(TicketState Before, TicketState After)> States,
    IReadOnlyList<Link> Added, IReadOnlyList<Link> Removed)
{
    public bool IsEmpty => States.Count == 0 && Added.Count == 0 && Removed.Count == 0;
}

public sealed partial class TicketsDb
{
    /// <summary>
    /// Runs <paramref name="work"/> in one write transaction and returns what it changed to the tickets in
    /// <paramref name="ids"/> (the first is the one it's about) and to the links touching that first one, for
    /// <see cref="Undo"/>. Nothing about the work itself changes.
    /// </summary>
    public TicketChange Record(string what, IReadOnlyList<string> ids, Action work)
    {
        if (ids.Count == 0) throw new ArgumentException("Name the ticket the change is about.", nameof(ids));
        TicketChange change = null!;
        InWrite(() =>
        {
            var keys = ids.Select(i => RequireTicket(i).Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var before = States(keys);
            var linksBefore = Links(keys[0]);
            work();
            var after = States(keys);
            var linksAfter = Links(keys[0]);
            change = new TicketChange(keys[0], what,
                [.. keys.Where(k => before[k] != after[k]).Select(k => (before[k], after[k]))],
                [.. linksAfter.Where(l => !linksBefore.Any(b => SameLink(b, l)))],
                [.. linksBefore.Where(l => !linksAfter.Any(a => SameLink(a, l)))]);
        });
        return change;
    }

    /// <summary>
    /// Undoes <paramref name="change"/> (M.TIX-T80) by writing compensating changes, never by deleting history: each ticket
    /// goes back to how it was (a closed one reopens, with its old ref), each recorded in its history as any change is; links
    /// it added are removed and links it removed come back; and the ticket gets an <c>undo</c> event saying what was undone.
    /// Refused, with nothing written, when any of it has changed again since (someone else's change isn't undone with it).
    /// </summary>
    public void Undo(TicketChange change)
    {
        if (change.IsEmpty) return;
        InWrite(() =>
        {
            var keys = change.States.Select(s => s.After.Id).ToList();
            var now = States(keys);
            foreach (var (_, after) in change.States)
                if (now[after.Id] != after)
                    throw new InvalidOperationException($"{after.Id} has changed since \"{change.What}\" ({Differences(after, now[after.Id])}); not undone.");
            var links = AllLinks();
            foreach (var l in change.Added.Where(l => !links.Any(x => SameLink(x, l))))
                throw new InvalidOperationException($"The link {l.From} {l.Kind} {l.To} has been removed since; not undone.");
            foreach (var l in change.Removed.Where(l => links.Any(x => SameLink(x, l))))
                throw new InvalidOperationException($"The link {l.From} {l.Kind} {l.To} is back already; not undone.");

            // Parents the undone closes rolled up (M.TIX-T366): found before anything reopens, reopened with the rest.
            var own = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            var rolledUp = RolledUpAncestors(change.States.Where(s => s.Before.Status == TicketRules.Open && s.After.Status != TicketRules.Open)
                .Select(s => s.After.Id), own);

            foreach (var (before, after) in change.States)
            {
                // Status and ref first: approval is only for open tickets, so a ticket reopens before its approval comes back.
                if (before.Status != after.Status || before.Ref != after.Ref)
                    Exec("UPDATE tickets SET status = $s, ref = $ref WHERE id = $id", null, ("$s", before.Status), ("$ref", before.Ref), ("$id", after.Id));
                SetApprovalCore(after.Id, before.Approval);
                SetPriorityCore(after.Id, before.Priority);
                if (before.Login != after.Login || before.Session != after.Session)
                {
                    var work = Work(after.Id);
                    var restored = work with { Login = before.Login, Session = before.Session, Since = before.Login is null && before.Session is null ? null : work.Since ?? Now() };
                    SaveWork(after.Id, restored);
                    Event(after.Id, "working", Describe(work), Describe(restored));
                }
            }
            foreach (var l in change.Added)
                Exec("DELETE FROM links WHERE from_id = $f COLLATE NOCASE AND to_id = $t COLLATE NOCASE AND kind = $k", null, ("$f", l.From), ("$t", l.To), ("$k", l.Kind));
            foreach (var l in change.Removed) AddLinkCore(l.From, l.To, l.Kind, l.Note);
            foreach (var (parent, oldRef) in rolledUp)
                Exec("UPDATE tickets SET status = $s, ref = $ref WHERE id = $id", null, ("$s", TicketRules.Open), ("$ref", oldRef), ("$id", parent));
            Event(RequireTicket(change.Id).Id, "undo", null, change.What);
        });
    }

    /// <summary>
    /// The ancestors that closing <paramref name="reopening"/> rolled up (M.TIX-T364), nearest first, each with the ref it had
    /// before: a part-of parent still closed with <see cref="ChildrenDoneRef"/> whose latest close was a roll-up triggered by that
    /// child (its <see cref="RollUpEvent"/> names it; a roll-up from before that event existed counts when it closed in the same
    /// second as the child), then that parent's own rolled-up parents, all the way up. A parent closed by hand is never one.
    /// </summary>
    private List<(string Id, string Ref)> RolledUpAncestors(IEnumerable<string> reopening, HashSet<string> skip)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(reopening);
        while (pending.TryDequeue(out var child))
        {
            var childClosedAt = LatestEvent(child, "status") is { New: TicketRules.Closed or TicketRules.WontDo } c ? c.At : null;
            foreach (var to in Rows("SELECT to_id FROM links WHERE from_id = $id COLLATE NOCASE AND kind = 'part-of'", r => r.GetString(0), ("$id", child)))
            {
                if (IsMilestoneId(to) || Ticket(to)?.Ticket is not { } parent || !seen.Add(parent.Id)) continue;
                if (parent.Status == TicketRules.Open || parent.Ref != ChildrenDoneRef) continue;
                if (LatestEvent(parent.Id, "status") is not { } close || close.New != parent.Status) continue;
                var trigger = LatestEvent(parent.Id, RollUpEvent);
                var byThisChild = trigger is { } e && e.Seq > close.Seq
                    ? string.Equals(e.New, child, StringComparison.OrdinalIgnoreCase)
                    : childClosedAt is not null && childClosedAt == close.At;
                if (!byThisChild) continue;
                var refEvent = LatestEvent(parent.Id, "ref");
                var oldRef = refEvent is { } r && r.Seq > close.Seq && r.New == ChildrenDoneRef ? r.Old ?? "" : parent.Ref;
                result.Add((parent.Id, oldRef));
                pending.Enqueue(parent.Id);
            }
        }
        return result;
    }

    /// <summary>A ticket's latest history event of <paramref name="field"/>, or null when it has none.</summary>
    private (long Seq, string At, string? Old, string? New)? LatestEvent(string id, string field) =>
        Rows("SELECT seq, at, old, new FROM ticket_events WHERE seq = (SELECT MAX(seq) FROM ticket_events WHERE ticket_id = $id COLLATE NOCASE AND field = $f)",
            r => ((long Seq, string At, string? Old, string? New)?)(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            ("$id", id), ("$f", field)).FirstOrDefault();

    /// <summary>Each ticket's undoable state, by its ID: one read per table when there are many.</summary>
    private Dictionary<string, TicketState> States(IReadOnlyList<string> ids)
    {
        var result = new Dictionary<string, TicketState>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count <= 3)
        {
            foreach (var id in ids)
            {
                var t = RequireTicket(id);
                var (approval, priority) = QueueState(t.Id);
                var w = Work(t.Id);
                result[id] = new TicketState(t.Id, t.Status, t.Ref, approval, priority, w.Login, w.Session);
            }
            return result;
        }
        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var queue = QueueStates();
        var work = WorkStates();
        foreach (var (_, t) in Tickets().Where(x => wanted.Contains(x.Ticket.Id)))
        {
            var (approval, priority) = queue.TryGetValue(t.Id, out var q) ? q : (Proposed, 0L);
            var w = work.TryGetValue(t.Id, out var found) ? found : TicketWork.None;
            result[t.Id] = new TicketState(t.Id, t.Status, t.Ref, approval, priority, w.Login, w.Session);
        }
        foreach (var id in ids.Where(i => !result.ContainsKey(i))) throw new KeyNotFoundException($"No ticket {id}.");
        return result;
    }

    private static bool SameLink(Link a, Link b) =>
        string.Equals(a.From, b.From, StringComparison.OrdinalIgnoreCase) && string.Equals(a.To, b.To, StringComparison.OrdinalIgnoreCase) && a.Kind == b.Kind;

    private static string Differences(TicketState expected, TicketState now)
    {
        var parts = new List<string>();
        if (expected.Status != now.Status) parts.Add($"status is {now.Status}");
        if (expected.Ref != now.Ref) parts.Add($"ref is {now.Ref}");
        if (expected.Approval != now.Approval) parts.Add($"approval is {now.Approval}");
        if (expected.Priority != now.Priority) parts.Add($"priority is {now.Priority.ToString(CultureInfo.InvariantCulture)}");
        if (expected.Login != now.Login || expected.Session != now.Session) parts.Add("who's working it changed");
        return string.Join(", ", parts);
    }
}
