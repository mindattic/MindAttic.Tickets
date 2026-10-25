using MindAttic.Tickets.Data;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>One ticket a bulk action couldn't change, and why (it's closed, it's gone).</summary>
public sealed record BulkFailure(string Id, string Reason);

/// <summary>What a bulk action (M.TIX-T79) did: the tickets it changed, each recorded in its own history, and those it couldn't.</summary>
public sealed record BulkResult(IReadOnlyList<string> Done, IReadOnlyList<BulkFailure> Failed)
{
    /// <summary>What the batch changed, for undoing it in one go (M.TIX-T80); null or empty when it changed nothing.</summary>
    public TicketChange? Change { get; init; }

    /// <summary>"Approved: A, B, C (3 tickets)." plus, when any were left alone, which and why.</summary>
    public string Describe(string verb)
    {
        var text = Done.Count switch
        {
            0 => $"{verb}: none.",
            1 => $"{verb}: {Done[0]}.",
            _ => $"{verb}: {string.Join(", ", Done)} ({Done.Count} tickets).",
        };
        if (Failed.Count > 0) text += $" Not changed: {string.Join("; ", Failed.Select(f => $"{f.Id} ({f.Reason.TrimEnd('.')})"))}.";
        return text;
    }
}
