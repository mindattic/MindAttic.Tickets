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
            // Assignee, reservation, prerequisites and the session working it: what the Orchestrator reads to run the work (M.TIX-T134).
            var work = db.WorkStates();
            Console.WriteLine(JsonSerializer.Serialize(rows.Select(x =>
            {
                var w = work.GetValueOrDefault(x.Ticket.Id) ?? TicketWork.None;
                return new
                {
                    repo = x.Repo, id = x.Ticket.Id, date = x.Ticket.Date, type = x.Ticket.Type,
                    approval = x.Approval, priority = x.Priority, summary = x.Ticket.Summary, @ref = x.Ticket.Ref,
                    assignee = w.Assignee, reserved = w.Reserved, requires = db.Requires(x.Ticket.Id),
                    working = w.IsWorking ? new { login = w.Login, session = w.Session, since = w.Since } : null,
                };
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
        [CommandArgument(0, "[IDS]")]
        [Description("Ticket IDs to approve; none lists every open ticket waiting for approval (fixes need none).")]
        public string[] Ids { get; init; } = [];

        [CommandOption("--undo")]
        [Description("Put them back to proposed instead.")]
        public bool Undo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Ids.Length == 0)
        {
            var waiting = db.Queue().Where(q => q.Approval == TicketsDb.Proposed && q.Ticket.Type != "fix").ToList();
            AnsiConsole.MarkupLine($"{waiting.Count} ticket(s) wait for approval (fixes need none):");
            foreach (var q in waiting)
                AnsiConsole.MarkupLine($"  [bold]{Output.Esc(q.Ticket.Id)}[/] {Output.Esc(q.Repo)} ({Output.Esc(q.Ticket.Type)}): {Output.Esc(q.Ticket.Summary)}");
            AnsiConsole.MarkupLine("[grey]tickets ticket approve <ID> [[<ID> ...]] lets them be worked.[/]");
            return 0;
        }
        // Every ID checked before anything changes (M.TIX-T134, as orchestrator approve did): a typo would otherwise be
        // "approved" beside the real ones while the ticket meant goes on waiting.
        var ids = settings.Ids.Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Where(i => db.Ticket(i) is null).ToList() is { Count: > 0 } unknown)
            throw new KeyNotFoundException($"No ticket {string.Join(", ", unknown)}; nothing was changed.");
        if (!settings.Undo && ids.Where(i => db.Ticket(i)!.Value.Ticket.Status != Log.TicketRules.Open).ToList() is { Count: > 0 } shut)
            throw new InvalidOperationException($"{string.Join(", ", shut)} {(shut.Count == 1 ? "isn't" : "aren't")} open; nothing was changed.");
        foreach (var id in ids)
        {
            if (settings.Undo) db.Unapprove(id); else db.Approve(id);
            Console.WriteLine($"{db.Ticket(id)!.Value.Ticket.Id} {db.QueueState(db.Ticket(id)!.Value.Ticket.Id).Approval}");
        }
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
