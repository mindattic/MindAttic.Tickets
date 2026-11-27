using System.Text;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary>
/// <c>tickets db export-legacy</c> (M.TIX-T403): every repo's frozen legacy planning rows (stories, epics,
/// backlog items, blocks and backlog links) to <c>data/legacy-planning.sql</c> and a JSON copy, each table's
/// written row count checked against a fresh count of the live table.
/// </summary>
public sealed class DbExportLegacyCommand : Command<DbExportLegacyCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings;

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var (rows, counts) = db.ExportLegacy();
        var self = settings.Workspace.Find(TicketsDb.HomeFolder(settings.Workspace.Root))
            ?? throw new InvalidOperationException("No MindAttic.Tickets repo in the workspace.");

        var sqlPath = Path.Combine(self.Path, "data", "legacy-planning.sql");
        var jsonPath = Path.Combine(self.Path, "data", "legacy-planning.json");
        File.WriteAllText(sqlPath, TicketsDb.LegacyDump(rows), new UTF8Encoding(false));
        File.WriteAllText(jsonPath, TicketsDb.LegacyJson(rows), new UTF8Encoding(false));

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Table", "Exported", "Live", "");
        foreach (var c in counts)
            table.AddRow(Output.Esc(c.Table), $"{c.Exported}", $"{c.Live}", c.Exported == c.Live ? "[green]ok[/]" : "[red]MISMATCH[/]");
        AnsiConsole.Write(table);

        var bad = counts.Where(c => c.Exported != c.Live).ToList();
        if (bad.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{bad.Count} table(s) changed mid-export (exported count doesn't match the live table): " +
                $"{Output.Esc(string.Join(", ", bad.Select(c => c.Table)))}.[/] Run it again.");
            return 1;
        }
        AnsiConsole.MarkupLine($"[green]Wrote[/] {Output.Esc(sqlPath)} [green]and[/] {Output.Esc(jsonPath)} " +
            $"({counts.Sum(c => c.Exported)} row(s) across {counts.Count} tables); every count matches the live tables.");
        return 0;
    }
}
