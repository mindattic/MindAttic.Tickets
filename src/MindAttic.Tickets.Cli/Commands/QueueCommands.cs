using System.ComponentModel;
using System.Text.Json;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

public sealed class TicketQueueCommand : Command<TicketQueueCommand.Settings>
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
        var rows = db.Queue(settings.Repo);
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows.Select(x => new
            {
                repo = x.Repo, id = x.Ticket.Id, date = x.Ticket.Date, type = x.Ticket.Type,
                approval = x.Approval, priority = x.Priority, summary = x.Ticket.Summary, @ref = x.Ticket.Ref,
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("#", "Priority", "Approval", "ID", "Type", "Summary");
        var n = 0;
        foreach (var x in rows)
            table.AddRow((++n).ToString(System.Globalization.CultureInfo.InvariantCulture), x.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Output.Esc(x.Approval), Output.Esc(x.Ticket.Id), Output.Esc(x.Ticket.Type), Output.Esc(x.Ticket.Summary));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{rows.Count} open ticket(s)[/]");
        return 0;
    }
}

public sealed class TicketApproveCommand : Command<TicketApproveCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--undo")]
        [Description("Put it back to proposed instead.")]
        public bool Undo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Undo) db.Unapprove(settings.Id); else db.Approve(settings.Id);
        Console.WriteLine($"{db.Ticket(settings.Id)!.Value.Ticket.Id} {db.QueueState(settings.Id).Approval}");
        return 0;
    }
}

public sealed class TicketRejectCommand : Command<TicketRejectCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--reason <TEXT>")]
        [Description("Why; becomes the ticket's ref as it closes wont-do.")]
        public string Reason { get; init; } = "";

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(Reason) ? ValidationResult.Error("--reason is required") : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var t = db.Reject(settings.Id, settings.Reason);
        Db.Rerender(db, settings);
        Console.WriteLine($"{t.Id} rejected ({t.Status})");
        return 0;
    }
}

public sealed class TicketPriorityCommand : Command<TicketPriorityCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandArgument(1, "<PRIORITY>")]
        [Description("Whole number; higher runs sooner (0 is the default).")]
        public long Priority { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        db.SetPriority(settings.Id, settings.Priority);
        Console.WriteLine($"{db.Ticket(settings.Id)!.Value.Ticket.Id} priority {settings.Priority}");
        return 0;
    }
}
