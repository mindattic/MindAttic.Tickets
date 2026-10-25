using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Data;

/// <summary>
/// Ticket labels (M.TIX-T74): free-form tags on a ticket, kept in <c>ticket_labels</c>. A component is a label with a
/// <c>component:</c> prefix (<c>component:desktop</c>), so one vocabulary covers both. Labels are lower-case; adding or removing
/// one is recorded in the ticket's history.
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>The longest a label may be.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>
    /// <paramref name="label"/> as it's stored (trimmed, lower-case), or null when it isn't one: a letter or digit first, then
    /// letters, digits and <c>- _ . : /</c>, at most <see cref="MaxLabelLength"/> characters.
    /// </summary>
    public static string? NormalizeLabel(string? label) =>
        label?.Trim().ToLowerInvariant() is { Length: > 0 and <= MaxLabelLength } l && LabelShape().IsMatch(l) ? l : null;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.:/-]*$")]
    private static partial Regex LabelShape();

    /// <summary>A ticket's labels, sorted.</summary>
    public IReadOnlyList<string> Labels(string id) =>
        [.. Rows("SELECT label FROM ticket_labels WHERE ticket_id = $id COLLATE NOCASE ORDER BY label", r => r.GetString(0), ("$id", CurrentId(id.Trim())))];

    /// <summary>Every labelled ticket → its labels (sorted), by ticket id (any case).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AllLabels() =>
        Rows("SELECT ticket_id, label FROM ticket_labels ORDER BY ticket_id, label", r => (Id: r.GetString(0), Label: r.GetString(1)))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(x => x.Label)], StringComparer.OrdinalIgnoreCase);

    /// <summary>Every label in use, with how many tickets carry it, by name.</summary>
    public IReadOnlyList<(string Label, long Tickets)> LabelCounts() =>
        [.. Rows("SELECT label, COUNT(*) FROM ticket_labels GROUP BY label ORDER BY label", r => (r.GetString(0), Convert.ToInt64(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)))];

    /// <summary>
    /// Adds <paramref name="labels"/> to a ticket, in one transaction; ones it already has are passed over. Every label is
    /// checked before anything changes. Returns the labels it has now.
    /// </summary>
    public IReadOnlyList<string> AddLabels(string id, IEnumerable<string> labels) => ChangeLabels(id, labels, add: true);

    /// <summary>Takes <paramref name="labels"/> off a ticket; ones it doesn't have are passed over. Returns the labels it has now.</summary>
    public IReadOnlyList<string> RemoveLabels(string id, IEnumerable<string> labels) => ChangeLabels(id, labels, add: false);

    private IReadOnlyList<string> ChangeLabels(string id, IEnumerable<string> labels, bool add)
    {
        var wanted = new List<string>();
        foreach (var raw in labels)
        {
            var l = NormalizeLabel(raw) ?? throw new ArgumentException(
                $"\"{raw}\" isn't a label: a letter or digit first, then letters, digits and - _ . : /, at most {MaxLabelLength} characters. Nothing changed.");
            if (!wanted.Contains(l, StringComparer.Ordinal)) wanted.Add(l);
        }
        if (wanted.Count == 0) throw new ArgumentException("Name at least one label.");
        IReadOnlyList<string> now = [];
        InWrite(() =>
        {
            var t = RequireTicket(id);
            var have = Labels(t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var l in wanted)
            {
                if (add == have.Contains(l)) continue;
                if (add)
                    Exec("INSERT INTO ticket_labels (ticket_id, label, added) VALUES ($id, $l, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))", null, ("$id", t.Id), ("$l", l));
                else
                    Exec("DELETE FROM ticket_labels WHERE ticket_id = $id COLLATE NOCASE AND label = $l", null, ("$id", t.Id), ("$l", l));
                Event(t.Id, "label", add ? null : l, add ? l : null);
            }
            now = Labels(t.Id);
        });
        return now;
    }
}
