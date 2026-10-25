using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using MindAttic.Tickets.Repos;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

// The ticket functions taken over from the Orchestrator (M.TIX-T134): it only runs the work now, and reads these from the
// ticket database; Tickets manages the tickets.

/// <summary><c>tickets ticket bump &lt;ID&gt; [--by N]</c>: raise (or lower) a ticket's priority.</summary>
public sealed class TicketBumpCommand : Command<TicketBumpCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--by <N>")]
        [Description("How far to move it; negative lowers it. Default 1.")]
        [DefaultValue(1L)]
        public long By { get; init; } = 1;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var next = db.Bump(settings.Id, settings.By);
        Console.WriteLine($"{db.Ticket(settings.Id)!.Value.Ticket.Id} priority {next.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }
}

/// <summary><c>tickets ticket assign &lt;ID&gt; [&lt;EMAIL&gt;]</c>: assign a ticket to a member's login, take it off them, or redelegate it.</summary>
public sealed class TicketAssignCommand : Command<TicketAssignCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandArgument(1, "[EMAIL]")]
        [Description("The member's login email. Without it, says who has the ticket.")]
        public string? Email { get; init; }

        [CommandOption("--unassign")]
        [Description("Take the ticket off whoever it's assigned to.")]
        public bool Unassign { get; init; }

        [CommandOption("--redelegate")]
        [Description("Redelegate now: drop the assignment so it's handed on like an unassigned ticket (another member with room, else the API key).")]
        public bool Redelegate { get; init; }

        public override ValidationResult Validate() =>
            Unassign && Redelegate ? ValidationResult.Error("--unassign and --redelegate are the same change; give one")
            : (Unassign || Redelegate) && Email is not null ? ValidationResult.Error("--unassign/--redelegate take no email")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Unassign || settings.Redelegate)
        {
            Console.WriteLine((settings.Redelegate ? db.Redelegate(settings.Id) : db.Unassign(settings.Id)).Text);
            return 0;
        }
        if (settings.Email is not { Length: > 0 } email)
        {
            var (_, t) = db.Ticket(settings.Id) ?? throw new KeyNotFoundException($"No ticket {settings.Id}.");
            Console.WriteLine(db.Work(t.Id).Assignee is { } who ? $"{t.Id} is assigned to {who}: it's dispatched only on that login." : $"{t.Id} isn't assigned to anyone.");
            return 0;
        }
        Console.WriteLine(db.Assign(settings.Id, email).Text);
        return 0;
    }
}

/// <summary><c>tickets ticket block &lt;ID&gt; --by X [--by Y] [--clear]</c>: make a ticket wait for others.</summary>
public sealed class TicketBlockCommand : Command<TicketBlockCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--by <ID>")]
        [Description("The ticket it waits for (repeatable).")]
        public string[] By { get; init; } = [];

        [CommandOption("--clear")]
        [Description("Drop what it waits for first; with --by, replaces them.")]
        public bool Clear { get; init; }

        public override ValidationResult Validate() =>
            By.Length == 0 && !Clear ? ValidationResult.Error("give --by <ID> (repeatable) and/or --clear") : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        var now = db.Block(settings.Id, settings.By, settings.Clear);
        var id = db.Ticket(settings.Id)!.Value.Ticket.Id;
        Console.WriteLine(now.Count > 0 ? $"{id} requires {string.Join(", ", now)}." : $"{id} requires nothing.");
        return 0;
    }
}

/// <summary><c>tickets ticket reserve &lt;ID&gt; &lt;TOKENS&gt;</c>: set aside tokens for a ticket (0 releases).</summary>
public sealed class TicketReserveCommand : Command<TicketReserveCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandArgument(1, "<TOKENS>")]
        [Description("Uncached tokens to set aside, like 2M or 500k; 0 releases the reservation.")]
        public string Tokens { get; init; } = "";

        public override ValidationResult Validate() =>
            TokenText.TryParse(Tokens, out _) ? ValidationResult.Success() : ValidationResult.Error("<TOKENS>: a number like 2M, 500k or 0");
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        TokenText.TryParse(settings.Tokens, out var tokens);
        using var db = Db.Open(settings);
        db.Reserve(settings.Id, tokens);
        var id = db.Ticket(settings.Id)?.Ticket.Id ?? settings.Id.Trim();
        Console.WriteLine(tokens > 0
            ? $"{id} has {TokenText.Format(tokens)} reserved: it's proposed first, and everything else paces against what's left after it."
            : $"{id}'s reservation is released.");
        return 0;
    }
}

/// <summary><c>tickets ticket working &lt;ID&gt; --login X [--session Y] | --clear</c>: the session working a ticket now, as the
/// Orchestrator records it while it runs the work; Tickets Desktop links back to it.</summary>
public sealed class TicketWorkingCommand : Command<TicketWorkingCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandOption("--login <EMAIL>")]
        [Description("The login it runs on, by its account email.")]
        public string? Login { get; init; }

        [CommandOption("--session <ID>")]
        [Description("Its session id.")]
        public string? Session { get; init; }

        [CommandOption("--clear")]
        [Description("Nothing is working it any more.")]
        public bool Clear { get; init; }

        public override ValidationResult Validate() =>
            Clear && (Login is not null || Session is not null) ? ValidationResult.Error("--clear takes no --login or --session")
            : !Clear && Login is null && Session is null ? ValidationResult.Error("give --login and/or --session, or --clear")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Clear) db.ClearWorking(settings.Id);
        else db.SetWorking(settings.Id, settings.Login, settings.Session);
        var id = db.Ticket(settings.Id)!.Value.Ticket.Id;
        var w = db.Work(id);
        Console.WriteLine(w.IsWorking ? $"{id} is being worked on {w.Login ?? "an unknown login"}{(w.Session is { } s ? $" ({s})" : "")}." : $"{id} isn't being worked.");
        return 0;
    }
}

/// <summary><c>tickets ticket note &lt;ID&gt; &lt;MARKDOWN&gt; [--author X]</c> (M.TIX-T72): appends a comment to a ticket; notes are
/// never changed or deleted, and <c>tickets ticket show</c> and Tickets Desktop list them.</summary>
public sealed class TicketNoteCommand : Command<TicketNoteCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; init; } = "";

        [CommandArgument(1, "<MARKDOWN>")]
        [Description("The note, as markdown. Pass - to read it from standard input.")]
        public string Markdown { get; init; } = "";

        [CommandOption("--author <NAME>")]
        [Description("Who wrote it. Default: $MINDATTIC_TICKETS_AUTHOR, else this machine's user name.")]
        public string? Author { get; init; }
    }

    /// <summary>The author a note gets when none is given.</summary>
    public static string DefaultAuthor() =>
        Environment.GetEnvironmentVariable("MINDATTIC_TICKETS_AUTHOR") is { Length: > 0 } a ? a.Trim() : Environment.UserName;

    public override int Execute(CommandContext context, Settings settings)
    {
        var markdown = settings.Markdown == "-" ? Console.In.ReadToEnd() : settings.Markdown;
        using var db = Db.Open(settings);
        var note = db.AddNote(settings.Id, settings.Author ?? DefaultAuthor(), markdown);
        Console.WriteLine($"{note.TicketId} note {note.Seq.ToString(CultureInfo.InvariantCulture)} by {note.Author} at {note.At}");
        return 0;
    }
}

/// <summary><c>tickets ticket hygiene</c>: closed tickets that may not really be done.</summary>
public sealed class TicketHygieneCommand : Command<TicketHygieneCommand.Settings>
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
        // Each repo's tickets from the database when it holds the repo, else from its own USER_STORIES.md.
        using var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root);
        var inDb = db?.Repos().Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        bool Wanted(string repo) => settings.Repo is null || repo.Equals(settings.Repo, StringComparison.OrdinalIgnoreCase);
        var fromDb = db is null ? [] : TicketHygiene.Check(db.Tickets().Where(t => Wanted(t.Repo)));
        var fromFiles = settings.Workspace.Repos()
            .Where(r => !inDb.Contains(r.Name) && Wanted(r.Name) && File.Exists(r.StoriesPath))
            .SelectMany(r =>
            {
                try { return TicketHygiene.Check(r.Name, [.. WorkspaceTickets.All([r]).Select(x => x.Ticket)]); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { return []; }
            });
        var findings = fromDb.Concat(fromFiles).ToList();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(findings.Select(f => new { repo = f.Repo, id = f.TicketId, summary = f.Summary, why = f.Why }),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (findings.Count == 0) { AnsiConsole.MarkupLine("[green]Every closed fix and feature cites its proof, and none came back.[/]"); return 0; }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repo", "Ticket", "Summary", "Why");
        foreach (var f in findings)
            table.AddRow(Output.Esc(f.Repo), Output.Esc(f.TicketId), Output.Esc(f.Summary.Length > 60 ? f.Summary[..59] + "…" : f.Summary), Output.Esc(f.Why));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{findings.Count} closed ticket(s) to double-check.[/]");
        return 0;
    }
}

/// <summary><c>tickets ticket intake [--dry-run]</c>: file GitHub issues labelled orchestrator and CI failures on main as tickets, once.</summary>
public sealed class TicketIntakeCommand : Command<TicketIntakeCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Only this repo. Default: every repo with a GitHub origin.")]
        public string? Repo { get; init; }

        [CommandOption("--dry-run")]
        [Description("Show what would be filed without filing it.")]
        public bool DryRun { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = TicketsDb.OpenIfMigrated(settings.Workspace.Root);
        var filed = GitHubIntake.Intake(settings.Workspace, db, settings.DryRun, only: settings.Repo).ToList();
        foreach (var (repo, t) in filed)
            AnsiConsole.MarkupLine($"{(settings.DryRun ? "would file" : "filed")} [bold]{Output.Esc(t.Id)}[/] in {Output.Esc(repo)} ({Output.Esc(t.Type)}): {Output.Esc(t.Summary)}");
        if (filed.Count > 0 && !settings.DryRun && db is not null) Db.Rerender(db, settings);
        AnsiConsole.MarkupLine(filed.Count == 0 ? "[grey]Nothing new on GitHub (issues labelled 'orchestrator', CI failures on main in the last day).[/]"
            : $"[grey]{filed.Count} ticket(s){(settings.DryRun ? " would be filed; nothing was written" : " filed")}.[/]");
        return 0;
    }
}

/// <summary>Token counts as typed (20000000, 20M, 1.5B, 500k) and shown.</summary>
public static class TokenText
{
    public static bool TryParse(string text, out long value)
    {
        value = 0;
        var t = text.Trim().Replace(",", "").Replace("_", "");
        if (t.Length == 0) return false;
        var scale = char.ToUpperInvariant(t[^1]) switch { 'K' => 1e3, 'M' => 1e6, 'B' => 1e9, _ => 1 };
        if (scale != 1) t = t[..^1];
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0) return false;
        // Under one token would round to 0, which releases a reservation; only a plain 0 means that. Past a quadrillion is a typo.
        if (n * scale is > 0 and < 1 || n * scale > TicketsDb.MaxReserved) return false;
        value = (long)(n * scale);
        return true;
    }

    public static string Format(long tokens) =>
        tokens >= 1_000_000_000 ? (tokens / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B"
        : tokens >= 1_000_000 ? (tokens / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : tokens >= 1_000 ? (tokens / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(CultureInfo.InvariantCulture);
}
