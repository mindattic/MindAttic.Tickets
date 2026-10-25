using System.Data.Common;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>A named ticket query kept in the database for reuse (M.TIX-T78), by the CLI and Tickets Desktop alike.</summary>
public sealed record SavedFilter(string Name, string Query, string Updated);

public sealed partial class TicketsDb
{
    /// <summary>Every saved filter, by name.</summary>
    public IReadOnlyList<SavedFilter> SavedFilters() =>
        [.. Rows("SELECT name, query, updated FROM saved_filters ORDER BY name COLLATE NOCASE", ReadFilter)];

    /// <summary>The saved filter called <paramref name="name"/> (any case), or null.</summary>
    public SavedFilter? FindFilter(string name) =>
        Rows("SELECT name, query, updated FROM saved_filters WHERE name = $n COLLATE NOCASE", ReadFilter, ("$n", name.Trim())).FirstOrDefault();

    /// <summary>
    /// Saves <paramref name="query"/> as <paramref name="name"/>, replacing a filter of that name (any case). The query is
    /// parsed first, so a filter that can't run is never saved (<see cref="FormatException"/>).
    /// </summary>
    public SavedFilter SaveFilter(string name, string query)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A saved filter needs a name.", nameof(name));
        if (name.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("A saved filter's name is one line.", nameof(name));
        var parsed = TicketQuery.Parse(query);
        if (parsed.IsEmpty) throw new ArgumentException("A saved filter needs a query.", nameof(query));
        InWrite(() =>
        {
            // Keep the name as first saved when only its case differs, so a rename by case doesn't fork it.
            var existing = FindFilter(name);
            var key = existing?.Name ?? name;
            Exec(server is null
                    ? "INSERT INTO saved_filters (name, query, updated) VALUES ($n, $q, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')) " +
                      "ON CONFLICT (name) DO UPDATE SET query = excluded.query, updated = excluded.updated"
                    : "UPDATE saved_filters SET query = $q, updated = strftime('%Y-%m-%dT%H:%M:%SZ', 'now') WHERE name = $n; " +
                      "IF @@ROWCOUNT = 0 INSERT INTO saved_filters (name, query, updated) VALUES ($n, $q, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))",
                null, ("$n", key), ("$q", parsed.Text));
        });
        return FindFilter(name)!;
    }

    /// <summary>Deletes the saved filter called <paramref name="name"/>; false when there was none.</summary>
    public bool DeleteFilter(string name) =>
        Exec("DELETE FROM saved_filters WHERE name = $n COLLATE NOCASE", null, ("$n", name.Trim())) > 0;

    private static SavedFilter ReadFilter(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2));

    /// <summary>
    /// Each ticket's labels, for a query's <c>label:</c>, from the <c>ticket_labels</c> table (ticket_id, label) once labels
    /// exist (M.TIX-T74); empty until then, when <c>label:</c> matches nothing.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> TicketLabels()
    {
        List<(string Id, string Label)> rows;
        try { rows = Rows("SELECT ticket_id, label FROM ticket_labels", r => (r.GetString(0), r.GetString(1))); }
        catch (DbException) { return new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase); }
        return rows.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)[.. g.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase)], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tickets <paramref name="query"/> matches (optionally in one repo), in repo then log order, each with its approval
    /// and labels.
    /// </summary>
    public IReadOnlyList<TicketFacts> Find(TicketQuery query, string? repo = null)
    {
        var states = QueueStates();
        var labels = TicketLabels();
        return [.. Tickets(repo)
            .Select(x => new TicketFacts(x.Repo, x.Ticket, states.TryGetValue(x.Ticket.Id, out var s) ? s.Approval : Proposed, labels.GetValueOrDefault(x.Ticket.Id)))
            .Where(query.Matches)];
    }
}
