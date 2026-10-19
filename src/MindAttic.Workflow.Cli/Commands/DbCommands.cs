using System.ComponentModel;
using System.Text;
using System.Text.Json;
using MindAttic.Workflow.Data;
using MindAttic.Workflow.Tickets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Workflow.Cli.Commands;

internal static class Db
{
    public static WorkflowDb Open(WorkspaceSettings s) =>
        WorkflowDb.OpenIfMigrated(s.Workspace.Root) ?? throw new InvalidOperationException("The workspace isn't migrated to the workflow database yet; run 'workflow db migrate'.");

    /// <summary>Re-renders the generated USER_STORIES.md views a change touched (only files that differ are written).</summary>
    public static void Rerender(WorkflowDb db, WorkspaceSettings s) => db.Render(s.Workspace);

    /// <summary>Ticket → tickets it waits for, from the orchestrator's 'block X --by Y' settings, if it has any.</summary>
    public static Dictionary<string, IReadOnlyList<string>> OrchestratorBlocks()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MindAttic", "MindAttic.Orchestrator", "settings.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("BlockedBy", out var b) || b.ValueKind != JsonValueKind.Object) return [];
            return b.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlyList<string>)[.. p.Value.EnumerateArray().Select(x => x.GetString()!)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}

public sealed class DbMigrateCommand : Command<DbMigrateCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--cutover")]
        [Description("After every file is verified to rebuild byte-for-byte, turn each repo's USER_STORIES.md into a generated view of the database.")]
        public bool Cutover { get; init; }

        [CommandOption("--db <PATH>")]
        [Description("Database file. Default: <root>/MindAttic.Workflow/data/workflow.db")]
        public string? DbPath { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var ws = settings.Workspace;
        var path = settings.DbPath ?? WorkflowDb.DefaultPath(ws.Root);
        using var db = WorkflowDb.Open(path);
        var report = db.Import(ws.Repos(), Db.OrchestratorBlocks());

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repo", "Code", "Epics", "Stories", "Backlog", "Tickets", "Links", "Rebuilds exactly");
        foreach (var r in report)
            table.AddRow(Output.Esc(r.Repo), r.Code, $"{r.Epics}", $"{r.Stories}", $"{r.BacklogItems}", $"{r.Tickets}", $"{r.Links}", r.RoundTrips ? "[green]yes[/]" : "[red]NO[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"{report.Count} repos · {report.Sum(r => r.Epics)} epics · {report.Sum(r => r.Stories)} stories · {report.Sum(r => r.BacklogItems)} backlog items · " +
            $"{report.Sum(r => r.Tickets)} tickets · {db.AllLinks().Count} links → {Output.Esc(db.Path)}");

        var bad = report.Where(r => !r.RoundTrips).ToList();
        if (bad.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{bad.Count} file(s) don't rebuild exactly; nothing was cut over.[/]");
            return 1;
        }
        if (!settings.Cutover)
        {
            AnsiConsole.MarkupLine("[grey]Every file rebuilds byte-for-byte. Nothing in the repos was changed; --cutover turns their USER_STORIES.md into generated views.[/]");
            return 0;
        }

        var written = db.Render(ws, report.Select(r => r.Repo));
        AnsiConsole.MarkupLine($"[green]Cut over {report.Count} repos:[/] each USER_STORIES.md is now a generated view of the database ({written.Count} rewritten). Commit them, then 'workflow db sync'.");
        return 0;
    }
}

public sealed class DbExportCommand : Command<DbExportCommand.Settings>
{
    public sealed class Settings : RepoSettings
    {
        [CommandOption("--out <FILE>")]
        [Description("Write here instead of printing.")]
        public string? Out { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var repo = settings.ResolveRepo();
        var bytes = db.Export(repo.Name) ?? throw new InvalidOperationException($"{repo.Name} isn't in the workflow database.");
        if (settings.Out is { } o) File.WriteAllBytes(o, bytes);
        else Console.Out.Write(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        return 0;
    }
}

public sealed class DbCheckCommand : Command<WorkspaceSettings>
{
    public override int Execute(CommandContext context, WorkspaceSettings settings)
    {
        using var db = Db.Open(settings);
        var problems = db.Check().Concat(db.StaleViews(settings.Workspace).Select(r => $"{r}: docs/USER_STORIES.md isn't its current view (hand-edited or not rendered); run 'workflow db render'")).ToList();
        foreach (var p in problems) AnsiConsole.MarkupLine($"[yellow]{Output.Esc(p)}[/]");
        AnsiConsole.MarkupLine(problems.Count == 0 ? "[green]No problems.[/]" : $"[grey]{problems.Count} problem(s)[/]");
        return problems.Count == 0 ? 0 : 1;
    }
}

public sealed class DbRenderCommand : Command<DbRenderCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var written = db.Render(settings.Workspace, settings.Repo is null ? null : [settings.Repo]);
        AnsiConsole.MarkupLine(written.Count == 0 ? "[grey]Every view is current.[/]" : $"[green]Rewrote[/] {Output.Esc(string.Join(", ", written))}");
        return 0;
    }
}

public sealed class DbSyncCommand : Command<DbSyncCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--no-push")]
        public bool NoPush { get; init; }

        [CommandOption("--ticket <ID>")]
        [Description("The ticket this sync commits for. Default: the tickets whose rows or history changed since the committed dump.")]
        public string? Ticket { get; init; }
    }

    /// <summary>
    /// Writes the readable dump and commits both files in the MindAttic.Workflow repo when anything changed.
    /// Every other repo's agents share this one database file, so it's never staged speculatively: whether
    /// there's anything to commit is decided from the dump text alone, before <c>git add</c> touches the
    /// index, and anything that stops the commit from landing (the hook rejects it, git errors, an
    /// exception) always resets the index back to clean. Otherwise a failed or partial sync would leave
    /// data/workflow.db and data/workflow.sql staged — including whatever other tickets changed them since
    /// the last sync — for the next, unrelated commit in this repo to sweep in.
    /// </summary>
    public override int Execute(CommandContext context, Settings settings)
    {
        var self = settings.Workspace.Find("MindAttic.Workflow") ?? throw new InvalidOperationException("No MindAttic.Workflow repo in the workspace.");
        string dump;
        using (var db = Db.Open(settings))
        {
            // Changes made outside the CLI (the orchestrator's GitHub intake) reach the views here; each repo commits its own.
            if (db.Render(settings.Workspace) is { Count: > 0 } views)
                AnsiConsole.MarkupLine($"[grey]Refreshed the USER_STORIES.md view in {Output.Esc(string.Join(", ", views))}.[/]");
            dump = db.Dump();
            if (settings.Ticket is { } t && db.Ticket(t) is null) throw new InvalidOperationException($"No ticket {t} in the workflow database.");
        }

        // Decide whether this sync has anything to commit before the database or its dump ever touch the
        // index: comparing the two dump texts needs no git state at all.
        var committed = Git.Run(self.Path, "show", "HEAD:" + WorkflowDb.DumpRelativePath);
        var before = committed.Ok ? committed.Output : null;
        if (before == dump)
        {
            AnsiConsole.MarkupLine("[grey]No changes to the workflow database since the last sync.[/]");
            return 0;
        }

        var files = new List<string> { WorkflowDb.RelativePath, WorkflowDb.DumpRelativePath };
        var subject = SyncCommit.Subject(SyncCommit.ChangedTickets(before, dump), settings.Ticket);
        try
        {
            if (subject is null)
            {
                // Nothing names this sync (no --ticket, and no ticket row or history changed, e.g. a story/epic/backlog
                // edit alone): open a closed chore ticket here to carry it instead of leaving the database staged
                // indefinitely for some later ticketed sync.
                Ticket ticket;
                using (var db = Db.Open(settings))
                {
                    ticket = db.Add(self.Name, "chore", TicketRules.Closed, "Sync the workflow database for changes that touched no ticket (story, epic or backlog edits)", "");
                    if (db.Render(settings.Workspace, [self.Name]).Count > 0) files.Add(StoryFile.RelativePath);
                    dump = db.Dump();
                }
                subject = SyncCommit.Subject([], ticket.Id)!; // ticket.Id is given explicitly, so Subject never returns null here
            }
            File.WriteAllText(Path.Combine(self.Path, WorkflowDb.DumpRelativePath.Replace('/', Path.DirectorySeparatorChar)), dump, new UTF8Encoding(false));
            Git.Run(self.Path, ["add", "--", .. files]);
            if (Git.Run(self.Path, ["diff", "--cached", "--quiet", "--", .. files]).Ok)
            {
                AnsiConsole.MarkupLine("[grey]No changes to the workflow database since the last sync.[/]");
                return 0;
            }
            var commit = Git.Run(self.Path, ["commit", "-q", "-m", subject, "--", .. files]);
            if (!commit.Ok) throw new InvalidOperationException("git commit failed: " + (commit.Output + commit.Error).Trim());
        }
        catch
        {
            // Never leave these files staged for whatever commit happens next in this repo: only a sync
            // that actually commits may touch the index for them.
            Git.Run(self.Path, ["reset", "-q", "--", .. files]);
            throw;
        }
        AnsiConsole.MarkupLine($"[green]Committed the workflow database:[/] {Output.Esc(subject)}");
        if (settings.NoPush) return 0;
        var push = Git.Run(self.Path, "push", "-q");
        AnsiConsole.MarkupLine(push.Ok ? "[green]Pushed.[/]" : $"[yellow]Push failed (pull and sync again):[/] {Output.Esc((push.Output + push.Error).Trim())}");
        return push.Ok ? 0 : 1;
    }
}

public sealed class TicketShowCommand : Command<TicketShowCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var (repo, t) = db.Ticket(settings.Id) ?? throw new KeyNotFoundException($"No ticket {settings.Id}.");
        var links = db.Links(t.Id);
        var history = db.History(t.Id);
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                repo, id = t.Id, date = t.Date, type = t.Type, status = t.Status, summary = t.Summary, @ref = t.Ref,
                requires = db.Requires(t.Id), links = links.Select(l => new { from = l.From, kind = l.Kind, to = l.To, note = l.Note }),
                history = history.Select(e => new { at = e.At, field = e.Field, old = e.Old, @new = e.New }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        AnsiConsole.MarkupLine($"[bold]{Output.Esc(t.Id)}[/] · {Output.Esc(repo)} · {Output.Esc(t.Type)} · {Output.Esc(t.Status)} · opened {Output.Esc(t.Date)}");
        AnsiConsole.WriteLine(t.Summary);
        AnsiConsole.MarkupLine($"[grey]Ref:[/] {Output.Esc(t.Ref)}");
        foreach (var l in links)
            AnsiConsole.MarkupLine(l.From.Equals(t.Id, StringComparison.OrdinalIgnoreCase)
                ? $"  [cyan]{Output.Esc(l.Kind)}[/] → {Output.Esc(l.To)}"
                : $"  ← [cyan]{Output.Esc(l.Kind)}[/] {Output.Esc(l.From)}");
        foreach (var e in history)
            AnsiConsole.MarkupLine($"  [grey]{Output.Esc(e.At)} {Output.Esc(e.Field)}: {Output.Esc(e.Old ?? "")} → {Output.Esc(e.New ?? "")}[/]");
        return 0;
    }
}

public sealed class TicketCorrectCommand : Command<TicketCorrectCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--type <TYPE>")]
        [Description("feature | fix | chore | docs")]
        public string Type { get; init; } = "";

        [CommandOption("--reason <TEXT>")]
        [Description("Why; kept in the ticket's history.")]
        public string Reason { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var t = db.CorrectType(settings.Id, settings.Type, settings.Reason);
        Db.Rerender(db, settings);
        Console.WriteLine($"{t.Id} type {t.Type}");
        return 0;
    }
}

public sealed class TicketLinkCommand : Command<TicketLinkCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<FROM>")]
        public string From { get; init; } = "";

        [CommandArgument(1, "<KIND>")]
        [Description("requires (FROM waits for TO) | related | follow-up-of | supersedes | duplicates | implements | part-of")]
        public string Kind { get; init; } = "";

        [CommandArgument(2, "<TO>")]
        public string To { get; init; } = "";

        [CommandOption("--remove")]
        public bool Remove { get; init; }

        [CommandOption("--note <TEXT>")]
        public string? Note { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Remove)
        {
            var gone = db.RemoveLink(settings.From, settings.To, settings.Kind);
            Console.WriteLine(gone ? $"removed {settings.From} {settings.Kind} {settings.To}" : "no such link");
            return gone ? 0 : 1;
        }
        db.AddLink(settings.From, settings.To, settings.Kind, settings.Note);
        Db.Rerender(db, settings);
        Console.WriteLine($"{settings.From} {settings.Kind} {settings.To}");
        return 0;
    }
}

public sealed class StoryListCommand : Command<StoryListCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        public string? Repo { get; init; }

        [CommandOption("--status <STATUS>")]
        [Description("done | partial | planned")]
        public string? Status { get; init; }

        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var stories = db.Stories(settings.Repo).Where(s => settings.Status is null || s.Status == settings.Status).ToList();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(stories, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repo", "ID", "", "Epic", "Story");
        foreach (var s in stories)
            table.AddRow(Output.Esc(s.Repo), Output.Esc(s.Id), s.Glyph ?? "", Output.Esc(s.Epic ?? s.Section ?? ""),
                Output.Esc(s.Text.Length > 110 ? s.Text[..110] + "…" : s.Text));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{stories.Count} stor{(stories.Count == 1 ? "y" : "ies")}[/]");
        return 0;
    }
}

public sealed class StoryShowCommand : Command<StoryShowCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var found = db.Story(settings.Id);
        if (found.Count == 0) throw new KeyNotFoundException($"No story {settings.Id}.");
        foreach (var s in found)
        {
            AnsiConsole.MarkupLine($"[bold]{Output.Esc(s.Id)}[/] {s.Glyph} {Output.Esc(s.Suffix)} · {Output.Esc(s.Repo)} · {Output.Esc(s.Epic is null ? s.Section ?? "" : "Epic " + s.Epic)}");
            AnsiConsole.WriteLine(s.Text);
            if (s.Evidence is { } e) AnsiConsole.MarkupLine($"[grey]Evidence:[/] {Output.Esc(e)}");
        }
        foreach (var l in db.Links(settings.Id))
            AnsiConsole.MarkupLine(l.From.Equals(settings.Id, StringComparison.OrdinalIgnoreCase)
                ? $"  [cyan]{Output.Esc(l.Kind)}[/] → {Output.Esc(l.To)}" : $"  ← [cyan]{Output.Esc(l.Kind)}[/] {Output.Esc(l.From)}");
        return 0;
    }
}

public sealed class StoryStatusCommand : Command<StoryStatusCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandArgument(1, "<STATUS>")]
        [Description("done | partial | planned")]
        public string Status { get; init; } = "";

        [CommandOption("--repo <NAME>")]
        [Description("When more than one repo has a story with this ID.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var n = db.SetStoryStatus(settings.Id, settings.Status, settings.Repo);
        Db.Rerender(db, settings);
        Console.WriteLine($"{settings.Id} {settings.Status}{(n > 1 ? $" ({n} stories with that ID)" : "")}");
        return 0;
    }
}

public sealed class StoryNewCommand : Command<StoryNewCommand.Settings>
{
    public sealed class Settings : RepoSettings
    {
        [CommandOption("--epic <KEY>")]
        public string Epic { get; init; } = "";

        [CommandOption("--text <TEXT>")]
        [Description("The story, e.g. 'As a user, I can …, so …'.")]
        public string Text { get; init; } = "";

        [CommandOption("--status <STATUS>")]
        [Description("planned (default) | partial | done")]
        public string Status { get; init; } = "planned";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var id = db.AddStory(settings.ResolveRepo().Name, settings.Epic, settings.Text, settings.Status);
        Db.Rerender(db, settings);
        Console.WriteLine(id);
        return 0;
    }
}

public sealed class StoryEditCommand : Command<StoryEditCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--text <TEXT>")]
        public string Text { get; init; } = "";

        [CommandOption("--repo <NAME>")]
        [Description("When more than one repo has a story with this ID.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        db.EditStory(settings.Id, settings.Text, settings.Repo);
        Db.Rerender(db, settings);
        Console.WriteLine($"{settings.Id} updated");
        return 0;
    }
}

public sealed class EpicNewCommand : Command<EpicNewCommand.Settings>
{
    public sealed class Settings : RepoSettings
    {
        [CommandOption("--key <KEY>")]
        [Description("The epic's letter (or short key), e.g. F.")]
        public string Key { get; init; } = "";

        [CommandOption("--title <TEXT>")]
        public string Title { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        db.AddEpic(settings.ResolveRepo().Name, settings.Key, settings.Title);
        Db.Rerender(db, settings);
        Console.WriteLine($"Epic {settings.Key} added");
        return 0;
    }
}

public sealed class BacklogAddCommand : Command<BacklogAddCommand.Settings>
{
    public sealed class Settings : RepoSettings
    {
        [CommandOption("--text <TEXT>")]
        public string Text { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        db.AddBacklogItem(settings.ResolveRepo().Name, settings.Text);
        Db.Rerender(db, settings);
        Console.WriteLine("added to the priority backlog");
        return 0;
    }
}