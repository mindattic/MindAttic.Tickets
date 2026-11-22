using System.ComponentModel;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary>
/// <c>tickets ticket split &lt;ID&gt; --task "summary"... [--file PATH] [--sequential] [--name-prefix X]</c> (M.TIX-T363): splits a
/// ticket into level-task children part-of it, in its repo, of its type, approved like it; see <see cref="TicketsDb.Split"/>.
/// </summary>
public sealed class TicketSplitCommand : Command<TicketSplitCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("The ticket to split, e.g. M.TIX-T361.")]
        public string Id { get; init; } = "";

        [CommandOption("--task <SUMMARY>")]
        [Description("One task's summary (repeatable); \"name | summary\" also gives it a friendly name.")]
        public string[] Tasks { get; init; } = [];

        [CommandOption("--file <PATH>")]
        [Description("Read tasks from a file, one per non-empty line; a line may be \"name | summary\".")]
        public string? File { get; init; }

        [CommandOption("--sequential")]
        [Description("Each task requires the one before it.")]
        public bool Sequential { get; init; }

        [CommandOption("--name-prefix <TEXT>")]
        [Description("Prefix each task's friendly name with this; a task without a name is named \"<prefix> <n>\".")]
        public string? NamePrefix { get; init; }

        public override ValidationResult Validate() =>
            Tasks.Length == 0 && File is null ? ValidationResult.Error("Give --task (repeatable) or --file.")
            : File is not null && !System.IO.File.Exists(File) ? ValidationResult.Error($"No file {File}.")
            : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var lines = settings.Tasks.Concat(settings.File is { } path ? System.IO.File.ReadAllLines(path) : [])
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var prefix = string.IsNullOrWhiteSpace(settings.NamePrefix) ? null : settings.NamePrefix.Trim();
        var tasks = lines.Select((line, i) =>
        {
            var (name, summary) = Parse(line);
            if (prefix is not null) name = name is null ? $"{prefix} {i + 1}" : $"{prefix} {name}";
            return (name, summary);
        }).ToList();
        if (tasks.Count == 0) throw new InvalidOperationException("No tasks: every line was blank.");

        using var db = Db.Open(settings);
        foreach (var t in db.Split(settings.Id, tasks, settings.Sequential))
            Console.WriteLine($"{t.Id}  {t.Summary}");
        Db.Rerender(db, settings);
        return 0;
    }

    /// <summary>"name | summary" → both; otherwise no name and the whole line as the summary.</summary>
    private static (string? Name, string Summary) Parse(string line)
    {
        var bar = line.IndexOf('|');
        if (bar < 0) return (null, line.Trim());
        var name = line[..bar].Trim();
        return (name.Length == 0 ? null : name, line[(bar + 1)..].Trim());
    }
}
