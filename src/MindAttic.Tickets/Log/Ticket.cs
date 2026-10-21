namespace MindAttic.Tickets.Log;

/// <summary>
/// One row of a repo's <c>## Tickets</c> table in <c>docs/USER_STORIES.md</c>.
/// Cell text is stored unescaped (a literal <c>|</c>, not <c>\|</c>).
/// </summary>
public sealed record Ticket(string Id, string Date, string Type, string Status, string Summary, string Ref)
{
    /// <summary>The numeric part of <see cref="Id"/> (<c>M.LNCH-T12</c> → 12), or null if it isn't <c>CODE-T&lt;n&gt;</c>.</summary>
    public int? Number => TicketRules.ParseNumber(Id);
}
