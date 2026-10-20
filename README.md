# MindAttic.Workflow

A ticket engine and repo job runner for the MindAttic workspace: every repo's stories and permanent ticket log live in one SQLite database, queried, checked and maintained by a CLI and a WPF desktop app.

![C#](https://img.shields.io/badge/C%23-latest-239120) ![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![Status](https://img.shields.io/badge/status-work--in--progress-orange)

```text
$ workflow ticket list --status open
╭────────────────────┬────────────┬────────────┬─────────┬────────┬─────────────────────────────────╮
│ Repo               │ ID         │ Date       │ Type    │ Status │ Summary                         │
├────────────────────┼────────────┼────────────┼─────────┼────────┼─────────────────────────────────┤
│ MindAttic.Workflow │ M.FLOW-T7  │ 2026-10-12 │ feature │ open   │ /commit logs tickets through …  │
│ MindAttic.Workflow │ M.FLOW-T8  │ 2026-10-12 │ feature │ open   │ Launcher Workflow menu item     │
╰────────────────────┴────────────┴────────────┴─────────┴────────┴─────────────────────────────────╯
```

## Why

- Every change and fix gets a ticket with a stable ID that lives in the repo it belongs to, so the history travels with the code.
- Answer "what's still open?" or "what got fixed this week?" across every repo with one command.
- Approve, reject and reorder the work before an agent picks it up, from the CLI or from Workflow Desktop.
- The rules (next ID, append-only rows, table last, unique project codes) are enforced by a tool, not remembered by an agent.
- Clean up the workspace without losing anything that only exists on this machine.

## Features

**Stories and tickets** (every repo's, in one SQLite database: `data/workflow.db`, with links such as `requires` between any two; each repo's `docs/USER_STORIES.md` is a generated, read-only view of it, rewritten after every change)
- `ticket new` appends a row with the next free `CODE-T<n>` ID and prints it.
- `ticket close` moves a ticket to `closed` or `wont-do` and can set its Ref.
- `ticket correct` relabels a nonstandard type to the standard vocabulary, the one sanctioned exception to append-only (recorded in the ticket's history).
- `ticket show` prints one ticket's row, links and history; `ticket link` connects two tickets or stories (`requires` is a prerequisite; a cycle is refused).
- `ticket list` shows tickets across the workspace, filtered by repo, status, type or date, as a table or `--json`.
- `ticket verify` fails if the table breaks the rules or a committed row was deleted or reworded.
- `ticket queue` lists a repo's (or every repo's) open tickets in priority order with their approval state; `ticket approve`/`ticket reject --reason` move a ticket through review (`--undo` puts an approval back to proposed; a reject closes the ticket wont-do with the reason as its ref); `ticket priority` sets where a ticket sits in that order.
- `codes check` finds duplicate project codes, repos without a ticket log, and malformed tables.
- `story new`/`edit`/`status`/`list`/`show`, `epic new`, `backlog add` add and edit a repo's stories, epics and priority backlog in the database.
- `db migrate`, `export`, `check`, `render`, `sync` import a repo's file once, rebuild it from the database, validate the database, rewrite a stale view, and commit (and push) `data/workflow.db` and its readable dump `data/workflow.sql`; a sync's commit names the tickets whose rows or history changed since the last one (or `--ticket ID`), and when a change has nothing to name (a story, epic or backlog edit alone) it opens and closes a chore ticket to carry it instead, so a sync with something to commit always names a ticket.
- `view` opens a read-only page of every repo's tickets and stories, with filters, in MindAttic.Browser (or the default browser). It's a snapshot; run it again to refresh.

**Workflow Desktop** (`src/MindAttic.Workflow.Desktop`, a WPF app built on MindAttic.Components.Wpf, matching the Orchestrator's look)
- Queue and Tickets views (Ctrl+1/Ctrl+2), filterable by repo, status, type and approval and searchable (Ctrl+F), with closed and wont-do tickets hidden until you show archived.
- Approve, unapprove, reject and reorder (move up/down/to top) queued tickets; file a new ticket (Ctrl+N); close, mark wont-do, correct a ticket's type or add a link from its detail pane.
- A Sync button (Ctrl+Shift+S) runs `workflow db sync` and shows the result; F5 refreshes.
- `MindAttic.Workflow.Desktop.exe --root <dir> --ticket <ID>` opens on that ticket, selected with its detail pane open (an open one in the Queue, to approve, move or close; a closed one in Tickets). Started again while the window is open, it hands the id to that window and brings it forward. The Orchestrator dashboard's ticket links start it this way.
- `deploy.ps1` publishes it as a self-contained exe to `C:\Apps\MindAttic.Workflow.Desktop` with Desktop and Start-menu shortcuts (and reinstalls the `workflow` CLI alongside it, via `tools/install.ps1`, unless `-SkipCli`).

**Workspace jobs**
- `repos status` shows each repo's uncommitted, unpushed and stashed work, ignored files that no build regenerates, and its GitHub state (archived, `work-in-progress` topic). Origins that predate a repo rename are resolved through GitHub's redirect.
- `repos prune` lists repos that are archived or untagged on GitHub and deletes the safe ones with `--apply` and a typed `yes`. Local-only work blocks a delete unless you name the repo in `--override`. A repo with no GitHub copy, or one another kept repo loads by relative path (a `ProjectReference`, a Unity `file:` package, a solution entry), can't be overridden.
- `repos rollout tickets` gives every repo without one a `docs/USER_STORIES.md` with a unique project code and a ticket log.
- `repos rename` renames a repo's folder-name key everywhere the database keys it (backed up first; it doesn't touch anything on disk — rename the folder yourself) and is kept forever in the repo's rename history and in every one of its tickets' history; `repos renames` lists recorded renames.

## Quick start

Prerequisites: the .NET 10 SDK, `git` on PATH, and (for the GitHub columns and `prune`) a GitHub token in MindAttic.Vault, set from the Launcher's Settings menu.

```text
git clone https://github.com/mindattic/MindAttic.Workflow.git
cd MindAttic.Workflow
dotnet build MindAttic.Workflow.slnx
dotnet run --project src/MindAttic.Workflow.Cli -- ticket list
```

You should see every ticket in every repo under `D:\Projects\MindAttic`.

## Commands

```text
workflow ticket new     [--repo X] --type feature|fix|chore|docs|interview --summary "..." [--ref ...] [--status open|closed|wont-do]
workflow ticket close   <CODE-Tn> [--repo X] [--ref ...] [--wont-do]
workflow ticket correct <CODE-Tn> --type feature|fix|chore|docs|interview --reason "..."
workflow ticket show    <CODE-Tn> [--json]
workflow ticket link    <FROM> requires|related|follow-up-of|supersedes|duplicates|implements|part-of <TO> [--remove] [--note "..."]
workflow ticket list    [--repo X] [--status S] [--type T] [--since YYYY-MM-DD] [--json]
workflow ticket verify  [--repo X]
workflow ticket queue   [--repo X] [--json]
workflow ticket approve <CODE-Tn> [--undo]
workflow ticket reject  <CODE-Tn> --reason "..."
workflow ticket priority <CODE-Tn> <PRIORITY>
workflow codes check
workflow story  list    [--repo X] [--status done|partial|planned] [--json]
workflow story  show    <CODE-US-...>
workflow story  new     [--repo X] --epic KEY --text "..." [--status planned|partial|done]
workflow story  edit    <CODE-US-...> --text "..." [--repo X]
workflow story  status  <CODE-US-...> done|partial|planned [--repo X]
workflow epic   new     [--repo X] --key KEY --title "..."
workflow backlog add    [--repo X] --text "..."
workflow db migrate     [--cutover] [--db FILE]
workflow db export      [--repo X] [--out FILE]
workflow db check       [--fix]
workflow db render      [--repo X]
workflow db sync        [--no-push] [--ticket ID]
workflow view [--repo X] [--out DIR] [--no-open]
workflow repos status [--json]
workflow repos prune  [--apply] [--only A,B] [--override A,B]
workflow repos rollout tickets [--apply]
workflow repos rename   <OLD> <NEW> [--reason "..."]
workflow repos renames  [--repo X]
```

Every command takes `--root <DIR>` (default `D:\Projects\MindAttic`). `--repo` defaults to the repo containing the current directory. Exit codes: 0 success, 1 problems found or the operation failed, 2 bad arguments.

## How it works

```text
   workflow CLI  ·  Workflow Desktop (WPF)  ·  Launcher menu  ·  /commit
                 |
        MindAttic.Workflow (library)
           |                              |
   Data/ (SQLite, WorkflowDb)       Repos/ (git + GitHub)
           |                              |
  data/workflow.db, data/workflow.sql,    git -C <repo>, api.github.com/user/repos
  <repo>/docs/USER_STORIES.md (generated view)
```

Every repo's stories, epics, backlog and tickets (and the links between them) live in `data/workflow.db`, committed to this repo alongside a readable SQL dump (`data/workflow.sql`) so changes show up as text diffs. A repo's `docs/USER_STORIES.md` is rewritten from the database after every change that touches it (and by `workflow db render`/`sync`); it's never read back in, so a hand edit is only ever a mistake `db check` flags. The GitHub token comes from MindAttic.Vault (bucket `GitHub`, key `github`), the same one the Launcher's roster filter uses. The library has no package dependencies: the CLI (and the Launcher) read the token and pass it in.

## Project layout

```text
src/MindAttic.Workflow/            library (packed to C:\LocalNuGet)
  Data/      WorkflowDb (schema, import/export, tickets, stories, links, history, the ticket queue, sync-commit subjects)
  Stories/   StoryDocument (parses a USER_STORIES.md into ordered blocks)
  Tickets/   Ticket, TicketRules, StoryFile, WorkspaceTickets (the markdown fallback, pre-migration)
  Repos/     RepoStatus, GitHubRepoIndex, DependencyScan, PrunePlanner
  Git.cs, Workspace.cs
src/MindAttic.Workflow.Cli/        the workflow exe (Spectre.Console.Cli)
src/MindAttic.Workflow.Desktop/    Workflow Desktop, a WPF ticket app (net10.0-windows, MindAttic.Components.Wpf)
tests/MindAttic.Workflow.Tests/    NUnit 4
tests/MindAttic.Workflow.Desktop.Tests/  NUnit 4: Workflow Desktop's view model and store against a temporary database
tools/build-readme.ps1             regenerates README.htm from README.md (wraps codex-standard's shared engine)
tools/codex.ps1                    Codex doctor + digest
tools/install.ps1                  packs the library and CLI and installs `workflow` as a global tool
deploy.ps1                         publishes Workflow Desktop and (re)creates its shortcuts
```

## Building and testing

```text
dotnet build MindAttic.Workflow.slnx      # TreatWarningsAsErrors, Nullable enabled; also builds Workflow Desktop (Windows)
dotnet test MindAttic.Workflow.slnx
powershell -File tools/codex.ps1 doctor   # docs canon check
```

The repo-level tests run real `git` against temporary repos; nothing touches the real workspace.

Install the CLI as a global `workflow` tool with `powershell -File tools/install.ps1` (packs the library and CLI to
`C:\LocalNuGet` and installs or updates the tool from there). Publish and install Workflow Desktop, with its Desktop
and Start-menu shortcuts, with `powershell -File deploy.ps1` (add `-Launch` to open it when done).

## Limitations

- Windows-first: the default root is `D:\Projects\MindAttic`, the CLI is built and tested on Windows, and Workflow Desktop (WPF, `net10.0-windows`) only runs on Windows at all.
- Output piped or redirected still carries ANSI color codes in places and doesn't yet honor `NO_COLOR`; `story list` wraps rather than truncating in a narrow terminal. See the Priority backlog in [docs/USER_STORIES.md](docs/USER_STORIES.md) for these and other open gaps (restoring the db from its dump, keeping `data/workflow.db` out of ordinary feature commits, concurrent writers).

## Documentation

- [docs/BIBLE.md](docs/BIBLE.md): what this project is and isn't, and its laws.
- [docs/USER_STORIES.md](docs/USER_STORIES.md): stories with their tests, the backlog, and this repo's own ticket log.
- [docs/AMENDMENTS.md](docs/AMENDMENTS.md): pending decisions.
- [docs/VIEWER.md](docs/VIEWER.md): how `workflow view` works and why it's a static snapshot.
- [AGENTS.md](AGENTS.md): agent instructions.

## License

No LICENSE file; all rights reserved.

Part of [MindAttic](https://mindattic.com) — see more projects at [github.com/mindattic](https://github.com/mindattic). Related: [MindAttic.Launcher](https://github.com/mindattic/MindAttic.Launcher) (workspace launcher) and [MindAttic.Vault](https://github.com/mindattic/MindAttic.Vault) (credential store).
