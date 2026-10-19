using System.Globalization;
using System.Text.RegularExpressions;

namespace MindAttic.Workflow.Tickets;

/// <summary>
/// The rules every repo's ticket table follows (see codex-standard/IMPLEMENTATION_PROMPT.md):
/// sequential <c>CODE-T&lt;n&gt;</c> IDs that are never reused, a fixed vocabulary for type and
/// status, and append-only rows whose only mutable cells are Status and Ref.
/// </summary>
public static partial class TicketRules
{
    public const string Open = "open";
    public const string Closed = "closed";
    public const string WontDo = "wont-do";

    public static readonly IReadOnlyList<string> Statuses = [Open, Closed, WontDo];
    public static readonly IReadOnlyList<string> Types = ["feature", "fix", "chore", "docs"];

    [GeneratedRegex(@"^((?:[A-Z]\.)?[A-Z][A-Z0-9]*)-T(\d+)$")]
    private static partial Regex IdPattern();

    // Case-insensitive on purpose: the front-matter 'code:' accepts a lowercase code (Validate flags it as malformed), and
    // if its tickets' numbers didn't parse, NextId would see every one as 0 and hand out "-T1" again on every Add.
    [GeneratedRegex(@"^((?:[A-Z]\.)?[A-Z][A-Z0-9]*)-T(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex IdNumberPattern();

    /// <summary>The number in <c>CODE-T&lt;n&gt;</c> (whatever the code's case), or null when <paramref name="id"/> isn't one.</summary>
    public static int? ParseNumber(string id)
    {
        var m = IdNumberPattern().Match(id);
        return m.Success && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>The next unused ID: highest existing number + 1, so a gap is never back-filled.</summary>
    public static string NextId(string code, IEnumerable<Ticket> existing)
    {
        var max = existing.Select(t => t.Number ?? 0).DefaultIfEmpty(0).Max();
        return $"{code}-T{max + 1}";
    }

    /// <summary>Problems with a single table: bad IDs, duplicates, unknown type/status, bad dates.</summary>
    public static IReadOnlyList<string> Validate(string code, IReadOnlyList<Ticket> tickets)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tickets)
        {
            var m = IdPattern().Match(t.Id);
            if (!m.Success) problems.Add($"{t.Id}: ID is not CODE-T<n>");
            else if (m.Groups[1].Value != code) problems.Add($"{t.Id}: ID prefix doesn't match the file's code '{code}'");
            if (!seen.Add(t.Id)) problems.Add($"{t.Id}: duplicate ID");
            if (!Types.Contains(t.Type)) problems.Add($"{t.Id}: unknown type '{t.Type}' (expected {string.Join("/", Types)})");
            if (!Statuses.Contains(t.Status)) problems.Add($"{t.Id}: unknown status '{t.Status}' (expected {string.Join("/", Statuses)})");
            if (!DateOnly.TryParseExact(t.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                problems.Add($"{t.Id}: date '{t.Date}' is not YYYY-MM-DD");
        }
        return problems;
    }

    /// <summary>
    /// Append-only check between an earlier and a later version of the same table: every earlier
    /// row must still exist with the same Date, Type and Summary. Status and Ref may change.
    /// </summary>
    public static IReadOnlyList<string> CheckAppendOnly(IReadOnlyList<Ticket> before, IReadOnlyList<Ticket> after)
    {
        var problems = new List<string>();
        var now = after.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var old in before)
        {
            if (!now.TryGetValue(old.Id, out var cur))
                problems.Add($"{old.Id}: row was deleted");
            else if (cur.Date != old.Date || cur.Type != old.Type || cur.Summary != old.Summary)
                problems.Add($"{old.Id}: row was reworded (only Status and Ref may change)");
        }
        return problems;
    }

    /// <summary>Today's date in the table's YYYY-MM-DD form.</summary>
    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
