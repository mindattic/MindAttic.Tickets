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
        t.SetDescription("Tickets: every repo's permanent ticket log, in the workflow database.");
        t.AddCommand<TicketNewCommand>("new").WithDescription("Add a ticket with the next free ID; prints the ID.");
        t.AddCommand<TicketCloseCommand>("close").WithDescription("Move a ticket to closed (or wont-do).");
        t.AddCommand<TicketListCommand>("list").WithDescription("List tickets across the workspace (or one repo).");
        t.AddCommand<TicketShowCommand>("show").WithDescription("One ticket: its row, links (prerequisites included) and history.");
        t.AddCommand<TicketLinkCommand>("link").WithDescription("Link two tickets or stories: workflow ticket link A requires B (--remove to unlink).");
        t.AddCommand<TicketVerifyCommand>("verify").WithDescription("Check a repo's tickets and links are valid.");
    });

    config.AddBranch("story", s =>
    {
        s.SetDescription("Stories: every repo's user stories, in the workflow database.");
        s.AddCommand<StoryListCommand>("list").WithDescription("List stories (--repo, --status done|partial|planned).");
        s.AddCommand<StoryShowCommand>("show").WithDescription("One story: its text, evidence and links.");
        s.AddCommand<StoryStatusCommand>("status").WithDescription("Mark a story done, partial or planned.");
    });

    config.AddBranch("db", d =>
    {
        d.SetDescription("The workflow database (MindAttic.Workflow/data/workflow.db).");
        d.AddCommand<DbMigrateCommand>("migrate").WithDescription("Import every repo's USER_STORIES.md and verify each rebuilds byte-for-byte (--cutover then replaces the files with pointers).");
        d.AddCommand<DbExportCommand>("export").WithDescription("A repo's USER_STORIES.md, rebuilt from the database.");
        d.AddCommand<DbCheckCommand>("check").WithDescription("Bad IDs, types, dates, dangling links and prerequisite cycles.");
        d.AddCommand<DbSyncCommand>("sync").WithDescription("Write the readable dump and commit (and push) the database.");
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
