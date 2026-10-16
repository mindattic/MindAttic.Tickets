using System.Text.RegularExpressions;

namespace MindAttic.Workflow.Data;

/// <summary>
/// The commit a <c>workflow db sync</c> makes names the tickets it carries, so the workspace's commit-msg hook
/// (every subject ends with an existing ticket ID) accepts it.
/// </summary>
public static partial class SyncCommit
{
    public const string SubjectStart = "Sync the workflow database";

    [GeneratedRegex(@"^INSERT INTO tickets \([^)]*\) VALUES \('(?<id>[^']+)'")]
    private static partial Regex TicketRow();

    [GeneratedRegex(@"^INSERT INTO ticket_events \([^)]*\) VALUES \(\d+, '(?<id>[^']+)'")]
    private static partial Regex EventRow();

    /// <summary>Tickets whose row or history differs between two dumps, in the order they appear in <paramref name="after"/>.</summary>
    public static IReadOnlyList<string> ChangedTickets(string? before, string after)
    {
        var old = new HashSet<string>((before ?? "").Split('\n'), StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var line in after.Split('\n'))
        {
            if (old.Contains(line)) continue;
            var m = TicketRow().Match(line);
            if (!m.Success) m = EventRow().Match(line);
            if (m.Success && !ids.Contains(m.Groups["id"].Value)) ids.Add(m.Groups["id"].Value);
        }
        return ids;
    }

    /// <summary>
    /// "Sync the workflow database for A, B (B)": the changed tickets, ending with the last one in parentheses.
    /// <paramref name="ticket"/> names the commit's ticket explicitly. Null when there's no ticket to name.
    /// </summary>
    public static string? Subject(IReadOnlyList<string> changed, string? ticket = null)
    {
        var last = ticket ?? changed.LastOrDefault();
        if (last is null) return null;
        var others = changed.Where(id => id != last).ToList();
        if (others.Count == 0) return $"{SubjectStart} ({last})";
        var listed = string.Join(", ", others.Take(5)) + (others.Count > 5 ? $", {others.Count - 5} more" : "");
        return $"{SubjectStart} for {listed} and {last} ({last})";
    }
}
