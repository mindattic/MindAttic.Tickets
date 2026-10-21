---
codex: 1
project: MindAttic.Tickets
code: M.TIX
layer: bible
status: living
updated: 2026-10-19
---

# MindAttic.Tickets — Project Bible
> Single source of truth for what MindAttic.Tickets IS, is NOT, and the rules that keep it coherent.
> README says how to build/run; this says how to think about the system.

## 1. The one sentence {#M.TIX-§1}
MindAttic.Tickets is the workspace's ticket engine and repo job runner: a .NET library plus a
`tickets` CLI that keeps every repo's stories and permanent ticket log in one SQLite database
(`data/Tickets.db`) and runs workspace-wide jobs (status, prune, rollout) safely across every
repo under `D:\Projects\MindAttic`.

## 2. The product promise {#M.TIX-§2}
- Every change and fix gets a ticket with a stable `CODE-T<n>` ID, kept forever in the repo it
  belongs to, so history travels with the code in git.
- One query answers "every open ticket", "what got fixed this week", across all repos.
- Rules are enforced by a tool, not remembered by an agent: next ID, vocabulary, append-only rows,
  table last, unique project codes.
- Workspace jobs are repeatable and safe: a preview first, nothing destructive without `--apply`
  and a typed `yes`, and nothing deleted that exists only on this machine unless explicitly
  overridden.

## 3. What it is NOT {#M.TIX-§3}
- NOT a hosted tracker or a server. The store is one SQLite file in this repo (`data/Tickets.db`),
  committed with a readable dump (`data/Tickets.sql`); there is no second copy to fall out of sync.
- NOT an agent and NOT an LLM client. No code path calls an LLM SDK or API.
- NOT the Launcher. MindAttic.Launcher launches agent tabs and orchestrates; it calls this library
  as one front door ([HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6)). This repo owns
  tickets and workspace jobs.
- NOT a git client for commits. `/commit` commits; it calls `tickets ticket` to log the work.
- NOT a free-form document editor. Stories (`CODE-US-…`), epics and backlog items are edited through the CLI
  (`story new`/`edit`/`status`, `epic new`, `backlog add`), which keeps the database and each view in step.

## 4. Architecture canon {#M.TIX-§4}
```
   tickets CLI (src/MindAttic.Tickets.Cli, Spectre.Console.Cli)      Launcher menu, /commit
                 |                                                         |
                 +-------------------- MindAttic.Tickets (library) -------+
                                   |                       |
                       Data/ (SQLite, TicketsDb)    Repos/ (git + GitHub)
                       Tickets/, Stories/ model       RepoStatus, GitHubRepoIndex,
                       types read/written by it       DependencyScan, PrunePlanner
                                   |                       |
                  data/Tickets.db, data/Tickets.sql,  git -C <repo>, api.github.com
                  <repo>/docs/USER_STORIES.md (generated view, `tickets db render`)
```
### 4.1 Projects
- `src/MindAttic.Tickets` — the library (net10.0), packed to `C:\LocalNuGet`.
- `src/MindAttic.Tickets.Cli` — the `tickets` exe.
- `src/MindAttic.Tickets.Desktop` — Tickets Desktop, the WPF ticket app (net10.0-windows).
- `tests/MindAttic.Tickets.Tests` — NUnit 4.
- `tests/MindAttic.Tickets.Desktop.Tests` — NUnit 4: Tickets Desktop's view model and store.

### 4.2 Domain model (nouns)
- **Repo** — a git repo directly under the workspace root (`Workspace.cs`).
- **TicketsDb** — the database connection and schema: repos, epics, stories, backlog items,
  tickets, links and ticket history (`Data/TicketsDb.cs`).
- **Ticket** — one row: ID, Date, Type, Status, Summary, Ref (`Log/Ticket.cs`).
- **StoryDocument / StoryFile** — a `docs/USER_STORIES.md` parsed into ordered blocks, used only
  to import a file into the database and to render its generated view back out
  (`Stories/StoryDocument.cs`, `Log/StoryFile.cs`, the markdown-only fallback for a workspace
  that isn't migrated yet).
- **RepoStatus** — what a repo holds only locally (`Repos/RepoStatus.cs`).
- **GitHubRepoInfo** — archived flag and topics for a repo (`Repos/GitHubRepoIndex.cs`).
- **PruneCandidate / PruneBlocker** — a repo that no longer belongs, and why it can't simply go
  (`Repos/PrunePlanner.cs`).

### 4.3 Key services (verbs)
- `TicketsDb.Add` / `Close` / `CorrectType` — append a ticket, move its status, or relabel a
  nonstandard type (one of the two sanctioned exceptions to append-only, recorded in `ticket_events`).
- `TicketsDb.RenameRepo` / `RepoRenames` — rename a repo's folder-name key everywhere it's used
  (repos, blocks, epics, stories, backlog_items, tickets.repo), backing up the database first; the
  other sanctioned exception. The old name is kept forever in `repo_renames`, and each moved ticket
  gets a 'repo' `ticket_events` row, so the repo and every ticket trace back to their original name;
  a stale `--repo <old name>` keeps resolving through that history.
- `TicketsDb.AddStory` / `EditStory` / `SetStoryStatus`, `AddEpic`, `AddBacklogItem` — edit a
  repo's stories, epics and backlog directly in the database.
- `TicketsDb.AddLink` / `RemoveLink` / `Requires` — links between any two tickets or stories
  (`requires` is a prerequisite; a `requires` cycle is refused).
- `TicketsDb.Render` / `StaleViews` / `Export` — rewrite a repo's generated `docs/USER_STORIES.md`
  view from the database, or report which views have fallen out of date.
- `TicketsDb.Import` / `Check` / `Dump` — migrate a repo's file in (only used once per repo, by
  `db migrate`), validate the whole database, and write the readable `data/Tickets.sql` dump.
- `TicketRules.Validate` / `CheckAppendOnly` — the same rules, still used by the markdown fallback
  and by `Check` on the database.
- `WorkspaceTickets.All` / `Check` / `DeriveCode` — cross-repo listing, checks, project codes
  (reads the database once migrated, a repo's file otherwise).
- `RepoStatus.Read` — uncommitted, unpushed, stashes, local-only ignored files.
- `GitHubRepoIndex.FetchAsync` / `WithRenamesAsync` — one sweep of `/user/repos`, plus redirects
  for renamed repos whose local `origin` predates the rename.
- `DependencyScan.Scan` — repos reaching into a sibling folder by relative path.
- `PrunePlanner.Plan` / `DeleteFolder` — candidates, blockers, safe deletion.

## 5. The Laws {#M.TIX-§5}
This project **inherits** the org-wide laws in
[`MindAttic.HouseRules.md`](../../MindAttic.HouseRules.md) by reference. The directly
load-bearing ones: [HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1) (whole-number
versions), [HOUSE-LAW-3](../../MindAttic.HouseRules.md#HOUSE-LAW-3) (the GitHub token comes from
Vault), [HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6) (one engine; CLI, Launcher and
`/commit` are front doors), [HOUSE-LAW-8](../../MindAttic.HouseRules.md#HOUSE-LAW-8) (every ✅
story cites a test).

### {#M.TIX-LAW-1} The ticket database is the only store.
Every repo's stories, epics, backlog and tickets, and the links between them, live in
`data/Tickets.db` and nowhere else (amended 2026-10-15 by M.TIX-T20; markdown was the store before). A
repo's `docs/USER_STORIES.md` is a generated, read-only view (`tickets db render`); `db check` flags a hand edit.

### {#M.TIX-LAW-2} Ticket rows are append-only.
A row is never deleted or reworded. The database enforces it with triggers: a ticket can't be deleted, and only
its status and ref can be updated; every change is recorded in `ticket_events`. Only Status (open → closed / wont-do) and Ref change,
with three sanctioned, logged exceptions that lift the trigger for one update: `CorrectType` (a nonstandard type),
`RenameRepo` (its `repo` key, when the repo's folder is renamed) and ID reassignment (`ticket reassign`, or
`repos recode` for a whole repo's code; amended 2026-10-20 by M.TIX-T121). A reassigned ID keeps its history: the old
ID is kept forever in the append-only `id_history` table and as an `id` entry in the ticket's own history, and every
lookup by it (`ticket show`, links, the commit-msg hook) still finds the ticket. IDs are
the highest existing number + 1 and are never reused, including former IDs. `ticket verify` fails if a committed row was
deleted or reworded.

### {#M.TIX-LAW-3} Nothing is lost.
Each repo's file was imported as ordered blocks as well as structured rows, and the migration only
cut a repo over after its file rebuilt from the database byte-for-byte (line endings and BOM included).

### {#M.TIX-LAW-4} Destructive jobs preview by default and never lose what only exists here.
`prune` and `rollout` change nothing without `--apply`; `prune --apply` also needs a typed `yes`.
A folder with uncommitted, unpushed or stashed work or local-only ignored files is blocked unless
named in `--override`. A repo with no GitHub copy, or one a kept repo depends on by path, can never
be overridden.

### {#M.TIX-LAW-5} No LLM.
No code path links an LLM SDK or calls an LLM API.

## 6. Verified state {#M.TIX-§6}
- `dotnet build MindAttic.Tickets.slnx`: 0 warnings, 0 errors.
- `dotnet test`: 147 passing NUnit tests (Tickets Desktop's tickets page and `--ticket` opening and handoff, database import/export/links/history, the ticket queue, repo rename,
  story/epic/backlog editing, the CLI commands through the real `tickets` on a temp workspace,
  ticket parsing and writing on the markdown fallback, rules, workspace checks, code derivation,
  git status on real temp repos, GitHub index with a fake handler, dependency scan, prune planning).
- First real run (2026-10-12): `ticket list` read 35 tickets across the workspace, `codes check`
  found the duplicate `GG` code, and `repos prune` blocked MindAttic.Telemetry because
  GridGameGaiden loads it from its folder, resolved MindAttic.Cryptography's pre-rename origin,
  then deleted 20 folders with the user's confirmation.
- Database cutover (2026-10-15, M.TIX-T20): 46 repos migrated, 1046 stories, 561 tickets, 831 links,
  every file rebuilt byte-for-byte before the cutover.

## 7. Active frontier {#M.TIX-§7}
See the Priority backlog in [USER_STORIES.md](USER_STORIES.md).

## 8. Quality bar {#M.TIX-§8}
A feature is done when it has a focused NUnit test, the solution builds with zero warnings, and
any command that writes or deletes has a preview path. Anything touching other repos is tried on
temp directories in tests before it runs on the real workspace.

## 9. Glossary {#M.TIX-§9}
- **Workspace root** — `D:\Projects\MindAttic`, the parent of every repo (`--root` overrides).
- **Ticket log** — a repo's tickets in the database, shown as the `## Tickets` table at the end of
  its generated `docs/USER_STORIES.md` view.
- **Project code** — the `code:` front-matter value that prefixes ticket IDs (`M.LNCH-T4`).
- **Local-only work** — anything a folder delete would lose: uncommitted, unpushed or stashed work,
  or ignored files that no build regenerates.
- **Candidate** — a repo whose GitHub copy is archived or lacks the `work-in-progress` topic.
- **Blocker** — a reason a candidate can't be deleted (local work, git error, no GitHub copy, a
  kept dependent).
