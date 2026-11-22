using System.ComponentModel;
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
