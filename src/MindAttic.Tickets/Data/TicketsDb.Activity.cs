using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// One change to one ticket, anywhere in the workspace (M.TIX-T84): a <c>ticket_events</c> row with the ticket's repo and summary.
/// <see cref="Worker"/> is, for a status change, the session recorded as working the ticket at that moment (its
/// <c>working</c> history): who closed it, when an agent did.
/// </summary>
public sealed record ActivityEvent(long Seq, string TicketId, string Repo, string Summary, string At, string Field, string? Old, string? New)
{
    /// <summary>The login and/or session working the ticket when its status changed ("login (session)", a login or a session);
    /// null for other changes, and when nobody was recorded as working it.</summary>
    public string? Worker { get; init; }

    /// <summary>The ticket was closed or closed as wont-do.</summary>
    public bool IsClose => Field == "status" && New is TicketRules.Closed or TicketRules.WontDo;

    /// <summary>The worker's login when it has one, else its session: a short "who".</summary>
    public string? WorkerName => Worker is null ? null
        : Worker.IndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? Worker[..i] : Worker;

    /// <summary>What happened, in a few words ("closed (abc123)", "approval: proposed → approved").</summary>
    public string Text => Field switch
    {
        "created" => $"filed ({New})",
        "status" when IsClose => New == TicketRules.WontDo ? "closed as wont-do" : "closed",
        // A description's history holds whole bodies; the line only says what happened.
        "description" => Old is null ? "description added" : New is null ? "description removed" : "description edited",
        "summary" => $"reworded: {New}",
        "working" => New is null ? $"no longer worked ({Old})" : $"being worked by {New}",
        _ when Old is null => $"{Field}: {New}",
        _ when New is null => $"{Field}: {Old} removed",
        _ => $"{Field}: {Old} → {New}",
    };
}

public sealed partial class TicketsDb
{
    /// <summary>
    /// The workspace's activity feed (M.TIX-T84): every ticket change in every repo after event <paramref name="after"/>, newest
    /// first, at most <paramref name="limit"/>; with <paramref name="repo"/>, only that repo's tickets. A status change carries
    /// the session that was working the ticket then (<see cref="ActivityEvent.Worker"/>). Polling with the last
    /// <see cref="ActivityEvent.Seq"/> seen returns only what's new.
    /// </summary>
    public IReadOnlyList<ActivityEvent> Activity(long after = 0, int limit = 200, string? repo = null)
    {
        if (limit <= 0) return [];
        // The worker at a status change: the newest 'working' row for the ticket before it (MAX, not LIMIT, so the subquery
        // reads the same on SQL Server).
        var sql =
            "SELECT e.seq, e.ticket_id, t.repo, t.summary, e.at, e.field, e.old, e.new, " +
            "CASE WHEN e.field = 'status' THEN (SELECT w.new FROM ticket_events w WHERE w.seq = " +
            "(SELECT MAX(x.seq) FROM ticket_events x WHERE x.ticket_id = e.ticket_id AND x.field = 'working' AND x.seq < e.seq)) END " +
            "FROM ticket_events e JOIN tickets t ON t.id = e.ticket_id " +
            "WHERE e.seq > $after" + (repo is null ? "" : " AND t.repo = $repo COLLATE NOCASE") + " ORDER BY e.seq DESC";
        (string, object?)[] args = repo is null ? [("$after", after)] : [("$after", after), ("$repo", repo.Trim())];
        var list = new List<ActivityEvent>();
        using var cmd = Command(sql, null, args);
        using var r = cmd.ExecuteReader();
        while (list.Count < limit && r.Read())
            list.Add(new ActivityEvent(Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7))
            {
                Worker = r.IsDBNull(8) ? null : r.GetString(8),
            });
        return list;
    }
}
