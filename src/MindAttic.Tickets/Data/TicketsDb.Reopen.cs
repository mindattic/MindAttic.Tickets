using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

public sealed partial class TicketsDb
{
    /// <summary>
    /// The label that opts a ticket out of roll-up (M.TIX-T372): a standing sweep or loop whose children come and go is never
    /// closed because its last open child closed. An explicit close still closes it.
    /// </summary>
    public const string ContinuousLabel = "continuous";

    /// <summary>The label that holds a ticket open for its tests (M.TIX-T411): its last child closing records a
    /// <see cref="WaitingEvent"/> instead of rolling it up.</summary>
    public const string GateTestsLabel = "gate:tests";

    /// <summary>The history field a roll-up held by <see cref="GateTestsLabel"/> writes, its new value why it waits.</summary>
    public const string WaitingEvent = "waiting";

    /// <summary>The history field a reopen writes: old = the status it had, new = the reason given (M.TIX-T372).</summary>
    public const string ReopenEvent = "reopen";

    /// <summary>
    /// Moves a closed or wont-do ticket back to open (M.TIX-T372), in one write transaction, and returns every ticket it
    /// reopened. The status change is history like any other (so stats and hygiene see it), plus a <see cref="ReopenEvent"/>
    /// with <paramref name="reason"/>. A ticket a roll-up closed gets back the ref it had before <see cref="ChildrenDoneRef"/>.
    /// With <paramref name="withParents"/>, the ancestors that closing it rolled up reopen too, nearest first, each with its
    /// old ref; without it they stay closed and can be reopened one by one.
    /// </summary>
    public IReadOnlyList<Ticket> Reopen(string id, string? reason = null, bool withParents = false)
    {
        var reopened = new List<Ticket>();
        InWrite(() =>
        {
            var t = RequireTicket(id);
            if (t.Status == TicketRules.Open) throw new InvalidOperationException($"{t.Id} is already open.");
            // Found before anything reopens: the roll-up trail is read from the close events as they stand.
            var parents = withParents ? RolledUpAncestors([t.Id], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { t.Id }) : [];
            reopened.Add(ReopenOne(t, RefBeforeRollUp(t), reason));
            foreach (var (parentId, oldRef) in parents)
                reopened.Add(ReopenOne(RequireTicket(parentId), oldRef, $"its part-of child {t.Id} reopened"));
        });
        return reopened;
    }

    /// <summary>The ref <paramref name="t"/> had before a roll-up set it to <see cref="ChildrenDoneRef"/>; its own ref otherwise.</summary>
    private string RefBeforeRollUp(Ticket t) =>
        t.Ref == ChildrenDoneRef && LatestEvent(t.Id, "ref") is { New: ChildrenDoneRef, Old: { } old } ? old : t.Ref;

    private Ticket ReopenOne(Ticket t, string reference, string? reason)
    {
        Exec("UPDATE tickets SET status = $s, ref = $ref WHERE id = $id", null, ("$s", TicketRules.Open), ("$ref", reference), ("$id", t.Id));
        Event(t.Id, ReopenEvent, t.Status, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        return t with { Status = TicketRules.Open, Ref = reference };
    }
}
