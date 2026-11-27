using System.ComponentModel;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

public sealed class DbReconcileCommand : Command<DbReconcileCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--dry-run")]
        [Description("Report only: copy nothing, change no index or statistic. The copy is still checked against the server.")]
        public bool DryRun { get; init; }

        [CommandOption("--rebuild-above <PERCENT>")]
        [Description("Rebuild an index fragmented more than this. Default: 30.")]
        [DefaultValue(30.0)]
        public double RebuildAbove { get; init; } = 30;

        [CommandOption("--reorganize-above <PERCENT>")]
        [Description("Reorganize an index fragmented more than this (and up to the rebuild threshold). Default: 5.")]
        [DefaultValue(5.0)]
        public double ReorganizeAbove { get; init; } = 5;

        [CommandOption("--min-pages <N>")]
        [Description("Leave an index of this many pages or fewer alone. Default: 100.")]
        [DefaultValue(100L)]
        public long MinPages { get; init; } = 100;
    }

    /// <summary>Rebuilds the SQLite copy whole and verifies it by row counts and per-ticket checksums, then tidies the server's indexes and statistics.</summary>
    public override int Execute(CommandContext context, Settings settings)
    {
        var failed = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string Lap() { var t = clock.Elapsed; clock.Restart(); return $"{t.TotalSeconds:0.0}s"; }

        using var db = Db.Open(settings);
        if (!db.OnServer) throw new InvalidOperationException("The ticket database is the SQLite file on this machine; there is no server to reconcile it with.");
        AnsiConsole.MarkupLine($"Reconciling {Output.Esc(db.Store)} with {Output.Esc(db.Path)}{(settings.DryRun ? " [grey](dry run)[/]" : "")}");

        if (!settings.DryRun)
        {
            db.CopyAll();
            if (db.LastCopyError is { } err) { AnsiConsole.MarkupLine($"[red]The copy reported: {Output.Esc(err)}[/]"); failed = true; }
        }
        var counts = db.Counts();
        var off = counts.Where(c => c.Store != c.File).ToList();
        AnsiConsole.MarkupLine($"1. Copy{(settings.DryRun ? " (not rebuilt)" : "")}: {counts.Count} tables, {counts.Sum(c => c.Store)} rows on the server, {counts.Sum(c => c.File)} in the copy, {off.Count} tables differ ({Lap()})");
        foreach (var c in off)
        {
            AnsiConsole.MarkupLine($"   {(settings.DryRun ? "[yellow]" : "[red]")}{Output.Esc(c.Table)}: {c.Store} on the server, {c.File} in the copy[/]");
            if (!settings.DryRun) failed = true;
        }

        clock.Restart();
        CopyMismatches mismatches;
        using (var copy = TicketsDb.OpenFile(db.Path)) mismatches = db.CompareTo(copy);
        var first = string.Join(", ", mismatches.Ids.Take(10));
        AnsiConsole.MarkupLine($"2. Verify: {mismatches.Compared} tickets checked, {mismatches.Ids.Count} mismatches{(first.Length > 0 ? ": " + Output.Esc(first) : "")} ({Lap()})");
        if (mismatches.Ids.Count > 0 && !settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[red]CHECKSUM MISMATCH after a whole copy: {mismatches.Ids.Count} tickets differ between the server and data/Tickets.db.[/]");
            failed = true;
        }

        clock.Restart();
        var work = db.MaintainIndexes(settings.RebuildAbove, settings.ReorganizeAbove, settings.MinPages, settings.DryRun, out var stats);
        var dry = settings.DryRun;
        AnsiConsole.MarkupLine($"3. Indexes: {work.Count(w => w.Action == "REBUILD")} {(dry ? "to rebuild" : "rebuilt")}, {work.Count(w => w.Action == "REORGANIZE")} {(dry ? "to reorganize" : "reorganized")}, statistics {(dry ? "of" : "updated on")} {stats} tables ({Lap()})");
        foreach (var w in work) AnsiConsole.MarkupLine($"   {w.Action} {Output.Esc(w.Table)}.{Output.Esc(w.Index)} ({w.Fragmentation:0.#}% of {w.Pages} pages)");

        AnsiConsole.MarkupLine(failed ? "[red]Reconcile FAILED.[/]" : "[green]Reconcile done.[/]");
        return failed ? 1 : 0;
    }
}
