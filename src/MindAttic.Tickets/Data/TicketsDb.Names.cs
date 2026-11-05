using System.Data.Common;
using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Data;

/// <summary>One proposed friendly name for a ticket (M.TIX-T313): what <see cref="TicketsDb.ProposeNames"/> offers for review.</summary>
public sealed record NameProposal(string Repo, string TicketId, string Summary, string Name);

/// <summary>
/// Friendly names (M.TIX-T313): a short (2 to 6 word) label a ticket carries beside its ID, its one-line summary and its
/// markdown description, kept in <c>ticket_names</c> (no row means none). Every change is history: a <c>name</c> event with the
/// old and new name. Where a ticket has none, lists show its summary cut short instead (<see cref="Label"/>).
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>The longest a friendly name may be.</summary>
    public const int MaxNameLength = 80;

    /// <summary>How long a summary shown in a name's place may run before it's cut with an ellipsis.</summary>
    public const int LabelSummaryLength = 60;

    /// <summary>A name as stored: one line, single-spaced, trimmed; null when blank.</summary>
    public static string? NormalizeName(string? name)
    {
        if (name is null) return null;
        var one = Whitespace().Replace(name, " ").Trim();
        if (one.Length == 0) return null;
        if (one.Length > MaxNameLength) throw new ArgumentException($"A friendly name is short (at most {MaxNameLength} characters); use the summary or description for more.", nameof(name));
        return one;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>A ticket's friendly name (found by a former ID too); null when it has none.</summary>
    public string? Name(string id) =>
        Rows("SELECT name FROM ticket_names WHERE ticket_id = $id COLLATE NOCASE", r => r.GetString(0), ("$id", CurrentId(id.Trim()))).FirstOrDefault();

    /// <summary>Every named ticket → its name, by ticket id (any case).</summary>
    public IReadOnlyDictionary<string, string> Names() =>
        Rows("SELECT ticket_id, name FROM ticket_names", r => (r.GetString(0), r.GetString(1)))
            .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);

    /// <summary>What a list shows for a ticket: its name, or its summary cut to <see cref="LabelSummaryLength"/> characters.</summary>
    public static string Label(string? name, string summary) =>
        name is { Length: > 0 } ? name : summary.Length <= LabelSummaryLength ? summary : summary[..(LabelSummaryLength - 1)].TrimEnd() + "…";

    /// <summary>"ID · label": how a ticket is named in one line everywhere (lists, graphs, link lines).</summary>
    public static string Caption(string id, string? name, string? summary) =>
        summary is null && name is null ? id : $"{id} · {Label(name, summary ?? "")}";

    /// <summary>
    /// Sets (or, with a blank <paramref name="name"/>, removes) a ticket's friendly name; the change is a <c>name</c> event in its
    /// history. Returns false when nothing changed.
    /// </summary>
    public bool SetName(string id, string? name)
    {
        var text = NormalizeName(name);
        var t = Ticket(id)?.Ticket ?? throw new KeyNotFoundException($"No ticket {id}.");
        var changed = false;
        InWrite(() => changed = SetNameCore(t.Id, text, ambient));
        return changed;
    }

    private bool SetNameCore(string id, string? text, DbTransaction? tx)
    {
        var old = Rows("SELECT name FROM ticket_names WHERE ticket_id = $id COLLATE NOCASE", tx, r => r.GetString(0), ("$id", id)).FirstOrDefault();
        if (old == text) return false;
        if (text is null) Exec("DELETE FROM ticket_names WHERE ticket_id = $id COLLATE NOCASE", tx, ("$id", id));
        else SaveName(id, text, tx);
        Exec("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'name', $o, $n)", tx,
            ("$id", id), ("$o", old), ("$n", text));
        return true;
    }

    private void SaveName(string id, string name, DbTransaction? tx) =>
        Exec(server is null
                ? "INSERT INTO ticket_names (ticket_id, name, updated) VALUES ($id, $n, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')) " +
                  "ON CONFLICT (ticket_id) DO UPDATE SET name = excluded.name, updated = excluded.updated"
                : "UPDATE ticket_names SET name = $n, updated = strftime('%Y-%m-%dT%H:%M:%SZ', 'now') WHERE ticket_id = $id; " +
                  "IF @@ROWCOUNT = 0 INSERT INTO ticket_names (ticket_id, name, updated) VALUES ($id, $n, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))",
            tx, ("$id", id), ("$n", name));

    /// <summary>
    /// A friendly name for every open ticket (optionally one repo's) that has none, worked out from its summary
    /// (<see cref="ProposeName"/>). Nothing is written: the author reviews the list and confirms it (<see cref="ApplyNames"/>).
    /// </summary>
    public IReadOnlyList<NameProposal> ProposeNames(string? repo = null)
    {
        var names = Names();
        return [.. Tickets(repo)
            .Where(x => x.Ticket.Status == Log.TicketRules.Open && !names.ContainsKey(x.Ticket.Id))
            .Select(x => new NameProposal(x.Repo, x.Ticket.Id, x.Ticket.Summary, ProposeName(x.Ticket.Summary)))
            .Where(p => p.Name.Length > 0)];
    }

    /// <summary>
    /// Writes the confirmed names (<paramref name="names"/>: ticket ID → name), each recorded in its ticket's history, in one
    /// transaction; a ticket the database doesn't hold is refused before anything is written. Returns how many changed.
    /// </summary>
    public int ApplyNames(IReadOnlyList<(string Id, string Name)> names)
    {
        var resolved = names.Select(n => (Id: Ticket(n.Id)?.Ticket.Id ?? throw new KeyNotFoundException($"No ticket {n.Id}."), Name: NormalizeName(n.Name))).ToList();
        var changed = 0;
        InWrite(() => { foreach (var (id, name) in resolved) if (SetNameCore(id, name, ambient)) changed++; });
        return changed;
    }

    /// <summary>
    /// A 2 to 6 word name from a summary: its first clause (up to a colon, dash, semicolon, parenthesis or "so"/"because"),
    /// without a leading ticket ID or bracketed tag, at most six words, no dangling joining word at its end.
    /// </summary>
    public static string ProposeName(string summary)
    {
        var s = LeadingTag().Replace(summary.Trim(), "");
        var cut = ClauseEnd().Match(s);
        if (cut.Success && cut.Index >= 8) s = s[..cut.Index];
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(',', '.', ';', ':')).Where(w => w.Length > 0).Take(6).ToList();
        while (words.Count > 2 && Joining.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count == 0) return "";
        var name = string.Join(' ', words);
        name = char.ToUpperInvariant(name[0]) + name[1..];
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength].TrimEnd();
    }

    private static readonly HashSet<string> Joining = new(StringComparer.OrdinalIgnoreCase)
        { "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with", "by", "from", "at", "as", "is", "its", "it", "that", "when", "so", "but", "into", "via" };

    [GeneratedRegex(@"^(\[[^\]]*\]\s*|[A-Z][A-Z0-9.]*-T\d+\s*[:\-—]?\s*)+")]
    private static partial Regex LeadingTag();

    [GeneratedRegex(@"(:\s| [—–-] |;|\(| so | because | instead of )")]
    private static partial Regex ClauseEnd();
}
