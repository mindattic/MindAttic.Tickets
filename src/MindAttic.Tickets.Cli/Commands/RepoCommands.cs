using System.ComponentModel;
using System.Text.Json;
using MindAttic.Tickets.Repos;
using MindAttic.Tickets.Log;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

public sealed class ReposStatusCommand : Command<ReposStatusCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var statuses = settings.Workspace.Repos().Select(RepoStatus.Read).ToList();
        var github = Output.TryGitHub(required: false, statuses.Select(s => s.GitHubSlug).OfType<string>());

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(statuses.Select(s =>
            {
                GitHubRepoInfo? info = null;
                if (s.GitHubSlug is not null) github?.TryGetValue(s.GitHubSlug, out info);
                return new
                {
                    repo = s.Repo.Name, github = s.GitHubSlug, archived = info?.Archived, workInProgress = info?.HasTopic(GitHubRepoIndex.WorkInProgressTopic),
                    uncommitted = s.Uncommitted, unpushed = s.Unpushed, stashes = s.Stashes, localOnlyIgnored = s.LocalOnlyIgnored, error = s.Error,
                };
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("Repo", "GitHub", "Uncommitted", "Unpushed", "Stash", "Local-only ignored");
        foreach (var s in statuses)
            table.AddRow(Output.Esc(s.Repo.Name), GitHubCell(s, github),
                Count(s.Uncommitted), Count(s.Unpushed), Count(s.Stashes),
                s.LocalOnlyIgnored.Count == 0 ? "[grey]—[/]" : $"[yellow]{Output.Esc(string.Join(", ", s.LocalOnlyIgnored.Take(2)))}{(s.LocalOnlyIgnored.Count > 2 ? $" +{s.LocalOnlyIgnored.Count - 2}" : "")}[/]");
        AnsiConsole.Write(table);
        return 0;
    }

    private static string Count(int n) => n == 0 ? "[grey]0[/]" : $"[yellow]{n}[/]";

    private static string GitHubCell(RepoStatus s, IReadOnlyDictionary<string, GitHubRepoInfo>? github)
    {
        if (s.Error is not null) return $"[red]{Output.Esc(s.Error)}[/]";
        if (s.GitHubSlug is null) return "[red]no GitHub remote[/]";
        if (github is null) return "[grey]?[/]";
        if (!github.TryGetValue(s.GitHubSlug, out var info)) return "[red]not found[/]";
        var renamed = info.Slug != s.GitHubSlug ? $" [grey](origin is stale: now {Output.Esc(info.Slug)})[/]" : "";
        if (info.Archived) return "[grey]archived[/]" + renamed;
        return (info.HasTopic(GitHubRepoIndex.WorkInProgressTopic) ? "[green]wip[/]" : "[yellow]untagged[/]") + renamed;
    }
}

public sealed class ReposPruneCommand : Command<ReposPruneCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--apply")]
        [Description("Actually delete the safe folders (asks for a typed 'yes' first).")]
        public bool Apply { get; init; }

        [CommandOption("--only <NAMES>")]
        [Description("Comma-separated repo names: limit deletion to these.")]
        public string? Only { get; init; }

        [CommandOption("--override <NAMES>")]
        [Description("Comma-separated blocked repos to delete anyway, giving up their local-only work. Never applies to a repo with no GitHub copy or a kept dependent.")]
        public string? Override { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var repos = settings.Workspace.Repos();
        AnsiConsole.MarkupLine("[grey]Reading git state and scanning for cross-repo folder references…[/]");
        var statuses = repos.Select(RepoStatus.Read).ToList();
        var github = Output.TryGitHub(required: true, statuses.Select(s => s.GitHubSlug).OfType<string>())!;
        var dependencies = DependencyScan.Scan(repos);
        var plan = PrunePlanner.Plan(statuses, github, dependencies);

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repo", "Why it's a candidate", "Verdict");
        foreach (var c in plan)
            table.AddRow(Output.Esc(c.Status.Repo.Name), Output.Esc(c.Reason),
                c.Safe ? "[green]safe to delete[/]" : $"[red]blocked:[/] {Output.Esc(string.Join("; ", c.Blockers))}");
        AnsiConsole.Write(table);

        var only = Names(settings.Only);
        var overrides = Names(settings.Override) ?? [];
        foreach (var name in overrides)
        {
            var c = plan.FirstOrDefault(x => x.Status.Repo.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (c is null) throw new InvalidOperationException($"--override {name}: not a prune candidate.");
            if (!c.Safe && !c.Overridable)
                throw new InvalidOperationException($"--override {name}: can't override '{string.Join("; ", c.Blockers.Where(b => b.Kind != BlockerKind.LocalWork))}'.");
        }

        var toDelete = plan.Where(c => (c.Safe || overrides.Contains(c.Status.Repo.Name)) && (only is null || only.Contains(c.Status.Repo.Name))).ToList();

        // The plan counts every safe candidate as going, so a candidate reaching into another doesn't block it. --only can
        // leave some of them on disk, and a folder one of those reaches into by path then has to stay too (to a fixed
        // point, for chains), exactly as for a kept repo.
        var going = toDelete.Select(c => c.Status.Repo.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool dropped;
        do
        {
            dropped = false;
            foreach (var c in toDelete.ToList())
            {
                if (dependencies.FirstOrDefault(d => d.ToRepo.Equals(c.Status.Repo.Name, StringComparison.OrdinalIgnoreCase) && !going.Contains(d.FromRepo)) is not { } d) continue;
                toDelete.Remove(c);
                going.Remove(c.Status.Repo.Name);
                dropped = true;
                AnsiConsole.MarkupLine($"[yellow]keeping {Output.Esc(c.Status.Repo.Name)}:[/] {Output.Esc(d.FromRepo)} depends on it ({Output.Esc(d.File)} → {Output.Esc(d.RawPath)}) and isn't being deleted.");
            }
        } while (dropped);
        AnsiConsole.MarkupLine($"{plan.Count} candidate(s): {plan.Count(c => c.Safe)} safe, {plan.Count(c => !c.Safe)} blocked{(overrides.Count > 0 ? $", {overrides.Count} overridden" : "")}.");

        if (!settings.Apply)
        {
            AnsiConsole.MarkupLine("[grey]Preview only. Re-run with --apply to delete the safe folders.[/]");
            return 0;
        }
        if (toDelete.Count == 0) { AnsiConsole.MarkupLine("Nothing to delete."); return 0; }

        AnsiConsole.MarkupLine("[bold]About to delete these folders (each is on GitHub):[/]");
        foreach (var c in toDelete)
            AnsiConsole.MarkupLine(c.Safe
                ? $"  {Output.Esc(c.Status.Repo.Path)}"
                : $"  {Output.Esc(c.Status.Repo.Path)} [red](overridden — loses: {Output.Esc(string.Join("; ", c.Blockers))})[/]");
        AnsiConsole.Markup("Type [bold]yes[/] to delete: ");
        if (Console.ReadLine()?.Trim() != "yes") { AnsiConsole.MarkupLine("Cancelled; nothing deleted."); return 1; }

        var failed = 0;
        foreach (var c in toDelete)
        {
            try
            {
                PrunePlanner.DeleteFolder(c.Status.Repo.Path);
                AnsiConsole.MarkupLine($"[green]deleted[/] {Output.Esc(c.Status.Repo.Name)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                AnsiConsole.MarkupLine($"[red]failed[/] {Output.Esc(c.Status.Repo.Name)}: {Output.Esc(ex.Message)}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    private static HashSet<string>? Names(string? csv) =>
        csv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReposRolloutTicketsCommand : Command<ReposRolloutTicketsCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "[WHAT]")]
        [Description("What to roll out. Only 'tickets' exists today.")]
        public string What { get; init; } = "tickets";

        [CommandOption("--apply")]
        [Description("Write the files (default is a preview).")]
        public bool Apply { get; init; }

        public override ValidationResult Validate() =>
            What == "tickets" ? ValidationResult.Success() : ValidationResult.Error("Only 'rollout tickets' exists.");
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var repos = settings.Workspace.Repos();
        using (var db = Data.TicketsDb.OpenIfMigrated(settings.Workspace.Root))
            if (db is not null) return InDatabase(db, settings, repos);
        var taken = repos.Select(WorkspaceTickets.CodeOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var today = TicketRules.Today();
        var changes = 0;

        foreach (var r in repos)
        {
            string action;
            if (!File.Exists(r.StoriesPath))
            {
                var code = WorkspaceTickets.DeriveCode(r.Name, taken);
                taken.Add(code);
                action = $"create docs/USER_STORIES.md (code {code})";
                if (settings.Apply)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(r.StoriesPath)!);
                    File.WriteAllText(r.StoriesPath, StoryFile.NewFileText(r.Name, code, today));
                    Seed(r);
                }
            }
            else
            {
                var file = StoryFile.Load(r.StoriesPath);
                if (file.HasTicketsSection) continue;
                if (file.Code is null) { AnsiConsole.MarkupLine($"[yellow]{Output.Esc(r.Name)}:[/] has USER_STORIES.md but no 'code:' front-matter; skipped."); continue; }
                action = "add a '## Tickets' section";
                if (settings.Apply) Seed(r);
            }
            changes++;
            AnsiConsole.MarkupLine($"{(settings.Apply ? "[green]done[/]" : "[grey]would[/]")} {Output.Esc(r.Name)}: {Output.Esc(action)}");
        }

        AnsiConsole.MarkupLine(changes == 0 ? "Every repo already has a ticket log."
            : settings.Apply ? $"{changes} repo(s) updated." : $"{changes} repo(s) would change. Re-run with --apply to write.");
        return 0;
    }

    /// <summary>Once migrated: a repo not in the database gets added to it (a code no other repo has) and its view written.</summary>
    private static int InDatabase(Data.TicketsDb db, Settings settings, IReadOnlyList<Repo> repos)
    {
        var known = db.Repos().ToDictionary(r => r.Name, r => r.Code, StringComparer.OrdinalIgnoreCase);
        var taken = known.Values.ToHashSet(StringComparer.Ordinal);
        var changes = 0;
        foreach (var r in repos.Where(r => !known.ContainsKey(r.Name)))
        {
            // A repo that arrives with its own USER_STORIES.md is imported, never replaced: rendering a blank entry's view
            // over that file would lose its stories and tickets.
            var bytes = File.Exists(r.StoriesPath) ? File.ReadAllBytes(r.StoriesPath) : null;
            if (bytes is not null && !Data.TicketsDb.IsPointer(bytes))
            {
                if (Stories.StoryDocument.FromBytes(bytes).Code is not { } own)
                {
                    AnsiConsole.MarkupLine($"[yellow]{Output.Esc(r.Name)}:[/] has USER_STORIES.md but no 'code:' front-matter; skipped.");
                    continue;
                }
                if (taken.Contains(own))
                {
                    AnsiConsole.MarkupLine($"[yellow]{Output.Esc(r.Name)}:[/] its code '{Output.Esc(own)}' is already another repo's; skipped.");
                    continue;
                }
                taken.Add(own);
                changes++;
                AnsiConsole.MarkupLine($"{(settings.Apply ? "[green]done[/]" : "[grey]would[/]")} {Output.Esc(r.Name)}: import its USER_STORIES.md into the ticket database (code {Output.Esc(own)})");
                if (!settings.Apply) continue;
                if (!StoryFile.Load(r.StoriesPath).HasTicketsSection) Seed(r);
                if (db.Import([r]).Single().RoundTrips) db.Render(settings.Workspace, [r.Name]);
                else AnsiConsole.MarkupLine($"[red]{Output.Esc(r.Name)}:[/] imported, but it doesn't rebuild byte-for-byte, so its file was left as it is.");
                continue;
            }

            var code = WorkspaceTickets.DeriveCode(r.Name, taken);
            taken.Add(code);
            changes++;
            AnsiConsole.MarkupLine($"{(settings.Apply ? "[green]done[/]" : "[grey]would[/]")} {Output.Esc(r.Name)}: add to the ticket database (code {code}) with a USER_STORIES.md view");
            if (!settings.Apply) continue;
            db.AddRepo(r, code);
            db.Add(r.Name, "docs", TicketRules.Closed, "Start the permanent ticket log in the ticket database", "");
            db.Render(settings.Workspace, [r.Name]);
        }
        AnsiConsole.MarkupLine(changes == 0 ? "Every repo is already in the ticket database."
            : settings.Apply ? $"{changes} repo(s) added." : $"{changes} repo(s) would be added. Re-run with --apply to write.");
        return 0;
    }

    private static void Seed(Repo r)
    {
        var file = StoryFile.Load(r.StoriesPath);
        file.Add("docs", TicketRules.Closed, "Start the permanent ticket log in USER_STORIES.md", "");
        file.Save();
    }
}

public sealed class ReposRenameCommand : Command<ReposRenameCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<OLD>")]
        [Description("The repo's current folder name, as the ticket database keys it.")]
        public string Old { get; init; } = "";

        [CommandArgument(1, "<NEW>")]
        [Description("Its new folder name. Doesn't rename anything on disk — rename the folder yourself (first or after).")]
        public string New { get; init; } = "";

        [CommandOption("--reason <TEXT>")]
        [Description("Why; kept forever in the repo's rename history and in every one of its tickets' history.")]
        public string Reason { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var result = db.RenameRepo(settings.Old, settings.New, settings.Reason);
        Db.Rerender(db, settings);
        Console.WriteLine($"{settings.Old} -> {result.NewName} ({result.TicketsMoved} ticket(s)); backed up to {result.BackupPath}");
        return 0;
    }
}

public sealed class ReposRecodeCommand : Command<ReposRecodeCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<REPO>")]
        public string Repo { get; init; } = "";

        [CommandArgument(1, "<CODE>")]
        [Description("The new project code (like M.LNCH or PRS); never one another repo has or had.")]
        public string Code { get; init; } = "";

        [CommandOption("--reason <TEXT>")]
        [Description("Why; kept forever in the ID history of the code and of every ticket, story and epic it moves.")]
        public string Reason { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var r = db.RecodeRepo(settings.Repo, settings.Code, settings.Reason);
        Db.Rerender(db, settings);
        Console.WriteLine($"{r.Repo}: {r.OldCode} -> {r.NewCode} ({r.Tickets} ticket(s), {r.Stories} story ID(s), {r.Epics} epic(s); former IDs still resolve); backed up to {r.BackupPath}");
        return 0;
    }
}

public sealed class ReposRenamesCommand : Command<ReposRenamesCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Only renames touching this name (as old or new, anywhere in its chain).")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var renames = db.RepoRenames(settings.Repo);
        if (renames.Count == 0) { AnsiConsole.MarkupLine("[grey]No renames on record.[/]"); return 0; }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Old name", "New name", "When", "Reason");
        foreach (var r in renames)
            table.AddRow(Output.Esc(r.OldName), Output.Esc(r.NewName), Output.Esc(r.At), Output.Esc(r.Reason));
        AnsiConsole.Write(table);
        return 0;
    }
}
