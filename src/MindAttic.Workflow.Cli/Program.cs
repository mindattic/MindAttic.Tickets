using MindAttic.Workflow.Cli.Commands;
using Spectre.Console.Cli;

// Box-drawing table borders and the em dashes in ticket text need UTF-8 on a Windows console.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("workflow");
    config.PropagateExceptions();

    config.AddBranch("ticket", t =>
    {
        t.SetDescription("Tickets in a repo's docs/USER_STORIES.md (the repo's permanent ticket log).");
        t.AddCommand<TicketNewCommand>("new").WithDescription("Append a ticket with the next free ID; prints the ID.");
        t.AddCommand<TicketCloseCommand>("close").WithDescription("Move a ticket to closed (or wont-do).");
        t.AddCommand<TicketListCommand>("list").WithDescription("List tickets across the workspace (or one repo).");
        t.AddCommand<TicketVerifyCommand>("verify").WithDescription("Check a repo's table is valid and no committed row was deleted or reworded.");
    });

    config.AddBranch("codes", c =>
    {
        c.SetDescription("Project codes that prefix ticket IDs.");
        c.AddCommand<CodesCheckCommand>("check").WithDescription("Workspace checks: duplicate codes, missing files, malformed tables.");
    });

    config.AddBranch("repos", r =>
    {
        r.SetDescription("Workspace-wide repo jobs.");
        r.AddCommand<ReposStatusCommand>("status").WithDescription("Local-only work and GitHub state for every repo.");
        r.AddCommand<ReposPruneCommand>("prune").WithDescription("Delete folders of archived / untagged repos that are safe to remove (preview unless --apply).");
        r.AddCommand<ReposRolloutTicketsCommand>("rollout").WithDescription("Give every repo a USER_STORIES.md with a ticket log (preview unless --apply).");
    });
});

try
{
    return app.Run(args);
}
catch (Exception ex) when (ex is CommandParseException or CommandRuntimeException)
{
    Spectre.Console.AnsiConsole.MarkupLine($"[red]{Spectre.Console.Markup.Escape(ex.Message)}[/]");
    return 2;
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IOException or HttpRequestException)
{
    Spectre.Console.AnsiConsole.MarkupLine($"[red]error:[/] {Spectre.Console.Markup.Escape(ex.Message)}");
    return 1;
}
