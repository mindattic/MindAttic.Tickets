using System.ComponentModel;
using MindAttic.Tickets.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Tickets.Cli.Commands;

/// <summary><c>tickets db server</c> (M.TIX-T131): where the ticket database is kept, and moving it between
/// data/Tickets.db (SQLite) and its SQL Server database.</summary>
public sealed class DbServerStatusCommand : Command<WorkspaceSettings>
{
    public override int Execute(CommandContext context, WorkspaceSettings settings)
    {
        using var db = Db.Open(settings);
        AnsiConsole.MarkupLine(db.OnServer
            ? $"[bold]SQL Server[/]: the ticket database is in {Output.Esc(db.Store)}; {Output.Esc(db.Path)} is its copy, rewritten after every change (the hooks and git read it)."
            : $"[bold]SQLite[/]: the ticket database is {Output.Esc(db.Path)}. Run [blue]tickets db server migrate[/] to move it to SQL Server.");
        if (db.LastCopyError is { } error) AnsiConsole.MarkupLine($"[yellow]The last copy into the file failed: {Output.Esc(error)}[/]");
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Table").AddColumn(db.OnServer ? "SQL Server" : "Store", c => c.RightAligned()).AddColumn("Tickets.db", c => c.RightAligned());
        foreach (var (name, store, file) in db.Counts())
            table.AddRow(name, store.ToString(System.Globalization.CultureInfo.InvariantCulture), file.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class DbServerMigrateCommand : Command<DbServerMigrateCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--server <NAME>")]
        [Description("The SQL Server instance (default: localhost, the full instance beside the Prose database).")]
        [DefaultValue("localhost")]
        public string Server { get; init; } = "localhost";

        [CommandOption("--database <NAME>")]
        [Description("The database to keep it in (created when it isn't there).")]
        [DefaultValue(TicketsServer.DefaultDatabase)]
        public string Database { get; init; } = TicketsServer.DefaultDatabase;

        [CommandOption("--connection <STRING>")]
        [Description("A full connection string, instead of --server and --database.")]
        public string? Connection { get; init; }

        [CommandOption("--replace")]
        [Description("Copy over a SQL Server database that already holds tickets.")]
        public bool Replace { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var file = TicketsDb.DefaultPath(settings.Workspace.Root);
        var connection = settings.Connection ?? TicketsServer.DefaultConnection(settings.Server, settings.Database);
        foreach (var c in TicketsDb.MigrateToServer(file, connection, settings.Replace))
            AnsiConsole.MarkupLine($"  {c.Table,-14} {c.Rows,7} rows");
        AnsiConsole.MarkupLine($"[green]The ticket database is in {Output.Esc(new TicketsServer(connection).Describe())} now[/]; every tickets command, " +
            $"Tickets Desktop and the orchestrator use it at their next open. {Output.Esc(file)} stays its copy. [blue]tickets db server back[/] returns it to SQLite.");
        return 0;
    }
}

public sealed class DbServerBackCommand : Command<WorkspaceSettings>
{
    public override int Execute(CommandContext context, WorkspaceSettings settings)
    {
        var file = TicketsDb.DefaultPath(settings.Workspace.Root);
        if (TicketsServer.Configured() is not { } connection)
        {
            AnsiConsole.MarkupLine($"The ticket database is {Output.Esc(file)} already.");
            return 0;
        }
        foreach (var c in TicketsDb.MigrateToSqlite(file, connection)) AnsiConsole.MarkupLine($"  {c.Table,-14} {c.Rows,7} rows");
        AnsiConsole.MarkupLine($"[green]{Output.Esc(file)} is the ticket database again[/]; the SQL Server database is left as it was.");
        return 0;
    }
}
