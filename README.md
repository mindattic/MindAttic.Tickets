# MindAttic.Workflow

A ticket engine and repo job runner for the MindAttic workspace: every repo keeps its own permanent ticket log in markdown, and one CLI queries, checks and maintains them all.

![C#](https://img.shields.io/badge/C%23-latest-239120) ![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![Status](https://img.shields.io/badge/status-work--in--progress-orange)

```text
$ workflow ticket list --status open
╭───────────────────┬───────┬────────────┬─────────┬────────┬────────────────────────────────────────╮
│ Repo              │ ID    │ Date       │ Type    │ Status │ Summary                                │
├───────────────────┼───────┼────────────┼─────────┼────────┼────────────────────────────────────────┤
│ MindAttic.Workflow│ WF-T7 │ 2026-10-12 │ feature │ open   │ /commit logs tickets through …         │
│ MindAttic.Workflow│ WF-T8 │ 2026-10-12 │ feature │ open   │ Launcher Workflow menu item            │
╰───────────────────┴───────┴────────────┴─────────┴────────┴────────────────────────────────────────╯
```

## Why

- Every change and fix gets a ticket with a stable ID that lives in the repo it belongs to, so the history travels with the code.
- Answer "what's still open?" or "what got fixed this week?" across every repo with one command.
- The rules (next ID, append-only rows, table last, unique project codes) are enforced by a tool, not remembered by an agent.
- Clean up the workspace without losing anything that only exists on this machine.

## Features

**Stories and tickets** (every repo's, in one SQLite database: `data/workflow.db`, with links such as `requires` between any two; each repo's `docs/USER_STORIES.md` is now a pointer and `workflow db export` rebuilds it)
- `ticket new` appends a row with the next free `CODE-T<n>` ID and prints it. Only that one line changes; line endings and BOM are kept.
- `ticket close` moves a ticket to `closed` or `wont-do` and can set its Ref.
- `ticket list` shows tickets across the workspace, filtered by repo, status, type or date, as a table or `--json`.
- `ticket verify` fails if the table breaks the rules or a committed row was deleted or reworded.
- `codes check` finds duplicate project codes, repos without a ticket log, and malformed tables.

**Workspace jobs**
- `repos status` shows each repo's uncommitted, unpushed and stashed work, ignored files that no build regenerates, and its GitHub state (archived, `work-in-progress` topic). Origins that predate a repo rename are resolved through GitHub's redirect.
- `repos prune` lists repos that are archived or untagged on GitHub and deletes the safe ones with `--apply` and a typed `yes`. Local-only work blocks a delete unless you name the repo in `--override`. A repo with no GitHub copy, or one another kept repo loads by relative path (a `ProjectReference`, a Unity `file:` package, a solution entry), can't be overridden.
- `repos rollout tickets` gives every repo without one a `docs/USER_STORIES.md` with a unique project code and a ticket log.

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
workflow ticket new   [--repo X] --type feature|fix|chore|docs --summary "..." [--ref ...] [--status open|closed|wont-do]
workflow ticket close <CODE-Tn> [--repo X] [--ref ...] [--wont-do]
workflow ticket list  [--repo X] [--status S] [--type T] [--since YYYY-MM-DD] [--json]
workflow ticket verify [--repo X]
workflow codes check
workflow repos status [--json]
workflow repos prune  [--apply] [--only A,B] [--override A,B]
workflow repos rollout tickets [--apply]
```

Every command takes `--root <DIR>` (default `D:\Projects\MindAttic`). `--repo` defaults to the repo containing the current directory. Exit codes: 0 success, 1 problems found or the operation failed, 2 bad arguments.

## How it works

```text
   workflow CLI  ·  Launcher menu  ·  /commit
                 |
        MindAttic.Workflow (library)
           |                    |
   Tickets/ (markdown)     Repos/ (git + GitHub)
           |                    |
 <repo>/docs/USER_STORIES.md   git -C <repo>, api.github.com/user/repos
```

There is no database or index: every query re-reads the files. The GitHub token comes from MindAttic.Vault (bucket `GitHub`, key `github`), the same one the Launcher's roster filter uses. The library has no package dependencies: the CLI (and the Launcher) read the token and pass it in.

## Project layout

```text
src/MindAttic.Workflow/            library (packed to C:\LocalNuGet)
  Tickets/   Ticket, TicketRules, StoryFile, WorkspaceTickets
  Repos/     RepoStatus, GitHubRepoIndex, DependencyScan, PrunePlanner
  Git.cs, Workspace.cs
src/MindAttic.Workflow.Cli/        the workflow exe (Spectre.Console.Cli)
tests/MindAttic.Workflow.Tests/    NUnit 4
tools/codex.ps1                    Codex doctor + digest
```

## Building and testing

```text
dotnet build MindAttic.Workflow.slnx      # TreatWarningsAsErrors, Nullable enabled
dotnet test MindAttic.Workflow.slnx
powershell -File tools/codex.ps1 doctor   # docs canon check
```

The repo-level tests run real `git` against temporary repos; nothing touches the real workspace.

## Limitations

- Windows-first: the default root is `D:\Projects\MindAttic`, and the CLI is built and tested on Windows.
- `/commit` doesn't call the CLI yet, and the Launcher has no Workflow menu item yet (see the Priority backlog).
- Only the `## Tickets` section is managed; stories are still written by hand.

## Documentation

- [docs/BIBLE.md](docs/BIBLE.md): what this project is and isn't, and its laws.
- [docs/USER_STORIES.md](docs/USER_STORIES.md): stories with their tests, the backlog, and this repo's own ticket log.
- [docs/AMENDMENTS.md](docs/AMENDMENTS.md): pending decisions.
- [AGENTS.md](AGENTS.md): agent instructions.

## License

No LICENSE file; all rights reserved.

Part of [MindAttic](https://mindattic.com) — see more projects at [github.com/mindattic](https://github.com/mindattic). Related: [MindAttic.Launcher](https://github.com/mindattic/MindAttic.Launcher) (workspace launcher) and [MindAttic.Vault](https://github.com/mindattic/MindAttic.Vault) (credential store).
