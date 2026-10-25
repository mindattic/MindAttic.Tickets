# MindAttic.Tickets

A ticket engine and repo job runner for the MindAttic workspace: every repo's stories and permanent ticket log live in one SQLite database, queried, checked and maintained by a CLI and a WPF desktop app.

![C#](https://img.shields.io/badge/C%23-latest-239120) ![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![Status](https://img.shields.io/badge/status-work--in--progress-orange)

```text
$ tickets ticket list --status open
╭────────────────────┬────────────┬────────────┬─────────┬────────┬─────────────────────────────────╮
│ Repo               │ ID         │ Date       │ Type    │ Status │ Summary                         │
├────────────────────┼────────────┼────────────┼─────────┼────────┼─────────────────────────────────┤
│ MindAttic.Tickets │ M.TIX-T7  │ 2026-10-12 │ feature │ open   │ /commit logs tickets through …  │
│ MindAttic.Tickets │ M.TIX-T8  │ 2026-10-12 │ feature │ open   │ Launcher Workflow menu item     │
╰────────────────────┴────────────┴────────────┴─────────┴────────┴─────────────────────────────────╯
```

## Why

- Every change and fix gets a ticket with a stable ID that lives in the repo it belongs to, so the history travels with the code.
- Answer "what's still open?" or "what got fixed this week?" across every repo with one command.
- Approve, reject and reorder the work before an agent picks it up, from the CLI or from Tickets Desktop.
- The rules (next ID, append-only rows, table last, unique project codes) are enforced by a tool, not remembered by an agent.
- Clean up the workspace without losing anything that only exists on this machine.

## Features

**Stories and tickets** (every repo's, in one SQLite database: `data/Tickets.db`, with links such as `requires` between any two; each repo's `docs/USER_STORIES.md` is a generated, read-only view of it, rewritten after every change)
- `ticket new` appends a row with the next free `CODE-T<n>` ID and prints it. It warns (on stderr, still filing) of open tickets in the repo whose summary reads alike, the same check Tickets Desktop's new-ticket dialog shows as you type. `--template` starts the description from the type's template (fix: symptom, cause, test; feature: story, acceptance; `ticket template TYPE` prints it), and the Desktop dialog starts from it too, swapping it as the type changes while it's untouched; an untouched template is never filed as a description.
- `ticket close` moves a ticket to `closed` or `wont-do` and can set its Ref.
- `ticket correct` relabels a nonstandard type to the standard vocabulary, the one sanctioned exception to append-only (recorded in the ticket's history).
- `ticket show` prints a ticket end to end: its row, queue state, assignee, reservation, the session working it, links, history and its commits: those whose subject names it, each with its GitHub link, as the Desktop detail pane lists them too (`--diff` adds their full diffs); `--versions` lists every version of it (on SQL Server, where tickets, their approval and priority, their work and links are system-versioned temporal tables, every save is kept, never pruned; on the SQLite file, versions are worked back from its history) and `--version N` shows one, its changes marked. `ticket link` connects two tickets or stories (`requires` is a prerequisite; a cycle is refused).
- `ticket list` shows tickets across the workspace, filtered by repo, status, type or date, as a table or `--json`. `--query` takes the ticket query language: `repo:` (a name or a ticket code), `type:`, `status:`, `label:`, `approval:` and free text (matched against ID, summary, ref and repo; quote it to keep spaces), every term required; commas give alternatives (`status:open,wont-do`), a leading `-` negates (`--query=-type:chore`), `*` is a wildcard in repo and label. An unknown field or value is refused, saying what's allowed. `--format csv|md` exports the filtered list as CSV (RFC 4180, a leading `=`/`+`/`-`/`@` defused) or a markdown table, and `--output FILE` writes it to a file (UTF-8; a `.csv` or `.md` name picks the format).
- `filter save <NAME> <QUERY>` / `filter list` / `filter delete <NAME>` keep named queries in the database (`saved_filters`); `ticket list --filter <NAME>` runs one (and `--query` narrows it). Tickets Desktop shares them.
- `ticket verify` fails if the table breaks the rules or a committed row was deleted or reworded. It checks the versions too: a table no longer system-versioned, a history row that runs backwards, or a ticket whose last recorded change isn't what it says now.
- `ticket queue` lists a repo's (or every repo's) open tickets in priority order with their approval state; `ticket approve`/`ticket reject --reason` move a ticket through review (`--undo` puts an approval back to proposed; a reject closes the ticket wont-do with the reason as its ref); `ticket priority` sets where a ticket sits in that order and `ticket bump` moves it up or down. `ticket estimate` keeps each ticket's effort estimate in tokens (`ticket_queue.estimate`, editable in Tickets Desktop too); within one priority the queue runs the smallest estimate first, and `ticket estimate --seed` fills in open tickets without one from the median cost of finished tickets of the same type and repo in the Orchestrator's TicketCosts.
- The ticket functions the Orchestrator used to own (M.TIX-T134; the Orchestrator now only runs the work and reads these from the database): `ticket assign <ID> <email>` assigns a ticket to a member's login (`--unassign`, `--redelegate`), `ticket block <ID> --by X` makes it wait for others (`--clear`), `ticket reserve <ID> 2M` sets tokens aside for it (0 releases), `ticket working <ID> --login --session` records the session working it (`--clear`), `ticket hygiene` lists closed tickets that may not be done, and `ticket intake` files GitHub issues labelled `orchestrator` and CI failures on main as tickets (`--dry-run`).
- `codes check` finds duplicate project codes, repos without a ticket log, and malformed tables.
- `story new`/`edit`/`status`/`list`/`show`, `epic new`, `backlog add` add and edit a repo's stories, epics and priority backlog in the database.
- `db migrate`, `export`, `check`, `render`, `sync` import a repo's file once, rebuild it from the database, validate the database, rewrite a stale view, and commit (and push) `data/Tickets.db` and its readable dump `data/Tickets.sql`; a sync's commit names the tickets whose rows or history changed since the last one (or `--ticket ID`), and when a change has nothing to name (a story, epic or backlog edit alone) it opens and closes a chore ticket to carry it instead, so a sync with something to commit always names a ticket.
- `view` opens a read-only page of every repo's tickets and stories, with filters, in MindAttic.Browser (or the default browser). It's a snapshot; run it again to refresh.

**Tickets Desktop** (`src/MindAttic.Tickets.Desktop`, a WPF app built on MindAttic.Components.Wpf, matching the Orchestrator's look)
- Tabs Repos | Queue | Audit (Ctrl+1/2/3), filterable by repo, status, type, approval and work (queued, needs approval, blocked) and searchable (Ctrl+F), with closed and wont-do tickets hidden until you show archived (or a query names a status). The Query box takes the same query language as `ticket list --query` (a query that doesn't parse says why under it), and Saved filter picks, saves and deletes the named queries `tickets filter` keeps. Export… (Ctrl+E) saves the tickets the current view shows, every filter, the query and the sort applied, as CSV or markdown.
- Repos: every repo's tickets under its name, with its counts, git state (branch, ahead/behind, changed files) and a Start link that opens the Orchestrator to start a CLI there.
- Queue: open tickets in the order /allocate hands them out; a claimed ticket shows its live worker (person, login, model, or orphaned) from the Orchestrator's database, read-only, as a link that opens the Orchestrator on it (O on the row).
- Audit: ticket hygiene (closed fixes and features with no Ref, or that a later ticket says came back, as `tickets ticket hygiene`; each links to its ticket) and the Orchestrator's last audit run (shared agent files that drifted or are missing, duplicate code across repos), read from its database.
- GitHub intake (`tickets ticket intake`): asks GitHub for issues labelled `orchestrator` and CI failures on main, shows the dry run, and files exactly those tickets only when you click File; nothing is written to GitHub.
- Approve, unapprove, reject and reorder (move up/down/to top) queued tickets; file a new ticket (Ctrl+N); close, mark wont-do, correct a ticket's type, add a link, assign it to a member's login or reserve tokens for it from its detail pane.
- A ticket being worked shows its login and session as links that open the Orchestrator on them (`MindAttic.Orchestrator.exe --show <email|session|ticket>`; nothing happens beyond a message when the Orchestrator isn't installed).
- A ticket's detail pane steps through every version of it with its ◀ ▶ arrows (or Left/Right), the rightmost the current one: each shows its version number and time, with what it changed from the version before marked.
- A ticket with `requires` links shows a Dependencies section: its chains both ways drawn in rows (what it waits for above it, farthest first; what waits for it below), what holds work up now outlined in red and said in words, each ticket a button that goes to it, and the same graph as an indented text list for screen readers (`tickets ticket graph` prints it too).
- Command palette (Ctrl+K, or the Commands… button): one box to jump to any ticket or story by ID or text, fuzzily, or run any command that can run now by name (`>` lists commands only, `#` tickets and stories only). Up/Down choose and Enter runs without leaving the box; a screen reader hears the result count and the selected entry as they change. A story opens with its text and links, and any linked ticket opens from there.
- A Sync button (Ctrl+Shift+S) runs `tickets db sync` and shows the result; F5 refreshes.
- `MindAttic.Tickets.Desktop.exe --root <dir> --ticket <ID>` opens on that ticket, selected with its detail pane open (an open one in the Queue, to approve, move or close; a closed one in Repos). Started again while the window is open, it hands the id to that window and brings it forward. The Orchestrator dashboard's ticket links start it this way.
- `deploy.ps1` publishes it as a self-contained exe to `C:\Apps\MindAttic.Tickets.Desktop` with Desktop and Start-menu shortcuts (and reinstalls the `tickets` CLI alongside it, via `tools/install.ps1`, unless `-SkipCli`).

**Workspace jobs**
- `repos status` shows each repo's uncommitted, unpushed and stashed work, ignored files that no build regenerates, and its GitHub state (archived, `work-in-progress` topic). Origins that predate a repo rename are resolved through GitHub's redirect.
- `repos prune` lists repos that are archived or untagged on GitHub and deletes the safe ones with `--apply` and a typed `yes`. Local-only work blocks a delete unless you name the repo in `--override`. A repo with no GitHub copy, or one another kept repo loads by relative path (a `ProjectReference`, a Unity `file:` package, a solution entry), can't be overridden.
- `repos rollout tickets` gives every repo without one a `docs/USER_STORIES.md` with a unique project code and a ticket log.
- `repos rename` renames a repo's folder-name key everywhere the database keys it (backed up first; it doesn't touch anything on disk — rename the folder yourself) and is kept forever in the repo's rename history and in every one of its tickets' history; `repos renames` lists recorded renames.

## Quick start

Prerequisites: the .NET 10 SDK, `git` on PATH, and (for the GitHub columns and `prune`) a GitHub token in MindAttic.Vault, set from the Launcher's Settings menu.

```text
git clone https://github.com/mindattic/MindAttic.Tickets.git
cd MindAttic.Tickets
dotnet build MindAttic.Tickets.slnx
dotnet run --project src/MindAttic.Tickets.Cli -- ticket list
```

You should see every ticket in every repo under `D:\Projects\MindAttic`.

## Commands

```text
tickets ticket new     [--repo X] --type feature|fix|chore|docs|interview --summary "..." [--ref ...] [--status open|closed|wont-do] [--template | --description "..." | --description-file F]
tickets ticket template feature|fix|chore|docs|interview   # print the type's description template
tickets ticket close   <CODE-Tn> [--repo X] [--ref ...] [--wont-do]
tickets ticket correct <CODE-Tn> --type feature|fix|chore|docs|interview --reason "..."
tickets ticket reassign <CODE-Tn> <CODE-Tm> --reason "..."   # the old ID is kept in id_history and still resolves
tickets ticket show    <CODE-Tn> [--json] [--diff] [--versions | --version N]
tickets ticket graph   <CODE-Tn> [--json]   # requires chains both ways as an indented tree, blockers marked
tickets ticket link    <FROM> requires|related|follow-up-of|supersedes|duplicates|implements|part-of <TO> [--remove] [--note "..."]
tickets ticket list    [--repo X] [--status S] [--type T] [--since YYYY-MM-DD] [--query "repo:X type:fix 'text'"] [--filter NAME] [--json | --format table|json|csv|md] [--output FILE]
tickets filter save    <NAME> <QUERY>   # also: tickets filter list [--json], tickets filter delete <NAME>
tickets ticket verify  [--repo X]
tickets ticket queue   [--repo X] [--json]
tickets ticket approve [<CODE-Tn> ...] [--undo]    # no IDs: list what waits for approval
tickets ticket reject  <CODE-Tn> --reason "..."
tickets ticket priority <CODE-Tn> <PRIORITY>
tickets ticket bump    <CODE-Tn> [--by N]
tickets ticket estimate <CODE-Tn> [<TOKENS> | --clear]   # effort estimate, like 300k; within a priority the queue runs the smallest first
tickets ticket estimate --seed [--repo X] [--overwrite] [--dry-run]   # estimate open tickets from the Orchestrator's TicketCosts medians
tickets ticket assign  <CODE-Tn> [<email>] [--unassign | --redelegate]
tickets ticket block   <CODE-Tn> [--by <CODE-Tm> ...] [--clear]
tickets ticket reserve <CODE-Tn> <TOKENS>          # like 2M or 500k; 0 releases
tickets ticket working <CODE-Tn> [--login <email>] [--session <id>] | --clear
tickets ticket note <CODE-Tn> "<markdown>" [--author <name>]   # append-only comment (- reads stdin); show lists them
tickets ticket hygiene [--repo X] [--json]
tickets ticket intake  [--repo X] [--dry-run]
tickets codes check
tickets story  list    [--repo X] [--status done|partial|planned] [--json]
tickets story  show    <CODE-US-...>
tickets story  new     [--repo X] --epic KEY --text "..." [--status planned|partial|done]
tickets story  edit    <CODE-US-...> --text "..." [--repo X]
tickets story  status  <CODE-US-...> done|partial|planned [--repo X]
tickets epic   new     [--repo X] --key KEY --title "..."
tickets backlog add    [--repo X] --text "..."
tickets db migrate     [--cutover] [--db FILE]
tickets db export      [--repo X] [--out FILE]
tickets db check       [--fix]
tickets db render      [--repo X]
tickets db sync        [--no-push] [--ticket ID]
tickets view [--repo X] [--out DIR] [--no-open]
tickets repos status [--json]
tickets repos prune  [--apply] [--only A,B] [--override A,B]
tickets repos rollout tickets [--apply]
tickets repos rename   <OLD> <NEW> [--reason "..."]
tickets repos renames  [--repo X]
tickets repos recode   <REPO> <CODE> --reason "..."   # every ticket/story/epic ID moves to the new code; old IDs still resolve
```

Every command takes `--root <DIR>` (default `D:\Projects\MindAttic`). `--repo` defaults to the repo containing the current directory. Exit codes: 0 success, 1 problems found or the operation failed, 2 bad arguments.

## How it works

```text
   tickets CLI  ·  Tickets Desktop (WPF)  ·  Launcher menu  ·  /commit
                 |
        MindAttic.Tickets (library)
           |                              |
   Data/ (SQLite, TicketsDb)       Repos/ (git + GitHub)
           |                              |
  data/Tickets.db, data/Tickets.sql,    git -C <repo>, api.github.com/user/repos
  <repo>/docs/USER_STORIES.md (generated view)
```

Every repo's stories, epics, backlog and tickets (and the links between them) live in `data/Tickets.db`, committed to this repo alongside a readable SQL dump (`data/Tickets.sql`) so changes show up as text diffs. A repo's `docs/USER_STORIES.md` is rewritten from the database after every change that touches it (and by `tickets db render`/`sync`); it's never read back in, so a hand edit is only ever a mistake `db check` flags. The GitHub token comes from MindAttic.Vault (bucket `GitHub`, key `github`), the same one the Launcher's roster filter uses. The library has no package dependencies: the CLI (and the Launcher) read the token and pass it in.

## Project layout

```text
src/MindAttic.Tickets/            library (packed to C:\LocalNuGet)
  Data/      TicketsDb (schema, import/export, tickets, stories, links, history, the ticket queue, sync-commit subjects)
  Stories/   StoryDocument (parses a USER_STORIES.md into ordered blocks)
  Tickets/   Ticket, TicketRules, StoryFile, WorkspaceTickets (the markdown fallback, pre-migration)
  Repos/     RepoStatus, GitHubRepoIndex, DependencyScan, PrunePlanner
  Git.cs, Workspace.cs
src/MindAttic.Tickets.Cli/        the tickets exe (Spectre.Console.Cli)
src/MindAttic.Tickets.Desktop/    Tickets Desktop, a WPF ticket app (net10.0-windows, MindAttic.Components.Wpf)
tests/MindAttic.Tickets.Tests/    NUnit 4
tests/MindAttic.Tickets.Desktop.Tests/  NUnit 4: Tickets Desktop's view model and store against a temporary database
tools/codex.ps1                    Codex doctor + digest
tools/install.ps1                  packs the library and CLI and installs `tickets` as a global tool
deploy.ps1                         publishes Tickets Desktop and (re)creates its shortcuts
```

## Building and testing

```text
dotnet build MindAttic.Tickets.slnx      # TreatWarningsAsErrors, Nullable enabled; also builds Tickets Desktop (Windows)
dotnet test MindAttic.Tickets.slnx
powershell -File tools/codex.ps1 doctor   # docs canon check
```

The repo-level tests run real `git` against temporary repos; nothing touches the real workspace.

Install the CLI as a global `tickets` tool with `powershell -File tools/install.ps1` (packs the library and CLI to
`C:\LocalNuGet` and installs or updates the tool from there). Publish and install Tickets Desktop, with its Desktop
and Start-menu shortcuts, with `powershell -File deploy.ps1` (add `-Launch` to open it when done).

## Limitations

- Windows-first: the default root is `D:\Projects\MindAttic`, the CLI is built and tested on Windows, and Tickets Desktop (WPF, `net10.0-windows`) only runs on Windows at all.
- Output piped or redirected still carries ANSI color codes in places and doesn't yet honor `NO_COLOR`; `story list` wraps rather than truncating in a narrow terminal. See the Priority backlog in [docs/USER_STORIES.md](docs/USER_STORIES.md) for these and other open gaps (restoring the db from its dump, keeping `data/Tickets.db` out of ordinary feature commits, concurrent writers).

## Documentation

- [docs/BIBLE.md](docs/BIBLE.md): what this project is and isn't, and its laws.
- [docs/USER_STORIES.md](docs/USER_STORIES.md): stories with their tests, the backlog, and this repo's own ticket log.
- [docs/AMENDMENTS.md](docs/AMENDMENTS.md): pending decisions.
- [docs/VIEWER.md](docs/VIEWER.md): how `tickets view` works and why it's a static snapshot.
- [AGENTS.md](AGENTS.md): agent instructions.

## License

No LICENSE file; all rights reserved.

Part of [MindAttic](https://mindattic.com) — see more projects at [github.com/mindattic](https://github.com/mindattic). Related: [MindAttic.Launcher](https://github.com/mindattic/MindAttic.Launcher) (workspace launcher) and [MindAttic.Vault](https://github.com/mindattic/MindAttic.Vault) (credential store).
