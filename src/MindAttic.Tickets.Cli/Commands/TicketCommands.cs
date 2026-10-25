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

        [CommandOption("--description <TEXT>")]
        [Description("Its markdown description, kept apart from the one-line summary.")]
        public string? Description { get; init; }

        [CommandOption("--description-file <PATH>")]
        [Description("Read the markdown description from this file.")]
        public string? DescriptionFile { get; init; }

        [CommandOption("--template")]
        [Description("Start the description from the type's template (fix: symptom, cause, test; feature: story, acceptance); 'tickets ticket template TYPE' prints it.")]
        public bool Template { get; init; }

        public override ValidationResult Validate() =>
            !TicketRules.Types.Contains(Type) ? ValidationResult.Error($"--type must be one of {string.Join(", ", TicketRules.Types)}")
            : string.IsNullOrWhiteSpace(Summary) ? ValidationResult.Error("--summary is required")
            : (Description is not null ? 1 : 0) + (DescriptionFile is not null ? 1 : 0) + (Template ? 1 : 0) > 1
                ? ValidationResult.Error("--description, --description-file and --template: pick one")
            : !TicketRules.Statuses.Contains(Status) ? ValidationResult.Error($"--status must be one of {string.Join(", ", TicketRules.Statuses)}")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var repo = settings.ResolveRepo();
        using (var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root))
            if (db is not null)
            {
                WarnOfDuplicates(settings, db.Tickets(repo.Name));
                var description = settings.Template ? TicketTemplates.For(settings.Type)
                    : settings.Description ?? (settings.DescriptionFile is { } path ? File.ReadAllText(path) : null);
                Console.WriteLine(db.Add(repo.Name, settings.Type, settings.Status, settings.Summary, settings.Ref ?? "", description: description).Id);
                Db.Rerender(db, settings);
                return 0;
            }
        if (!File.Exists(repo.StoriesPath))
            throw new InvalidOperationException($"{repo.Name} has no docs/USER_STORIES.md; run 'tickets repos rollout --apply' first.");
        if (settings.Description is not null || settings.DescriptionFile is not null || settings.Template)
            throw new InvalidOperationException("Descriptions live in the ticket database; this workspace hasn't been migrated to it.");

        var file = StoryFile.Load(repo.StoriesPath);
        WarnOfDuplicates(settings, file.Tickets.Select(t => (repo.Name, t)));
        var added = file.Add(settings.Type, settings.Status, settings.Summary, settings.Ref ?? "");
        file.Save();
        Console.WriteLine(added.Id);
        return 0;
    }

    /// <summary>
    /// Warns (on stderr, so stdout stays just the new ID) when an open ticket in the repo already reads like this one
    /// (M.TIX-T88). It still files: logging finished work as closed skips the check.
    /// </summary>
    private static void WarnOfDuplicates(Settings settings, IEnumerable<(string Repo, Ticket Ticket)> existing)
    {
        if (settings.Status != TicketRules.Open) return;
        var similar = TicketSimilarity.Similar(settings.Summary, existing);
        if (similar.Count == 0) return;
        Console.Error.WriteLine($"warning: {similar.Count} open ticket(s) read like this one; filing it anyway:");
        foreach (var s in similar)
            Console.Error.WriteLine($"  {s.Ticket.Id} ({s.Score:P0} alike) {s.Ticket.Summary}");
    }
}

/// <summary><c>tickets ticket template TYPE</c> (M.TIX-T88): prints the description template for a ticket type.</summary>
public sealed class TicketTemplateCommand : Command<TicketTemplateCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<TYPE>")]
        [Description("feature | fix | chore | docs | interview")]
        public string Type { get; init; } = "";

        public override ValidationResult Validate() =>
            TicketRules.Types.Contains(Type) ? ValidationResult.Success()
                : ValidationResult.Error($"TYPE must be one of {string.Join(", ", TicketRules.Types)}");
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        Console.Write(TicketTemplates.For(settings.Type));
        return 0;
    }
}

/// <summary><c>tickets ticket describe ID</c> (M.TIX-T73): sets, replaces or clears a ticket's markdown description.</summary>
public sealed class TicketDescribeCommand : Command<TicketDescribeCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Ticket ID, e.g. M.LNCH-T5.")]
        public string Id { get; init; } = "";

        [CommandOption("--text <TEXT>")]
        [Description("The markdown description.")]
        public string? Text { get; init; }

        [CommandOption("--file <PATH>")]
        [Description("Read the markdown description from this file.")]
        public string? File { get; init; }

        [CommandOption("--stdin")]
        [Description("Read the markdown description from standard input.")]
        public bool Stdin { get; init; }

        [CommandOption("--clear")]
        [Description("Remove the description.")]
        public bool Clear { get; init; }

        public override ValidationResult Validate() =>
            (Text is not null ? 1 : 0) + (File is not null ? 1 : 0) + (Stdin ? 1 : 0) + (Clear ? 1 : 0) != 1
                ? ValidationResult.Error("give exactly one of --text, --file, --stdin or --clear")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var body = settings.Clear ? null : settings.Text ?? (settings.Stdin ? Console.In.ReadToEnd() : System.IO.File.ReadAllText(settings.File!));
        var changed = db.SetDescription(settings.Id, body);
        var id = db.Ticket(settings.Id)!.Value.Ticket.Id;
        Console.WriteLine(!changed ? $"{id} description unchanged" : db.Description(id) is null ? $"{id} description cleared" : $"{id} description set");
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

        [CommandOption("--query <QUERY>")]
        [Description("A ticket query: repo:NAME|CODE type:fix status:open label:ui approval:approved and free text ('quoted' to keep spaces); commas give alternatives (status:open,wont-do), a leading - negates (-type:chore; a query that starts with one is passed as --query=-type:chore), * is a wildcard in repo and label.")]
        public string? Query { get; init; }

        [CommandOption("--filter <NAME>")]
        [Description("A saved filter's query (see 'tickets filter list'); combined with --query when both are given.")]
        public string? Filter { get; init; }

        [CommandOption("--label <LABEL>")]
        [Description("Only tickets with this label (repeatable: tickets with every one named). A component is a 'component:' label.")]
        public string[] Label { get; init; } = [];

        [CommandOption("--json")]
        public bool Json { get; init; }

        [CommandOption("--format <FORMAT>")]
        [Description("table (default) | json | csv | md: csv and md export the filtered list (M.TIX-T87).")]
        public string? Format { get; init; }

        [CommandOption("--output <PATH>")]
        [Description("Write the list to this file (UTF-8) instead of standard output; its extension (.csv, .md) picks the format when --format isn't given.")]
        public string? Output { get; init; }

        /// <summary>The format asked for: --format, else --json, else the --output file's extension, else table.</summary>
        internal string EffectiveFormat =>
            Format?.Trim().ToLowerInvariant() is { Length: > 0 } f ? (f == "markdown" ? TicketExport.Markdown : f)
            : Json ? "json"
            : Output is not null && TicketExport.FormatOf(Output) is { } byExt ? byExt
            : "table";

        public override ValidationResult Validate() =>
            Label.FirstOrDefault(l => TicketsDb.NormalizeLabel(l) is null) is { } bad
                ? ValidationResult.Error($"--label \"{bad}\" isn't a label (a letter or digit first, then letters, digits and - _ . : /)")
            : EffectiveFormat is not ("table" or "json" or TicketExport.Csv or TicketExport.Markdown)
                ? ValidationResult.Error("--format must be table, json, csv or md")
            : Json && Format is not null && EffectiveFormat != "json"
                ? ValidationResult.Error("--json and --format disagree")
            : Output is not null && EffectiveFormat == "table"
                ? ValidationResult.Error("--output needs --format json, csv or md (or a .csv or .md file name)")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var repos = settings.Workspace.Repos();
        if (!string.IsNullOrWhiteSpace(settings.Repo))
            repos = [.. repos.Where(r => r.Name.Equals(settings.Repo, StringComparison.OrdinalIgnoreCase))];

        // Labels live only in the ticket database (M.TIX-T74).
        IReadOnlyDictionary<string, IReadOnlyList<string>> labels;
        using (var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root))
            labels = db?.AllLabels() ?? (settings.Label.Length > 0
                ? throw new InvalidOperationException("Labels live in the ticket database, and this workspace isn't migrated to it yet; run 'tickets db migrate'.")
                : new Dictionary<string, IReadOnlyList<string>>());
        IReadOnlyList<string> LabelsOf(string id) => labels.TryGetValue(id, out var l) ? l : [];
        var wanted = settings.Label.Select(l => TicketsDb.NormalizeLabel(l)!).ToList();

        var rows = WorkspaceTickets.All(repos)
            .Where(x => settings.Status is null || x.Ticket.Status == settings.Status)
            .Where(x => settings.Type is null || x.Ticket.Type == settings.Type)
            .Where(x => settings.Since is null || string.CompareOrdinal(x.Ticket.Date, settings.Since) >= 0)
            .Where(x => wanted.All(LabelsOf(x.Ticket.Id).Contains))
            .ToList();

        if (!string.IsNullOrWhiteSpace(settings.Query) || !string.IsNullOrWhiteSpace(settings.Filter))
        {
            using var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root);
            var text = settings.Query ?? "";
            if (!string.IsNullOrWhiteSpace(settings.Filter))
            {
                if (db is null) throw new InvalidOperationException("Saved filters live in the ticket database, and this workspace isn't migrated yet.");
                var saved = db.FindFilter(settings.Filter)
                    ?? throw new KeyNotFoundException($"No saved filter '{settings.Filter}'; 'tickets filter list' shows them.");
                text = saved.Query + " " + text;
            }
            var query = TicketQuery.Parse(text);
            // Approval and labels live only in the database; before migration every ticket is proposed and unlabelled.
            var states = db?.QueueStates();
            rows = [.. rows.Where(x => query.Matches(new TicketFacts(x.Repo.Name, x.Ticket,
                states is not null && states.TryGetValue(x.Ticket.Id, out var s) ? s.Approval : TicketsDb.Proposed,
                labels.GetValueOrDefault(x.Ticket.Id))))];
        }

        var format = settings.EffectiveFormat;
        if (format != "table")
        {
            var text = format == "json"
                ? JsonSerializer.Serialize(rows.Select(x => new
                {
                    repo = x.Repo.Name, id = x.Ticket.Id, date = x.Ticket.Date, type = x.Ticket.Type,
                    status = x.Ticket.Status, summary = x.Ticket.Summary, @ref = x.Ticket.Ref, labels = LabelsOf(x.Ticket.Id),
                }), new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine
                : TicketExport.Write(format, rows.Select(x => new ExportRow(x.Repo.Name, x.Ticket, LabelsOf(x.Ticket.Id))));
            if (settings.Output is { } path)
            {
                File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: format == TicketExport.Csv));
                // stderr, so a pipeline reading stdout sees nothing but the export.
                Console.Error.WriteLine($"{rows.Count} ticket(s) written to {Path.GetFullPath(path)}");
            }
            else Console.Write(text);
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("Repo", "ID", "Date", "Type", "Status", "Summary", "Labels");
        foreach (var x in rows)
        {
            var status = x.Ticket.Status switch
            {
                TicketRules.Open => "[yellow]open[/]",
                TicketRules.Closed => "[green]closed[/]",
                _ => $"[grey]{Output.Esc(x.Ticket.Status)}[/]",
            };
            table.AddRow(Output.Esc(x.Repo.Name), Output.Esc(x.Ticket.Id), Output.Esc(x.Ticket.Date),
                Output.Esc(x.Ticket.Type), status, Output.Esc(x.Ticket.Summary), Output.Esc(string.Join(", ", LabelsOf(x.Ticket.Id))));
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
