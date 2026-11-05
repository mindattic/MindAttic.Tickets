using System.ComponentModel;
using System.Text.Json;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary>
/// <c>tickets ticket suggest-links</c> (M.TIX-T316): suggestions only, never an automatic edit. For every pair of open tickets whose
/// summaries read alike (<see cref="TicketSimilarity"/>) and that aren't linked yet, it proposes a link, <c>duplicates</c> when they
/// read nearly the same, else <c>related</c>, and prints the <c>tickets ticket link</c> command that would accept it.
/// </summary>
public sealed class TicketSuggestLinksCommand : Command<TicketSuggestLinksCommand.Settings>
{
    /// <summary>How alike two summaries must be to be proposed as duplicates rather than related.</summary>
    public const double DuplicateScore = 0.8;

    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Only pairs with at least one ticket in this repo. Default: every repo.")]
        public string? Repo { get; init; }

        [CommandOption("--min <SCORE>")]
        [Description("How alike (0 to 1) two summaries must be to be proposed. Default: 0.6.")]
        public double Min { get; init; } = TicketSimilarity.Threshold;

        [CommandOption("--max <N>")]
        [Description("At most this many proposals, most alike first. Default: 50.")]
        public int Max { get; init; } = 50;

        [CommandOption("--json")]
        public bool Json { get; init; }

        public override ValidationResult Validate() =>
            Min is <= 0 or > 1 ? ValidationResult.Error("--min is a score above 0, at most 1")
            : Max < 1 ? ValidationResult.Error("--max is at least 1")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var graph = db.Graph();
        var open = db.Tickets().Where(t => t.Ticket.Status == TicketRules.Open)
            .Select(t => (t.Repo, t.Ticket, Words: TicketSimilarity.Words(t.Ticket.Summary))).ToList();
        bool Linked(string a, string b) => graph.Out(a).Any(e => e.To.Equals(b, StringComparison.OrdinalIgnoreCase))
                                           || graph.In(a).Any(e => e.From.Equals(b, StringComparison.OrdinalIgnoreCase));
        var proposals = new List<(string From, string Kind, string To, double Score)>();
        for (var i = 0; i < open.Count; i++)
            for (var j = i + 1; j < open.Count; j++)
            {
                var (a, b) = (open[i], open[j]);
                if (settings.Repo is { } r && !a.Repo.Equals(r, StringComparison.OrdinalIgnoreCase) && !b.Repo.Equals(r, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.Words.Count + b.Words.Count == 0) continue;
                var score = 2.0 * a.Words.Count(b.Words.Contains) / (a.Words.Count + b.Words.Count);
                if (score < settings.Min || Linked(a.Ticket.Id, b.Ticket.Id)) continue;
                // The newer one duplicates the older; related reads the same either way.
                var (newer, older) = TicketRules.ParseNumber(a.Ticket.Id) > TicketRules.ParseNumber(b.Ticket.Id) && a.Repo == b.Repo ? (a, b)
                    : string.CompareOrdinal(a.Ticket.Date, b.Ticket.Date) >= 0 ? (a, b) : (b, a);
                proposals.Add((newer.Ticket.Id, score >= DuplicateScore ? "duplicates" : "related", older.Ticket.Id, score));
            }
        var shown = proposals.OrderByDescending(p => p.Score).ThenBy(p => p.From, StringComparer.Ordinal).Take(settings.Max).ToList();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(shown.Select(p => new
            {
                from = p.From, kind = p.Kind, to = p.To, score = Math.Round(p.Score, 2), accept = $"tickets ticket link {p.From} {p.Kind} {p.To}",
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Alike", "Proposed link", "To accept");
        foreach (var p in shown)
            table.AddRow(p.Score.ToString("P0", System.Globalization.CultureInfo.InvariantCulture),
                $"{Output.Esc(graph.Node(p.From).Caption)} [cyan]{p.Kind}[/] {Output.Esc(graph.Node(p.To).Caption)}",
                $"[grey]tickets ticket link {Output.Esc(p.From)} {p.Kind} {Output.Esc(p.To)}[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{shown.Count} proposal(s) of {proposals.Count}; nothing is linked until you run its command.[/]");
        return 0;
    }
}
