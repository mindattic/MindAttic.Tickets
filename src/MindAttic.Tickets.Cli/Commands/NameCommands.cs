using System.ComponentModel;
using System.Text;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary>
/// <c>tickets ticket names</c> (M.TIX-T313): the one-time backfill of friendly names, propose then confirm, never a silent bulk
/// rewrite. Without <c>--apply</c> it only proposes a name for every open ticket that has none, worked out from its summary,
/// and (with <c>--output</c>) writes them to a tab-separated file for the author to review and edit; <c>--apply FILE</c> then
/// writes exactly the names that file holds, each recorded in its ticket's history.
/// </summary>
public sealed class TicketNamesCommand : Command<TicketNamesCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Only this repo's open tickets. Default: every repo.")]
        public string? Repo { get; init; }

        [CommandOption("--output <FILE>")]
        [Description("Write the proposals to this tab-separated file (ID, name, summary) to review and edit; nothing is written to the database.")]
        public string? Output { get; init; }

        [CommandOption("--apply <FILE>")]
        [Description("Write the names in this reviewed file (ID<TAB>name per line; a blank name is skipped) to the database.")]
        public string? Apply { get; init; }

        public override ValidationResult Validate() =>
            Output is not null && Apply is not null ? ValidationResult.Error("--output proposes and --apply confirms: pick one") : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Apply is { } file)
        {
            var names = File.ReadAllLines(file)
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => l.Split('\t'))
                .Where(c => c.Length >= 2 && c[0].Trim().Length > 0 && c[1].Trim().Length > 0)
                .Select(c => (Id: c[0].Trim(), Name: c[1].Trim()))
                .ToList();
            var changed = db.ApplyNames(names);
            Console.WriteLine($"{changed} name(s) written ({names.Count} in {Path.GetFileName(file)}).");
            return 0;
        }

        var proposals = db.ProposeNames(settings.Repo);
        if (settings.Output is { } path)
        {
            var sb = new StringBuilder("# Proposed friendly names: edit or blank the second column, then 'tickets ticket names --apply FILE'.\n");
            foreach (var p in proposals) sb.Append(p.TicketId).Append('\t').Append(p.Name).Append('\t').Append(p.Summary.Replace('\t', ' ')).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Console.Error.WriteLine($"{proposals.Count} proposal(s) written to {Path.GetFullPath(path)}; nothing is named until you --apply it.");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("ID", "Proposed name", "Summary");
        foreach (var p in proposals)
            table.AddRow(Output.Esc(p.TicketId), Output.Esc(p.Name), $"[grey]{Output.Esc(TicketsDb.Label(null, p.Summary))}[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{proposals.Count} proposal(s); nothing written. Save them with --output FILE, review, then --apply FILE.[/]");
        return 0;
    }
}
