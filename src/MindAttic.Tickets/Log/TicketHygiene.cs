using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Log;

/// <summary>A closed ticket that may not really be done, and why.</summary>
public sealed record HygieneFinding(string Repo, string TicketId, string Summary, string Why);

/// <summary>
/// Closed tickets that may not really be done (taken over from the Orchestrator's <c>orchestrator hygiene</c>, M.TIX-T134): a
/// fix or feature closed with no Ref (no test or commit cited, against the house rule that every done row cites its proof),
/// and a closed ticket that a later ticket in the same repo points back to as reopened, regressed or still broken.
/// </summary>
public static partial class TicketHygiene
{
    [GeneratedRegex(@"\b(reopen\w*|regress\w*|again|still|broke\w*|incomplete|didn'?t|did not|doesn'?t|not fixed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Relapse();

    /// <summary>The findings in one repo's tickets.</summary>
    public static IReadOnlyList<HygieneFinding> Check(string repo, IReadOnlyList<Ticket> tickets)
    {
        var findings = new List<HygieneFinding>();
        foreach (var t in tickets.Where(t => t.Status == TicketRules.Closed && t.Type is "fix" or "feature"))
        {
            if (t.Ref.Trim() is "" or "—" or "-" or "–")
                findings.Add(new(repo, t.Id, t.Summary, "closed with no Ref: no test or commit cited"));

            var idPattern = new Regex($@"\b{Regex.Escape(t.Id)}\b");
            var followUp = tickets.FirstOrDefault(later => (later.Number ?? 0) > (t.Number ?? 0) &&
                idPattern.IsMatch(later.Summary) && Relapse().IsMatch(later.Summary));
            if (followUp is not null)
                findings.Add(new(repo, t.Id, t.Summary, $"{followUp.Id} says it came back: \"{Trim(followUp.Summary, 90)}\""));
        }
        return findings;
    }

    /// <summary>The findings across every repo's tickets, judged repo by repo.</summary>
    public static IReadOnlyList<HygieneFinding> Check(IEnumerable<(string Repo, Ticket Ticket)> tickets) =>
        [.. tickets.GroupBy(t => t.Repo, StringComparer.OrdinalIgnoreCase).SelectMany(g => Check(g.Key, [.. g.Select(x => x.Ticket)]))];

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
