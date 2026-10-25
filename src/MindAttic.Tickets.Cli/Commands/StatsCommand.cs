using System.ComponentModel;
using System.Text.Json;
using MindAttic.Tickets.Log;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary>
/// <c>tickets ticket stats</c> (M.TIX-T86): throughput (filed, closed and wont-do per period), cycle time (filed to closed)
/// overall and by type, and how many tickets were open at the end of each period, by repo and by type, all worked out
/// from the tickets and their history in <c>ticket_events</c>.
/// </summary>
public sealed class TicketStatsCommand : Command<TicketStatsCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Only this repo (its name or code). Default: every repo.")]
        public string? Repo { get; init; }

        [CommandOption("--type <TYPE>")]
        [Description("Only this type: feature | fix | chore | docs | interview.")]
        public string? Type { get; init; }

        [CommandOption("--by <PERIOD>")]
        [Description("day | week (default; Monday to Sunday, UTC) | month.")]
        public string By { get; init; } = "week";

        [CommandOption("--periods <N>")]
        [Description("How many periods, ending with the current one. Default: 8.")]
        public int Periods { get; init; } = 8;

        [CommandOption("--json")]
        public bool Json { get; init; }

        public override ValidationResult Validate() =>
            TicketStats.ParsePeriod(By) is null ? ValidationResult.Error("--by must be day, week or month")
            : Periods is < 1 or > 366 ? ValidationResult.Error("--periods must be 1 to 366")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var by = TicketStats.ParsePeriod(settings.By)!.Value;
        var repos = db.Repos();
        string? repo = null;
        if (!string.IsNullOrWhiteSpace(settings.Repo))
            repo = repos.FirstOrDefault(r => r.Name.Equals(settings.Repo, StringComparison.OrdinalIgnoreCase) || r.Code.Equals(settings.Repo, StringComparison.OrdinalIgnoreCase)).Name
                ?? throw new KeyNotFoundException($"No repo '{settings.Repo}' in the ticket database.");
        var tickets = db.Tickets(repo).Where(x => settings.Type is null || x.Ticket.Type.Equals(settings.Type, StringComparison.OrdinalIgnoreCase));
        var m = TicketStats.Compute(tickets, db.StatusHistory(), by, settings.Periods, DateTime.UtcNow);

        if (settings.Json)
        {
            static double? Hours(TimeSpan? t) => t is { } x ? Math.Round(x.TotalHours, 2) : null;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                by = by.ToString().ToLowerInvariant(),
                repo, type = settings.Type,
                periods = m.Periods.Select(p => new { label = p.Label, start = p.Start.ToString("yyyy-MM-dd"), end = p.End.ToString("yyyy-MM-dd"), filed = p.Opened, closed = p.Closed, wontDo = p.WontDo, openAtEnd = p.OpenAtEnd }),
                cycleTime = m.CycleTime.Select(c => new { type = c.Group, closed = c.Count, medianHours = Hours(c.Median), meanHours = Hours(c.Mean), p85Hours = Hours(c.P85) }),
                openByRepo = m.OpenByRepo.Select(s => new { repo = s.Name, counts = s.Counts }),
                openByType = m.OpenByType.Select(s => new { type = s.Name, counts = s.Counts }),
                closedBeforeHistory = m.ClosedBeforeHistory,
                filedClosed = m.FiledClosed,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var unit = by.ToString().ToLowerInvariant();
        var scope = string.Join(", ", new[] { repo, settings.Type }.Where(x => x is not null));
        AnsiConsole.MarkupLine($"[bold]Ticket metrics[/]{(scope.Length > 0 ? $" for {Output.Esc(scope)}" : "")}: the last {m.Periods.Count} {unit}(s), UTC, from ticket_events.");

        var throughput = new Table().Border(TableBorder.Rounded).Title("Throughput")
            .AddColumns(by == StatsPeriod.Week ? "Week of" : by == StatsPeriod.Day ? "Day" : "Month", "Filed", "Closed", "Wont-do", "Open at end");
        foreach (var p in m.Periods)
            throughput.AddRow(p.Label, $"{p.Opened}", $"{p.Closed}", $"{p.WontDo}", $"{p.OpenAtEnd}");
        AnsiConsole.Write(throughput);

        var cycle = new Table().Border(TableBorder.Rounded).Title("Cycle time, filed to closed")
            .AddColumns("Type", "Closed", "Median", "Mean", "85th percentile");
        foreach (var c in m.CycleTime)
            cycle.AddRow(Output.Esc(c.Group), $"{c.Count}", TicketStats.Duration(c.Median), TicketStats.Duration(c.Mean), TicketStats.Duration(c.P85));
        AnsiConsole.Write(cycle);

        WriteSeries("Open at the end of each period, by repo", "Repo", m.OpenByRepo, m);
        WriteSeries("Open at the end of each period, by type", "Type", m.OpenByType, m);

        if (m.FiledClosed > 0)
            AnsiConsole.MarkupLine($"[grey]{m.FiledClosed} closing(s) were filed already closed: counted as throughput, not in cycle time.[/]");
        if (m.ClosedBeforeHistory > 0)
            AnsiConsole.MarkupLine($"[grey]{m.ClosedBeforeHistory} ticket(s) closed before their history was kept: in none of these numbers.[/]");
        return 0;
    }

    private static void WriteSeries(string title, string what, IReadOnlyList<OpenSeries> series, TicketMetrics m)
    {
        if (series.Count == 0) { AnsiConsole.MarkupLine($"[grey]{title}: none open.[/]"); return; }
        // Short headings (MM-dd; a month as yyyy-MM) keep a column per period on screen; the throughput table names each in full.
        var table = new Table().Border(TableBorder.Rounded).Title(title).AddColumn(new TableColumn(what).NoWrap());
        foreach (var p in m.Periods) table.AddColumn(new TableColumn(m.By == StatsPeriod.Month ? p.Label : p.Start.ToString("MM-dd")).RightAligned());
        foreach (var s in series) table.AddRow([Output.Esc(s.Name), .. s.Counts.Select(c => $"{c}")]);
        AnsiConsole.Write(table);
    }
}
