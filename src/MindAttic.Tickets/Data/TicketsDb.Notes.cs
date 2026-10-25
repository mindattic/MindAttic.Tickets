namespace MindAttic.Tickets.Data;

/// <summary>One comment on a ticket (M.TIX-T72): who wrote it, when (<c>yyyy-MM-ddTHH:mm:ssZ</c>), and its markdown.</summary>
public sealed record TicketNote(long Seq, string TicketId, string Author, string At, string Markdown);

public sealed partial class TicketsDb
{
    /// <summary>
    /// Appends a note to the ticket <paramref name="id"/> (or the one a former ID became). Notes are append-only: the
    /// database refuses to change or delete one. Returns the note as stored.
    /// </summary>
    public TicketNote AddNote(string id, string author, string markdown)
    {
        if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("A note needs an author.", nameof(author));
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("A note needs text.", nameof(markdown));
        TicketNote note = null!;
        InWrite(() =>
        {
            var t = RequireTicket(id);
            var at = Now();
            var seq = Convert.ToInt64(Scalar("INSERT INTO ticket_notes (ticket_id, author, at, markdown) VALUES ($id, $a, $at, $md) RETURNING seq", null,
                ("$id", t.Id), ("$a", author.Trim()), ("$at", at), ("$md", markdown.Trim())), System.Globalization.CultureInfo.InvariantCulture);
            note = new TicketNote(seq, t.Id, author.Trim(), at, markdown.Trim());
        });
        return note;
    }

    /// <summary>The ticket's notes, oldest first (none for an unknown ticket).</summary>
    public IReadOnlyList<TicketNote> Notes(string id) =>
        [.. Rows("SELECT seq, ticket_id, author, at, markdown FROM ticket_notes WHERE ticket_id = $id COLLATE NOCASE ORDER BY seq",
            r => new TicketNote(Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)),
            ("$id", Ticket(id.Trim())?.Ticket.Id ?? CurrentId(id.Trim())))];
}
