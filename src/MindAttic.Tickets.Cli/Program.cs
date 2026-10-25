using MindAttic.Tickets.Cli.Commands;
using Spectre.Console.Cli;

// Box-drawing table borders and the em dashes in ticket text need UTF-8 on a Windows console.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("tickets");
    config.PropagateExceptions();
    // An option a command doesn't declare (a typo, or one that belongs to a sibling command) is a
    // CommandParseException naming it, not a silent no-op — see M.TIX-T51.
    config.UseStrictParsing();

    config.AddBranch("ticket", t =>
    {
        t.SetDescription("Tickets: every repo's permanent ticket log, in the ticket database.");
        t.AddCommand<TicketNewCommand>("new").WithDescription("Add a ticket with the next free ID; prints the ID, and warns on stderr of open tickets in the repo whose summary reads alike (--template starts the description from the type's template).");
        t.AddCommand<TicketTemplateCommand>("template").WithDescription("Print a ticket type's description template (fix: symptom, cause, test; feature: story, acceptance); 'ticket new --template' starts from it.");
        t.AddCommand<TicketCloseCommand>("close").WithDescription("Move a ticket to closed (or wont-do).");
        t.AddCommand<TicketListCommand>("list").WithDescription("List tickets across the workspace (or one repo).");
        t.AddCommand<TicketShowCommand>("show").WithDescription("One ticket: its row, links (prerequisites included) and history; --versions lists every version, --version N shows one.");
        t.AddCommand<TicketGraphCommand>("graph").WithDescription("A ticket's requires chains both ways as an indented tree: what it waits for and what waits for it, blockers marked (--json for the nodes).");
        t.AddCommand<TicketLinkCommand>("link").WithDescription("Link two tickets or stories: tickets ticket link A requires B (--remove to unlink).");
        t.AddCommand<TicketCorrectCommand>("correct").WithDescription("Correct a ticket's type to the standard vocabulary (--type) or reword its summary (--summary), with --reason; recorded in its history.");
        t.AddCommand<TicketDescribeCommand>("describe").WithDescription("Set (--text, --file) or --clear a ticket's markdown description, kept apart from its one-line summary; recorded in its history.");
        t.AddCommand<TicketReassignCommand>("reassign").WithDescription("Give a ticket a new ID; the old one is kept in its history and still resolves.");
        t.AddCommand<TicketVerifyCommand>("verify").WithDescription("Check a repo's tickets, links and versions are valid.");
        t.AddCommand<TicketQueueCommand>("queue").WithDescription("Open tickets in queue order, with approval and priority.");
        t.AddCommand<TicketApproveCommand>("approve").WithDescription("Approve open tickets for work (--undo puts them back to proposed); with no IDs, list what waits for approval.");
        t.AddCommand<TicketRejectCommand>("reject").WithDescription("Reject an open ticket: it closes wont-do with --reason as its ref.");
        t.AddCommand<TicketPriorityCommand>("priority").WithDescription("Set a ticket's queue priority (higher runs sooner).");
        t.AddCommand<TicketEstimateCommand>("estimate").WithDescription("Show, set (estimate <ID> 300k) or --clear a ticket's effort estimate in tokens; --seed estimates every open ticket without one from the Orchestrator's TicketCosts medians. Within a priority the queue runs the smallest first.");
        t.AddCommand<TicketBumpCommand>("bump").WithDescription("Raise (or with a negative --by, lower) a ticket's priority.");
        t.AddCommand<TicketAssignCommand>("assign").WithDescription("Assign a ticket to a member's login (assign <ID> <email>): it's dispatched only on that login; --unassign takes it off them, --redelegate hands it on like an unassigned ticket.");
        t.AddCommand<TicketBlockCommand>("block").WithDescription("Make a ticket wait for others (--by X-T1, repeatable), or --clear it.");
        t.AddCommand<TicketReserveCommand>("reserve").WithDescription("Set aside tokens for a ticket so background work can't spend them first (0 releases).");
        t.AddCommand<TicketWorkingCommand>("working").WithDescription("Record the session working a ticket now (--login, --session) or --clear it; the Orchestrator writes it, Tickets Desktop links back.");
        t.AddCommand<TicketNoteCommand>("note").WithDescription("Append a comment to a ticket: tickets ticket note <ID> \"markdown\" [--author X] (- reads it from stdin). Notes are never changed or deleted.");
        t.AddBranch("label", l =>
        {
            l.SetDescription("Ticket labels and components (a component is a 'component:' label); 'tickets ticket list --label X' filters by them.");
            l.AddCommand<TicketLabelAddCommand>("add").WithDescription("Label a ticket: label add <ID> <LABEL>... (recorded in its history).");
            l.AddCommand<TicketLabelRemoveCommand>("remove").WithDescription("Take labels off a ticket: label remove <ID> <LABEL>...");
            l.AddCommand<TicketLabelListCommand>("list").WithDescription("A ticket's labels, or with no ID every label in use and how many tickets carry it.");
        });
        t.AddCommand<TicketHygieneCommand>("hygiene").WithDescription("Closed tickets that may not be done: no Ref cited, or a later ticket says it came back.");
        t.AddCommand<TicketStatsCommand>("stats").WithDescription("Ticket metrics from ticket_events: throughput and cycle time per period, and open tickets by repo and type over time (--by day|week|month, --periods N, --repo, --type, --json).");
        t.AddCommand<TicketIntakeCommand>("intake").WithDescription("File GitHub issues labelled orchestrator and CI failures on main as tickets (--dry-run to preview).");
    });

    config.AddBranch("filter", f =>
    {
        f.SetDescription("Saved filters: named ticket queries (repo: type: status: label: approval: and text) for 'ticket list --filter' and Tickets Desktop.");
        f.AddCommand<FilterListCommand>("list").WithDescription("Every saved filter and its query.");
        f.AddCommand<FilterSaveCommand>("save").WithDescription("Save (or replace) a named query: tickets filter save open-fixes \"type:fix status:open\".");
        f.AddCommand<FilterDeleteCommand>("delete").WithDescription("Delete a saved filter.");
    });

    config.AddBranch("story", s =>
    {
        s.SetDescription("Stories: every repo's user stories, in the ticket database.");
        s.AddCommand<StoryListCommand>("list").WithDescription("List stories (--repo, --status done|partial|planned).");
        s.AddCommand<StoryShowCommand>("show").WithDescription("One story: its text, evidence and links.");
        s.AddCommand<StoryStatusCommand>("status").WithDescription("Mark a story done, partial or planned.");
        s.AddCommand<StoryNewCommand>("new").WithDescription("Add a story to an epic (next free ID); prints the ID.");
        s.AddCommand<StoryEditCommand>("edit").WithDescription("Rewrite a story's text.");
    });

    config.AddBranch("epic", e =>
    {
        e.SetDescription("Epics: the themes stories are grouped under.");
        e.AddCommand<EpicNewCommand>("new").WithDescription("Add an epic to a repo.");
    });

    config.AddBranch("backlog", b =>
    {
        b.SetDescription("A repo's priority backlog.");
        b.AddCommand<BacklogAddCommand>("add").WithDescription("Append an item (story IDs in it are linked).");
    });

    config.AddBranch("db", d =>
    {
        d.SetDescription("The ticket database (MindAttic.Tickets/data/Tickets.db).");
        d.AddCommand<DbMigrateCommand>("migrate").WithDescription("Import every repo's USER_STORIES.md and verify each rebuilds byte-for-byte (--cutover then turns the files into generated views).");
        d.AddCommand<DbExportCommand>("export").WithDescription("A repo's USER_STORIES.md, rebuilt from the database.");
        d.AddCommand<DbCheckCommand>("check").WithDescription("Bad IDs, types, dates, dangling links and prerequisite cycles.");
        d.AddCommand<DbRenderCommand>("render").WithDescription("Rewrite each repo's generated USER_STORIES.md view from the database.");
        d.AddCommand<DbSyncCommand>("sync").WithDescription("Write the readable dump and commit (and push) the database.");
        d.AddBranch("server", s =>
        {
            s.SetDescription("Where the ticket database is kept: SQL Server (its own database beside Prose's) or data/Tickets.db, with each table's rows in both.");
            s.SetDefaultCommand<DbServerStatusCommand>();
            s.AddCommand<DbServerMigrateCommand>("migrate").WithDescription("Copy data/Tickets.db into SQL Server (--server, --database or --connection; the database is created), check every table's row count, then use it on this machine; --replace copies over a database that already holds tickets.");
            s.AddCommand<DbServerBackCommand>("back").WithDescription("Copy the SQL Server database back into data/Tickets.db and stop using SQL Server on this machine.");
        });
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
        r.AddCommand<ReposRenameCommand>("rename").WithDescription("Rename a repo's folder-name key everywhere the database keys it (backed up first; doesn't touch disk).");
        r.AddCommand<ReposRecodeCommand>("recode").WithDescription("Change a repo's project code, reassigning its ticket, story and epic IDs (old IDs still resolve; backed up first).");
        r.AddCommand<ReposRenamesCommand>("renames").WithDescription("List recorded repo renames (--repo to filter).");
    });

    config.AddCommand<ViewCommand>("view").WithDescription("Open a read-only page of every repo's stories and tickets in MindAttic.Browser.");
});

MindAttic.Tickets.Cli.TicketsLog.Start("Cli");
var log = MindAttic.Tickets.Cli.TicketsLog.For("Command");
var line = "tickets " + string.Join(' ', args);
try
{
    var code = app.Run(args);
    log.Information("{Command:l} exited {Code}", line, code);
    return code;
}
catch (Exception ex) when (ex is CommandParseException or CommandRuntimeException)
{
    log.Warning("{Command:l}: {Message:l}", line, ex.Message);
    Spectre.Console.AnsiConsole.MarkupLine($"[red]{Spectre.Console.Markup.Escape(ex.Message)}[/]");
    return 2;
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IOException or HttpRequestException
    or System.Data.Common.DbException or FormatException)
{
    log.Error(ex, "{Command:l} failed: {Message:l}", line, ex.Message);
    Spectre.Console.AnsiConsole.MarkupLine($"[red]error:[/] {Spectre.Console.Markup.Escape(ex.Message)}");
    return 1;
}
catch (Exception ex)
{
    log.Fatal(ex, "{Command:l} crashed", line);
    throw;
}
finally
{
    MindAttic.Tickets.Cli.TicketsLog.Stop();
}
