using System.Globalization;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>One ticket changed since a <see cref="TicketsDb.ChangedTicketsSince"/> mark: its row and queue state as
/// they are now, its history (oldest first) and its hygiene findings (judged within its own repo, since a finding
/// can depend on a later ticket there).</summary>
public sealed record ChangedTicketRow(string Repo, Ticket Ticket, string Approval, long Priority, IReadOnlyList<TicketEvent> History)
{
    /// <summary>Its effort estimate in tokens (M.TIX-T75), or null while it has none.</summary>
    public long? Estimate { get; init; }

    /// <summary>Its labels, sorted (M.TIX-T74).</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Its short friendly name (M.TIX-T313), or null while it has none.</summary>
    public string? Name { get; init; }

    /// <summary>A session is recorded as working it in the ticket database (<c>ticket_work</c>; M.TIX-T134).</summary>
    public bool Working { get; init; }

    /// <summary>Findings against it (<see cref="TicketHygiene"/>); empty while it has none.</summary>
    public IReadOnlyList<HygieneFinding> Hygiene { get; init; } = [];
}

/// <summary>What changed since event <paramref name="After"/> was read (<see cref="TicketsDb.ChangedTicketsSince"/>):
/// every changed ticket, and the highest event seen, to watch for next time.</summary>
public sealed record ChangedTicketsResult(long After, IReadOnlyList<ChangedTicketRow> Tickets);

public sealed partial class TicketsDb
{
    /// <summary>
    /// The highest <c>ticket_events</c> sequence number recorded so far (0 when there are none): the mark a full read
    /// should keep, so its first <see cref="ChangedTicketsSince"/> call starts from where it read, not from the
    /// beginning of every ticket's own history (M.TIX-T370).
    /// </summary>
    public long LatestEventSeq() => Scalar("SELECT MAX(seq) FROM ticket_events") is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0L;

    /// <summary>
    /// Every ticket that changed anywhere in the workspace after event <paramref name="after"/> (<c>ticket_events</c>;
    /// M.TIX-T369), each with its current row, queue state, history and hygiene findings — enough for the desktop view's
    /// incremental refresh (M.TIX-T235) to bring just those rows up to date instead of reloading the whole
    /// <see cref="TicketsDb.Tickets"/> table, every history and every repo's hygiene on each change. Hygiene is judged
    /// within each changed ticket's own repo (a finding can depend on a later ticket there saying it came back), not
    /// just the changed row. Polling with the returned <see cref="ChangedTicketsResult.After"/> returns only what's
    /// new. Unlike <see cref="ChangedSince"/> (SQL Server change tracking by version, minimal rows), this works on
    /// SQLite too and answers from any mark ticket_events still holds, at the cost of reading the full row.
    /// </summary>
    public ChangedTicketsResult ChangedTicketsSince(long after)
    {
        var newest = Scalar("SELECT MAX(seq) FROM ticket_events");
        var maxSeq = newest is null ? after : Math.Max(after, Convert.ToInt64(newest, CultureInfo.InvariantCulture));

        var ids = Rows("SELECT DISTINCT ticket_id FROM ticket_events WHERE seq > $after", r => r.GetString(0), ("$after", after));
        if (ids.Count == 0) return new ChangedTicketsResult(maxSeq, []);

        var found = new List<(string Repo, Ticket Ticket)>();
        foreach (var id in ids)
            if (Ticket(id) is { } t) found.Add(t);
        if (found.Count == 0) return new ChangedTicketsResult(maxSeq, []);

        var states = QueueStates();
        var estimates = Estimates();
        var labels = AllLabels();
        var names = Names();
        var work = WorkStates();
        var hygieneByRepo = new Dictionary<string, IReadOnlyList<HygieneFinding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in found.Select(f => f.Repo).Distinct(StringComparer.OrdinalIgnoreCase))
            hygieneByRepo[repo] = TicketHygiene.Check(repo, [.. Tickets(repo).Select(x => x.Ticket)]);

        var rows = found.Select(f =>
        {
            var (approval, priority) = states.TryGetValue(f.Ticket.Id, out var s) ? s : (Proposed, 0L);
            return new ChangedTicketRow(f.Repo, f.Ticket, approval, priority, History(f.Ticket.Id))
            {
                Estimate = estimates.TryGetValue(f.Ticket.Id, out var e) ? e : null,
                Labels = labels.TryGetValue(f.Ticket.Id, out var l) ? l : [],
                Name = names.GetValueOrDefault(f.Ticket.Id),
                Working = work.TryGetValue(f.Ticket.Id, out var w) && w.IsWorking,
                Hygiene = [.. hygieneByRepo.GetValueOrDefault(f.Repo, []).Where(h => h.TicketId.Equals(f.Ticket.Id, StringComparison.OrdinalIgnoreCase))],
            };
        }).ToList();
        return new ChangedTicketsResult(maxSeq, rows);
    }
}
