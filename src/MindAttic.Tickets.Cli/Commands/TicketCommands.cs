using System.ComponentModel;
using System.Text.Json;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

public sealed class TicketNewCommand : Command<TicketNewCommand.Settings>
{
    public sealed class Settings : RepoSettings
    {
        [CommandOption("--type <TYPE>")]
        [Description("feature | fix | chore | docs | interview")]
        public string Type { get; init; } = "";

        [CommandOption("--summary <TEXT>")]
        [Description("One line: what changed and why.")]
        public string Summary { get; init; } = "";

        [CommandOption("--ref <TEXT>")]
        [Description("Story ID, test name and/or commit hash.")]
        public string? Ref { get; init; }

        [CommandOption("--status <STATUS>")]
        [Description("open (default) | closed | wont-do")]
        public string Status { get; init; } = TicketRules.Open;

        public override ValidationResult Validate() =>
            !TicketRules.Types.Contains(Type) ? ValidationResult.Error($"--type must be one of {string.Join(", ", TicketRules.Types)}")
            : string.IsNullOrWhiteSpace(Summary) ? ValidationResult.Error("--summary is required")
            : !TicketRules.Statuses.Contains(Status) ? ValidationResult.Error($"--status must be one of {string.Join(", ", TicketRules.Statuses)}")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var repo = settings.ResolveRepo();
        using (var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root))
            if (db is not null)
            {
                Console.WriteLine(db.Add(repo.Name, settings.Type, settings.Status, settings.Summary, settings.Ref ?? "").Id);
                Db.Rerender(db, settings);
                return 0;
            }
        if (!File.Exists(repo.StoriesPath))
            throw new InvalidOperationException($"{repo.Name} has no docs/USER_STORIES.md; run 'tickets repos rollout --apply' first.");

        var file = StoryFile.Load(repo.StoriesPath);
        var t = file.Add(settings.Type, settings.Status, settings.Summary, settings.Ref ?? "");
        file.Save();
        Console.WriteLine(t.Id);
        return 0;
    }
}

public sealed class TicketCloseCommand : Command<TicketCloseCommand.Settings>
{
    public sealed class Settings : RepoSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Ticket ID, e.g. M.LNCH-T5.")]
        public string Id { get; init; } = "";

        [CommandOption("--ref <TEXT>")]
        [Description("Story ID, test name and/or commit hash. Keeps the existing Ref when omitted.")]
        public string? Ref { get; init; }

        [CommandOption("--wont-do")]
        [Description("Close as wont-do instead of closed.")]
        public bool WontDo { get; init; }

        [CommandOption("--status <STATUS>")]
        [Description("closed | wont-do. Same as --wont-do; accepted so a mistyped --status doesn't silently close as 'closed' instead.")]
        public string? Status { get; init; }

        public override ValidationResult Validate()
        {
            if (Status is null) return ValidationResult.Success();
            if (Status != TicketRules.Closed && Status != TicketRules.WontDo)
                return ValidationResult.Error($"--status must be '{TicketRules.Closed}' or '{TicketRules.WontDo}'");
            if (WontDo && Status != TicketRules.WontDo)
                return ValidationResult.Error("--status and --wont-do disagree");
            return ValidationResult.Success();
        }

        internal bool IsWontDo => WontDo || Status == TicketRules.WontDo;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var status = settings.IsWontDo ? TicketRules.WontDo : TicketRules.Closed;
        using (var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root))
            if (db is not null)
            {
                // Ticket IDs are unique across the workspace, so --repo isn't needed (an agent in a worktree may not be in one).
                var closed = db.Close(settings.Id, status, settings.Ref);
                Db.Rerender(db, settings);
                Console.WriteLine($"{closed.Id} {closed.Status}");
                return 0;
            }
        var repo = settings.ResolveRepo();
        var file = StoryFile.Load(repo.StoriesPath);
        var t = file.Close(settings.Id, status, settings.Ref);
        file.Save();
        Console.WriteLine($"{t.Id} {t.Status}");
        return 0;
    }
}

public sealed class TicketListCommand : Command<TicketListCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Only this repo. Default: every repo.")]
        public string? Repo { get; init; }

        [CommandOption("--status <STATUS>")]
        public string? Status { get; init; }

        [CommandOption("--type <TYPE>")]
        public string? Type { get; init; }

        [CommandOption("--since <YYYY-MM-DD>")]
        [Description("Only tickets dated on or after this day.")]
        public string? Since { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var repos = settings.Workspace.Repos();
        if (!string.IsNullOrWhiteSpace(settings.Repo))
            repos = [.. repos.Where(r => r.Name.Equals(settings.Repo, StringComparison.OrdinalIgnoreCase))];

        var rows = WorkspaceTickets.All(repos)
            .Where(x => settings.Status is null || x.Ticket.Status == settings.Status)
            .Where(x => settings.Type is null || x.Ticket.Type == settings.Type)
            .Where(x => settings.Since is null || string.CompareOrdinal(x.Ticket.Date, settings.Since) >= 0)
            .ToList();

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows.Select(x => new
            {
                repo = x.Repo.Name, id = x.Ticket.Id, date = x.Ticket.Date, type = x.Ticket.Type,
                status = x.Ticket.Status, summary = x.Ticket.Summary, @ref = x.Ticket.Ref,
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("Repo", "ID", "Date", "Type", "Status", "Summary");
        foreach (var x in rows)
        {
            var status = x.Ticket.Status switch
            {
                TicketRules.Open => "[yellow]open[/]",
                TicketRules.Closed => "[green]closed[/]",
                _ => $"[grey]{Output.Esc(x.Ticket.Status)}[/]",
            };
            table.AddRow(Output.Esc(x.Repo.Name), Output.Esc(x.Ticket.Id), Output.Esc(x.Ticket.Date),
                Output.Esc(x.Ticket.Type), status, Output.Esc(x.Ticket.Summary));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{rows.Count} ticket(s)[/]");
        return 0;
    }
}

public sealed class TicketVerifyCommand : Command<RepoSettings>
{
    public override int Execute(CommandContext context, RepoSettings settings)
    {
        var repo = settings.ResolveRepo();
        using (var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root))
            if (db is not null)
            {
                // Append-only is enforced by the database itself; what's left to check is the data.
                var found = db.Check().Where(p => p.StartsWith(repo.Name + ": ", StringComparison.OrdinalIgnoreCase) || !p.Contains(": ", StringComparison.Ordinal) || p.StartsWith("link ", StringComparison.Ordinal)).ToList();
                // And their versions (M.TIX-T137): the history every version is read from.
                found.AddRange(db.CheckVersions(repo.Name));
                foreach (var p in found) AnsiConsole.MarkupLine($"[red]{Output.Esc(p)}[/]");
                if (found.Count == 0)
                    AnsiConsole.MarkupLine($"[green]{Output.Esc(repo.Name)}: tickets OK[/] ({db.Tickets(repo.Name).Count}; " +
                                           (db.IsVersioned() ? "every save kept" : "versions worked back from their history") + ")");
                return found.Count == 0 ? 0 : 1;
            }
        if (!File.Exists(repo.StoriesPath)) { AnsiConsole.MarkupLine($"[red]{Output.Esc(repo.Name)}: no docs/USER_STORIES.md[/]"); return 1; }

        var now = StoryFile.Load(repo.StoriesPath);
        var problems = new List<string>();
        if (now.Code is not { } code) problems.Add("no 'code:' front-matter");
        else if (!now.HasTicketsSection) problems.Add("no '## Tickets' section");
        else
        {
            if (!now.TicketsSectionIsLast) problems.Add("'## Tickets' isn't the last section");
            problems.AddRange(TicketRules.Validate(code, now.Tickets));

            var committed = Git.Run(repo.Path, "show", $"HEAD:{StoryFile.RelativePath}");
            if (committed.Ok)
                problems.AddRange(TicketRules.CheckAppendOnly(StoryFile.Parse(committed.Output).Tickets, now.Tickets));
        }

        foreach (var p in problems) AnsiConsole.MarkupLine($"[red]{Output.Esc(repo.Name)}:[/] {Output.Esc(p)}");
        if (problems.Count == 0) AnsiConsole.MarkupLine($"[green]{Output.Esc(repo.Name)}: tickets OK[/] ({now.Tickets.Count})");
        return problems.Count == 0 ? 0 : 1;
    }
}

public sealed class CodesCheckCommand : Command<WorkspaceSettings>
{
    public override int Execute(CommandContext context, WorkspaceSettings settings)
    {
        var problems = WorkspaceTickets.Check(settings.Workspace.Repos());
        foreach (var p in problems) AnsiConsole.MarkupLine($"[yellow]{Output.Esc(p.Repo)}:[/] {Output.Esc(p.Message)}");
        AnsiConsole.MarkupLine(problems.Count == 0 ? "[green]No problems.[/]" : $"[grey]{problems.Count} problem(s)[/]");
        return problems.Count == 0 ? 0 : 1;
    }
}
