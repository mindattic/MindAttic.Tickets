using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Log;

/// <summary>An open ticket whose summary reads like the one being filed, and how alike they are (0 to 1).</summary>
public sealed record SimilarTicket(string Repo, Ticket Ticket, double Score);

/// <summary>
/// Duplicate detection on summary similarity (M.TIX-T88): the Dice coefficient of the two summaries' significant words
/// (lowercased, a plural 's' dropped, common words ignored). Filing still goes ahead; this only warns.
/// </summary>
public static partial class TicketSimilarity
{
    /// <summary>How alike two summaries must be to be called a likely duplicate.</summary>
    public const double Threshold = 0.6;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "of", "to", "in", "on", "at", "by", "for", "with", "from", "into", "as",
        "is", "are", "be", "it", "its", "this", "that", "so", "when", "then", "than", "not", "no", "do", "does", "has",
        "have", "was", "were", "can", "via", "per", "all", "each", "every", "there", "their", "they", "which", "what",
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    /// <summary>The significant words of a summary.</summary>
    public static IReadOnlySet<string> Words(string summary)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Word().Matches(summary.ToLowerInvariant()))
        {
            var w = m.Value;
            if (w.Length < 2 || StopWords.Contains(w)) continue;
            if (w.Length > 4 && (w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal)
                                 || w.EndsWith("sses", StringComparison.Ordinal) || w.EndsWith("xes", StringComparison.Ordinal)))
                w = w[..^2];
            else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
            words.Add(w);
        }
        return words;
    }

    /// <summary>The Dice coefficient of the two summaries' significant words: 1 for the same words, 0 for none shared.</summary>
    public static double Score(string a, string b) => Score(Words(a), Words(b));

    private static double Score(IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        a.Count + b.Count == 0 ? 0 : 2.0 * a.Count(b.Contains) / (a.Count + b.Count);

    /// <summary>The open tickets among <paramref name="candidates"/> whose summary is at least <paramref name="threshold"/> alike, most alike first.</summary>
    public static IReadOnlyList<SimilarTicket> Similar(string summary, IEnumerable<(string Repo, Ticket Ticket)> candidates,
        double threshold = Threshold, int max = 5)
    {
        var words = Words(summary);
        if (words.Count == 0) return [];
        return [.. candidates
            .Where(c => c.Ticket.Status == TicketRules.Open)
            .Select(c => new SimilarTicket(c.Repo, c.Ticket, Score(words, Words(c.Ticket.Summary))))
            .Where(s => s.Score >= threshold)
            .OrderByDescending(s => s.Score).ThenBy(s => s.Ticket.Id, StringComparer.Ordinal)
            .Take(max)];
    }
}
