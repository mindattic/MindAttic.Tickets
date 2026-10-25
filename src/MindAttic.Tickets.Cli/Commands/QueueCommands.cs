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
                    approval = x.Approval, priority = x.Priority, estimate = x.Estimate, summary = x.Ticket.Summary, @ref = x.Ticket.Ref,
                    assignee = w.Assignee, reserved = w.Reserved, requires = db.Requires(x.Ticket.Id),
                    working = w.IsWorking ? new { login = w.Login, session = w.Session, since = w.Since } : null,
                };
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("#", "Priority", "Estimate", "Approval", "ID", "Type", "Summary");
        var n = 0;
        foreach (var x in rows)
            table.AddRow((++n).ToString(System.Globalization.CultureInfo.InvariantCulture), x.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x.Estimate is { } e ? TokenText.Format(e) : "[grey]—[/]",
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

/// <summary>
/// <c>tickets ticket estimate &lt;ID&gt; [&lt;TOKENS&gt;|--clear]</c>: show, set or clear a ticket's effort estimate (M.TIX-T75);
/// <c>tickets ticket estimate --seed</c>: give each open ticket without one the median cost of finished tickets like it, from
/// the Orchestrator's TicketCosts. Within one priority the queue runs the smallest estimate first.
/// </summary>
public sealed class TicketEstimateCommand : Command<TicketEstimateCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "[ID]")]
        [Description("The ticket. Omit it with --seed.")]
        public string? Id { get; init; }

        [CommandArgument(1, "[TOKENS]")]
        [Description("Its estimate in uncached tokens, like 300k or 1.2M. Omit it to show the current one.")]
        public string? Tokens { get; init; }

        [CommandOption("--clear")]
        [Description("Take the ticket's estimate away.")]
        public bool Clear { get; init; }

        [CommandOption("--seed")]
        [Description("Estimate every open ticket that has none from the Orchestrator's TicketCosts medians.")]
        public bool Seed { get; init; }

        [CommandOption("--repo <NAME>")]
        [Description("With --seed: only this repo's tickets.")]
        public string? Repo { get; init; }

        [CommandOption("--overwrite")]
        [Description("With --seed: re-estimate tickets that already have one too.")]
        public bool Overwrite { get; init; }

        [CommandOption("--dry-run")]
        [Description("With --seed: show what each would be given; nothing is written.")]
        public bool DryRun { get; init; }

        public override ValidationResult Validate()
        {
            if (Seed)
                return Id is not null || Tokens is not null || Clear ? ValidationResult.Error("--seed takes no ID, TOKENS or --clear") : ValidationResult.Success();
            if (Repo is not null || Overwrite || DryRun) return ValidationResult.Error("--repo, --overwrite and --dry-run go with --seed");
            if (Id is null) return ValidationResult.Error("give a ticket ID, or --seed");
            if (Clear && Tokens is not null) return ValidationResult.Error("--clear takes no TOKENS");
            if (Tokens is not null && (!TokenText.TryParse(Tokens, out var t) || t > TicketsDb.MaxEstimate))
                return ValidationResult.Error("<TOKENS>: a number like 300k, 1.2M or 0");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Seed) return Seed(db, settings);
        var id = db.Ticket(settings.Id!)?.Ticket.Id ?? throw new KeyNotFoundException($"No ticket {settings.Id}.");
        if (settings.Clear) db.SetEstimate(id, null);
        else if (settings.Tokens is { } text)
        {
            TokenText.TryParse(text, out var tokens);
            db.SetEstimate(id, tokens);
        }
        Console.WriteLine(db.Estimate(id) is { } e ? $"{id} is estimated at {TokenText.Format(e)} tokens." : $"{id} has no estimate.");
        return 0;
    }

    private static int Seed(TicketsDb db, Settings settings)
    {
        var (costs, source) = OrchestratorCosts.Load(settings.Workspace.Root);
        var seed = new EstimateSeed(costs);
        if (seed.Tickets == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]The Orchestrator has no ticket costs yet ({Output.Esc(source)}); nothing was estimated.[/]");
            return 0;
        }
        var given = db.SeedEstimates(costs, settings.Repo, settings.Overwrite, settings.DryRun);
        foreach (var g in given)
            AnsiConsole.MarkupLine($"{(settings.DryRun ? "would estimate" : "estimated")} [bold]{Output.Esc(g.TicketId)}[/] at " +
                                   $"{TokenText.Format(g.Estimate.Tokens)} [grey](median of {g.Estimate.Basis} {Output.Esc(g.Estimate.Of)})[/]");
        AnsiConsole.MarkupLine($"[grey]{given.Count} ticket(s) {(settings.DryRun ? "would be estimated; nothing was written" : "estimated")}, " +
                               $"from {seed.Tickets} finished ticket(s) in {Output.Esc(source)}.[/]");
        return 0;
    }
}
