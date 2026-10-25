using System.ComponentModel;
using System.Text.Json;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary>Saved filters (M.TIX-T78): named ticket queries kept in the ticket database, for <c>tickets ticket list --filter</c>
/// and Tickets Desktop's filter bar.</summary>
internal static class Filters
{
    public static TicketsDb Open(WorkspaceSettings settings) =>
        TicketsDb.OpenIfMigrated(settings.Workspace.Root)
            ?? throw new InvalidOperationException("Saved filters live in the ticket database, and this workspace isn't migrated yet.");
}

/// <summary><c>tickets filter list [--json]</c>: every saved filter and its query.</summary>
public sealed class FilterListCommand : Command<FilterListCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--json")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Filters.Open(settings);
        var filters = db.SavedFilters();
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(filters.Select(f => new { name = f.Name, query = f.Query, updated = f.Updated }),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (filters.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No saved filters; save one with 'tickets filter save <NAME> <QUERY>'.[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Query", "Updated");
        foreach (var f in filters) table.AddRow(Output.Esc(f.Name), Output.Esc(f.Query), Output.Esc(f.Updated));
        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary><c>tickets filter save &lt;NAME&gt; &lt;QUERY&gt;</c>: saves (or replaces) a named query, checked first.</summary>
public sealed class FilterSaveCommand : Command<FilterSaveCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("The filter's name.")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "<QUERY>")]
        [Description("The query, e.g. \"repo:M.TIX type:fix status:open\" (quote it as one argument).")]
        public string Query { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Filters.Open(settings);
        var replaced = db.FindFilter(settings.Name) is not null;
        var saved = db.SaveFilter(settings.Name, settings.Query);
        Console.WriteLine($"{(replaced ? "Replaced" : "Saved")} filter {saved.Name}: {saved.Query}");
        return 0;
    }
}

/// <summary><c>tickets filter delete &lt;NAME&gt;</c>: removes a saved filter.</summary>
public sealed class FilterDeleteCommand : Command<FilterDeleteCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("The filter's name.")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Filters.Open(settings);
        if (!db.DeleteFilter(settings.Name)) throw new KeyNotFoundException($"No saved filter '{settings.Name}'.");
        Console.WriteLine($"Deleted filter {settings.Name}.");
        return 0;
    }
}
