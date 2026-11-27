using System.ComponentModel;
using System.Text.Json;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary>
/// <c>tickets ticket level &lt;ID&gt; [LEVEL]</c> (M.TIX-T362): shows a ticket's level, or sets it (epic | feature | story | task),
/// replacing any other <c>level:</c> label.
/// </summary>
public sealed class TicketLevelCommand : Command<TicketLevelCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Ticket ID, e.g. M.TIX-T362.")]
        public string Id { get; init; } = "";

        [CommandArgument(1, "[LEVEL]")]
        [Description("epic | feature | story | task. Omit to show the ticket's level.")]
        public string? Level { get; init; }

        public override ValidationResult Validate() =>
            Level is not null && TicketsDb.NormalizeLevel(Level) is null
                ? ValidationResult.Error($"LEVEL must be one of {string.Join(", ", TicketsDb.Levels)}")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var id = db.Ticket(settings.Id)?.Ticket.Id ?? throw new InvalidOperationException($"No ticket {settings.Id}.");
        var level = settings.Level is { } l ? db.SetLevel(id, l) : db.Level(id);
        Console.WriteLine($"{id} level: {level ?? "none"}");
        return 0;
    }
}

/// <summary>
/// <c>tickets ticket orphans [--repo R] [--json]</c> (M.TIX-T389): open tickets, epics aside, with no epic ancestor in their own
/// repo; see <see cref="TicketsDb.Orphans"/>.
/// </summary>
public sealed class TicketOrphansCommand : Command<TicketOrphansCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Only this repo. Default: every repo.")]
        public string? Repo { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var rows = db.Orphans(settings.Repo);
        var names = db.Names();
        var labels = db.AllLabels();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows.Select(x => new
            {
                repo = x.Repo, id = x.Ticket.Id, name = names.GetValueOrDefault(x.Ticket.Id), type = x.Ticket.Type,
                level = TicketsDb.LevelOf(labels.GetValueOrDefault(x.Ticket.Id)), summary = x.Ticket.Summary,
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        foreach (var x in rows)
            AnsiConsole.MarkupLine($"  [bold]{Output.Esc(x.Ticket.Id)}[/] {Output.Esc(x.Repo)} ({Output.Esc(x.Ticket.Type)}): {Output.Name(names.GetValueOrDefault(x.Ticket.Id), x.Ticket.Summary)}");
        AnsiConsole.MarkupLine($"[grey]{rows.Count} open ticket(s) with no epic above them in their repo[/]");
        return 0;
    }
}
