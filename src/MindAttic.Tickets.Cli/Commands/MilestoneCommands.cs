using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

// Milestones (M.TIX-T335): `tickets milestone new|list|show|activate|accept|drop|edit|add|remove` and
// `tickets milestone check add|list|result|remove`. The store and its checks only; the Orchestrator runs the checks.

public class MilestoneSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("Milestone ID (MS-1), or its exact name.")]
    public string Id { get; init; } = "";
}

internal static class MilestoneOutput
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string State(string state) => state switch
    {
        MilestoneStates.Active => "[yellow]active[/]",
        MilestoneStates.Accepting => "[cyan]accepting[/]",
        MilestoneStates.Reached => "[green]reached[/]",
        MilestoneStates.Dropped => "[grey]dropped[/]",
        _ => Output.Esc(state),
    };

    public static string Result(string result) => result switch
    {
        MilestoneCheckResults.Pass => "[green]pass[/]",
        MilestoneCheckResults.Fail => "[red]fail[/]",
        _ => "[grey]unknown[/]",
    };

    public static object Json(MilestoneCheck c) => new
    {
        number = c.Number, kind = c.Kind, description = c.Description, repos = c.Repos, target = c.Target,
        result = c.Result, @checked = c.Checked, evidence = c.Evidence,
    };

    /// <summary>A milestone as <c>list --json</c> and <c>show --json</c> print it: its row, its member tickets with a done/total
    /// count, and its checks; <paramref name="history"/> adds its history (show only).</summary>
    public static object Json(TicketsDb db, Milestone m, TicketGraph graph, bool history)
    {
        var members = db.MilestoneMembers(m.Id, graph);
        var (done, total) = TicketsDb.MilestoneProgress(members);
        var checks = db.MilestoneChecks(m.Id);
        return new
        {
            id = m.Id, name = m.Name, goal = m.Goal, repos = m.Repos, order = m.Order, state = m.State,
            created = m.Created, updated = m.Updated, activated = m.Activated, reached = m.Reached, reachedBy = m.ReachedBy,
            done, total,
            members = members.Select(x => new
            {
                id = x.Id, isTicket = x.IsTicket, direct = x.Direct, repo = x.Node.Repo, type = x.Node.Type, status = x.Node.Status,
                name = x.Node.Name, summary = x.Node.Summary,
            }),
            checksPassed = checks.Count(c => c.Passed),
            checks = checks.Select(Json),
            history = history ? db.MilestoneHistory(m.Id).Select(e => new { at = e.At, field = e.Field, old = e.Old, @new = e.New }) : null,
        };
    }
}

/// <summary><c>tickets milestone new --name … --repo …</c>: adds a planned milestone and prints its ID.</summary>
public sealed class MilestoneNewCommand : Command<MilestoneNewCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--name <TEXT>")]
        [Description("Its name: the finished thing, in a few words.")]
        public string Name { get; init; } = "";

        [CommandOption("--goal <TEXT>")]
        [Description("What reaching it means, and the lasting goal it's on the way to.")]
        public string? Goal { get; init; }

        [CommandOption("--repo <NAME>")]
        [Description("A repo it spans (repeatable). Default: the repo containing the current directory.")]
        public string[] Repo { get; init; } = [];

        [CommandOption("--order <N>")]
        [Description("Where it sorts among milestones, lower first. Default: after every other.")]
        public long? Order { get; init; }

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(Name) ? ValidationResult.Error("--name is required") : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var repos = settings.Repo.Length > 0 ? settings.Repo
            : [(settings.Workspace.Containing(Environment.CurrentDirectory)
                ?? throw new InvalidOperationException($"The current directory isn't inside a repo under {settings.Workspace.Root}; pass --repo.")).Name];
        Console.WriteLine(db.AddMilestone(settings.Name, settings.Goal, repos, settings.Order).Id);
        return 0;
    }
}

/// <summary><c>tickets milestone list</c>: every milestone in order, with its state, progress and checks.</summary>
public sealed class MilestoneListCommand : Command<MilestoneListCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--state <STATE>")]
        [Description("planned | active | accepting | reached | dropped. Default: every state.")]
        public string? State { get; init; }

        [CommandOption("--repo <NAME>")]
        [Description("Only milestones that span this repo.")]
        public string? Repo { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }

        public override ValidationResult Validate() =>
            State is not null && !MilestoneStates.All.Contains(State) ? ValidationResult.Error($"--state must be one of {string.Join(", ", MilestoneStates.All)}")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var milestones = db.Milestones(settings.State)
            .Where(m => settings.Repo is null || m.Repos.Contains(settings.Repo, StringComparer.OrdinalIgnoreCase)).ToList();
        var graph = db.Graph();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(milestones.Select(m => MilestoneOutput.Json(db, m, graph, history: false)), MilestoneOutput.Indented));
            return 0;
        }
        if (milestones.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No milestones; add one with 'tickets milestone new --name … --repo …'.[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Order", "ID", "State", "Name", "Repos", "Tickets", "Checks", "Activated");
        foreach (var m in milestones)
        {
            var (done, total) = TicketsDb.MilestoneProgress(db.MilestoneMembers(m.Id, graph));
            var checks = db.MilestoneChecks(m.Id);
            table.AddRow(m.Order.ToString(CultureInfo.InvariantCulture), Output.Esc(m.Id), MilestoneOutput.State(m.State), Output.Esc(m.Name),
                Output.Esc(string.Join(", ", m.Repos)), $"{done}/{total}", $"{checks.Count(c => c.Passed)}/{checks.Count}", Output.Esc(m.Activated ?? ""));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{milestones.Count} milestone(s)[/]");
        return 0;
    }
}

/// <summary><c>tickets milestone show &lt;ID&gt;</c>: one milestone, its member tickets with a done/total count, its checks and history.</summary>
public sealed class MilestoneShowCommand : Command<MilestoneShowCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var m = db.Milestone(settings.Id) ?? throw new KeyNotFoundException($"No milestone {settings.Id}.");
        var graph = db.Graph();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(MilestoneOutput.Json(db, m, graph, history: true), MilestoneOutput.Indented));
            return 0;
        }
        var members = db.MilestoneMembers(m.Id, graph);
        var (done, total) = TicketsDb.MilestoneProgress(members);
        var checks = db.MilestoneChecks(m.Id);
        AnsiConsole.MarkupLine($"[bold]{Output.Esc(m.Id)}[/] · [bold]{Output.Esc(m.Name)}[/] · {MilestoneOutput.State(m.State)} · order {m.Order.ToString(CultureInfo.InvariantCulture)}");
        if (m.Goal.Length > 0) AnsiConsole.WriteLine(m.Goal);
        AnsiConsole.MarkupLine($"[grey]Repos:[/] {Output.Esc(string.Join(", ", m.Repos))}");
        AnsiConsole.MarkupLine($"[grey]Created:[/] {Output.Esc(m.Created)}" + (m.Activated is { } on ? $" · [grey]activated:[/] {Output.Esc(on)}" : "") +
            (m.Reached is { } at ? $" · [grey]reached:[/] {Output.Esc(at)} [grey]by[/] {Output.Esc(m.ReachedBy ?? "unknown")}" : ""));
        AnsiConsole.MarkupLine($"{Output.Heading("Tickets")} ({done} of {total} done)");
        if (members.Count == 0) AnsiConsole.MarkupLine("  [grey]None; add them with 'tickets milestone add <ID> <TICKET>...'.[/]");
        foreach (var x in members)
            AnsiConsole.MarkupLine($"  {Output.Esc(x.Node.Line)}" + (x.Node.Repo is { } repo ? $" [grey]({Output.Esc(repo)})[/]" : "") + (x.Direct ? "" : " [grey]through its parent[/]"));
        AnsiConsole.MarkupLine($"{Output.Heading("Acceptance checks")} ({checks.Count(c => c.Passed)} of {checks.Count} pass)");
        if (checks.Count == 0) AnsiConsole.MarkupLine("  [grey]None; add them with 'tickets milestone check add <ID> --kind … --description …'.[/]");
        foreach (var c in checks) AnsiConsole.MarkupLine(MilestoneCheckListCommand.Line(c));
        foreach (var e in db.MilestoneHistory(m.Id))
            AnsiConsole.MarkupLine($"  [grey]{Output.Esc(e.At)} {Output.Esc(e.Field)}: {Output.Esc(e.Old ?? "")} → {Output.Esc(e.New ?? "")}[/]");
        return 0;
    }
}

/// <summary><c>tickets milestone activate &lt;ID&gt;</c>: planned → active (or back from acceptance).</summary>
public sealed class MilestoneActivateCommand : Command<MilestoneSettings>
{
    public override int Execute(CommandContext context, MilestoneSettings settings)
    {
        using var db = Db.Open(settings);
        var m = db.ActivateMilestone(settings.Id);
        Console.WriteLine($"{m.Id} {m.State}");
        return 0;
    }
}

/// <summary><c>tickets milestone accept &lt;ID&gt;</c>: active → accepting; <c>--reached</c> accepts it as reached.</summary>
public sealed class MilestoneAcceptCommand : Command<MilestoneAcceptCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandOption("--reached")]
        [Description("Accept it as reached (from active or accepting); refused while any acceptance check is fail or unknown.")]
        public bool Reached { get; init; }

        [CommandOption("--by <NAME>")]
        [Description("Who accepts it as reached. Default: $MINDATTIC_TICKETS_AUTHOR, else this machine's user name.")]
        public string? By { get; init; }

        [CommandOption("--force")]
        [Description("With --reached: reach it though checks haven't passed (they're named in its history).")]
        public bool Force { get; init; }

        public override ValidationResult Validate() =>
            !Reached && (Force || By is not null) ? ValidationResult.Error("--by and --force go with --reached") : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var m = db.AcceptMilestone(settings.Id, settings.Reached, settings.Reached ? settings.By ?? TicketNoteCommand.DefaultAuthor() : null, settings.Force);
        Console.WriteLine(m.Reached is null ? $"{m.Id} {m.State}" : $"{m.Id} {m.State} {m.Reached} by {m.ReachedBy}");
        return 0;
    }
}

/// <summary><c>tickets milestone drop &lt;ID&gt;</c>: a milestone that won't be reached; it stays on record.</summary>
public sealed class MilestoneDropCommand : Command<MilestoneDropCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandOption("--reason <TEXT>")]
        [Description("Why, kept in its history.")]
        public string? Reason { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var m = db.DropMilestone(settings.Id, settings.Reason);
        Console.WriteLine($"{m.Id} {m.State}");
        return 0;
    }
}

/// <summary><c>tickets milestone edit &lt;ID&gt;</c>: its name, goal, order or repos.</summary>
public sealed class MilestoneEditCommand : Command<MilestoneEditCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandOption("--name <TEXT>")]
        public string? Name { get; init; }

        [CommandOption("--goal <TEXT>")]
        public string? Goal { get; init; }

        [CommandOption("--order <N>")]
        [Description("Where it sorts among milestones, lower first.")]
        public long? Order { get; init; }

        [CommandOption("--repo <NAME>")]
        [Description("The repos it spans (repeatable): replaces the ones it had.")]
        public string[] Repo { get; init; } = [];

        public override ValidationResult Validate() =>
            Name is null && Goal is null && Order is null && Repo.Length == 0 ? ValidationResult.Error("give --name, --goal, --order or --repo") : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var m = db.EditMilestone(settings.Id, settings.Name, settings.Goal, settings.Order, settings.Repo.Length > 0 ? settings.Repo : null);
        Console.WriteLine($"{m.Id} · {m.Name} · order {m.Order.ToString(CultureInfo.InvariantCulture)} · {string.Join(", ", m.Repos)}");
        return 0;
    }
}

public sealed class MilestoneMemberSettings : MilestoneSettings
{
    [CommandArgument(1, "<ITEMS>")]
    [Description("One or more ticket IDs (M.TIX-T5) or epic IDs (CODE-EPIC-KEY). A ticket's children come with it.")]
    public string[] Items { get; init; } = [];
}

/// <summary><c>tickets milestone add &lt;ID&gt; &lt;TICKET&gt;...</c>: each becomes part-of the milestone.</summary>
public sealed class MilestoneAddCommand : Command<MilestoneMemberSettings>
{
    public override int Execute(CommandContext context, MilestoneMemberSettings settings)
    {
        using var db = Db.Open(settings);
        var members = db.AddToMilestone(settings.Id, settings.Items);
        var (done, total) = TicketsDb.MilestoneProgress(members);
        Console.WriteLine($"{db.Milestone(settings.Id)!.Id}: {done} of {total} ticket(s) done");
        return 0;
    }
}

/// <summary><c>tickets milestone remove &lt;ID&gt; &lt;TICKET&gt;...</c>: takes each off the milestone.</summary>
public sealed class MilestoneRemoveCommand : Command<MilestoneMemberSettings>
{
    public override int Execute(CommandContext context, MilestoneMemberSettings settings)
    {
        using var db = Db.Open(settings);
        var members = db.RemoveFromMilestone(settings.Id, settings.Items);
        var (done, total) = TicketsDb.MilestoneProgress(members);
        Console.WriteLine($"{db.Milestone(settings.Id)!.Id}: {done} of {total} ticket(s) done");
        return 0;
    }
}

/// <summary><c>tickets milestone check add &lt;ID&gt; --kind … --description …</c>: an acceptance check, its result unknown.</summary>
public sealed class MilestoneCheckAddCommand : Command<MilestoneCheckAddCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandOption("--kind <KIND>")]
        [Description("build (a build that passes on main in the --repo repos) | exists (a file or package, --target) | harness (a harness run that reaches the --target state) | signoff (the owner's sign-off).")]
        public string Kind { get; init; } = "";

        [CommandOption("--description <TEXT>")]
        [Description("What must be true, in one line.")]
        public string Description { get; init; } = "";

        [CommandOption("--repo <NAME>")]
        [Description("A repo the check looks at (repeatable): required for build.")]
        public string[] Repo { get; init; } = [];

        [CommandOption("--target <TEXT>")]
        [Description("What it looks for: a path or package (exists), or the state a harness run must reach (harness).")]
        public string? Target { get; init; }

        public override ValidationResult Validate() =>
            !MilestoneCheckKinds.All.Contains(Kind) ? ValidationResult.Error($"--kind must be one of {string.Join(", ", MilestoneCheckKinds.All)}")
            : string.IsNullOrWhiteSpace(Description) ? ValidationResult.Error("--description is required")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var c = db.AddMilestoneCheck(settings.Id, settings.Kind, settings.Description, settings.Repo, settings.Target);
        Console.WriteLine($"{c.MilestoneId} #{c.Number.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }
}

/// <summary><c>tickets milestone check list &lt;ID&gt;</c>: its acceptance checks and what each last came to.</summary>
public sealed class MilestoneCheckListCommand : Command<MilestoneCheckListCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    /// <summary>One check in a line: number, kind, result, what it asks, what it looks at, then when it was checked and the evidence.</summary>
    public static string Line(MilestoneCheck c) =>
        $"  #{c.Number.ToString(CultureInfo.InvariantCulture)} [cyan]{Output.Esc(c.Kind)}[/] {MilestoneOutput.Result(c.Result)}  {Output.Esc(c.Description)}" +
        (c.Repos.Count > 0 ? $" [grey]in {Output.Esc(string.Join(", ", c.Repos))}[/]" : "") +
        (c.Target is { } target ? $" [grey]→ {Output.Esc(target)}[/]" : "") +
        (c.Checked is { } at ? $"{Environment.NewLine}    [grey]checked {Output.Esc(at)}{(c.Evidence is { } e ? ": " + Output.Esc(e) : "")}[/]" : "");

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var checks = db.MilestoneChecks(settings.Id);
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(checks.Select(MilestoneOutput.Json), MilestoneOutput.Indented));
            return 0;
        }
        if (checks.Count == 0) AnsiConsole.MarkupLine("[grey]No acceptance checks.[/]");
        foreach (var c in checks) AnsiConsole.MarkupLine(Line(c));
        return 0;
    }
}

/// <summary><c>tickets milestone check result &lt;ID&gt; &lt;N&gt; pass|fail|unknown</c>: records what a check came to.</summary>
public sealed class MilestoneCheckResultCommand : Command<MilestoneCheckResultCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandArgument(1, "<N>")]
        [Description("The check's number, as 'milestone check list' shows it.")]
        public long Number { get; init; }

        [CommandArgument(2, "<RESULT>")]
        [Description("pass | fail | unknown")]
        public string Result { get; init; } = "";

        [CommandOption("--evidence <TEXT>")]
        [Description("A short note of what showed it: a commit, a build, who signed off.")]
        public string? Evidence { get; init; }

        public override ValidationResult Validate() =>
            MilestoneCheckResults.All.Contains(Result) ? ValidationResult.Success()
                : ValidationResult.Error($"RESULT must be one of {string.Join(", ", MilestoneCheckResults.All)}");
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var c = db.SetMilestoneCheckResult(settings.Id, settings.Number, settings.Result, settings.Evidence);
        Console.WriteLine($"{c.MilestoneId} #{c.Number.ToString(CultureInfo.InvariantCulture)} {c.Result}");
        return 0;
    }
}

/// <summary><c>tickets milestone check remove &lt;ID&gt; &lt;N&gt;</c>: takes a check off a milestone.</summary>
public sealed class MilestoneCheckRemoveCommand : Command<MilestoneCheckRemoveCommand.Settings>
{
    public sealed class Settings : MilestoneSettings
    {
        [CommandArgument(1, "<N>")]
        [Description("The check's number, as 'milestone check list' shows it.")]
        public long Number { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var gone = db.RemoveMilestoneCheck(settings.Id, settings.Number);
        Console.WriteLine(gone ? $"removed check #{settings.Number.ToString(CultureInfo.InvariantCulture)}" : "no such check");
        return gone ? 0 : 1;
    }
}
