AUTHORITATIVE - full detail in docs/BIBLE.md
<!-- generatedFrom: M.TIX-bible -->

# MindAttic.Tickets (M.TIX) - Bible digest

## The one sentence (#M.TIX-§1)
MindAttic.Tickets is the workspace's ticket engine and repo job runner: a .NET library plus a
`tickets` CLI that keeps every repo's milestones and permanent ticket log — each ticket placed in
a tree by level (`epic` → `feature` → `story` → `task`) and `part-of` links — in one database: a
local SQL Server instance when configured (`tickets db server migrate`, M.TIX-T131), with
`data/Tickets.db` kept a complete, git-committed copy; SQLite alone (`data/Tickets.db` is then the
store) otherwise. It also runs workspace-wide jobs (status, prune, rollout) safely across every
repo under `D:\Projects\MindAttic`.

## What it is NOT (#M.TIX-§3)
- NOT a hosted tracker. The store of record is either `data/Tickets.db` (SQLite, the default) or a
  local SQL Server database beside it (`tickets db server migrate`, M.TIX-T131); either way
  `data/Tickets.db` is committed to this repo with a readable dump (`data/Tickets.sql`), and on SQL
  Server it's kept a complete copy, brought up to date after every write, so there's never a copy
  that needs reconciling by hand.
- NOT an agent and NOT an LLM client. No code path calls an LLM SDK or API.
- NOT the Launcher. MindAttic.Launcher launches agent tabs and orchestrates; it calls this library
  as one front door ([HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6)). This repo owns
  tickets and workspace jobs.
- NOT a git client for commits. `/commit` commits; it calls `tickets ticket` to log the work.
- NOT a free-form document editor. A ticket's place in the plan is its level (`ticket level <ID>
  epic|feature|story|task`) and its `part-of` links (`ticket new --parent`, `ticket split`);
  milestones group ticket trees toward a goal (`milestone new`/`add`). Edited only through the
  CLI, which keeps the database and each generated view in step.

## The Laws (#M.TIX-§5)
This project **inherits** the org-wide laws in
[`MindAttic.HouseRules.md`](../../MindAttic.HouseRules.md) by reference. The directly
load-bearing ones: [HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1) (whole-number
versions), [HOUSE-LAW-3](../../MindAttic.HouseRules.md#HOUSE-LAW-3) (the GitHub token comes from
Vault), [HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6) (one engine; CLI, Launcher and
`/commit` are front doors), [HOUSE-LAW-8](../../MindAttic.HouseRules.md#HOUSE-LAW-8) (every ✅
story cites a test).

### {#M.TIX-LAW-1} The ticket database is the only store.
Every repo's milestones, tickets (placed in a tree by level and `part-of` links) and the links
between them live in the ticket database and nowhere else (amended 2026-10-15 by M.TIX-T20;
markdown was the store before). The database is `data/Tickets.db` (SQLite) by default, or a local
SQL Server database once migrated (`tickets db server migrate`; amended 2026-10-22 by M.TIX-T131),
with `data/Tickets.db` then kept a complete copy, brought up to date after every write. A
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

## Glossary (#M.TIX-§9)
- **Workspace root** — `D:\Projects\MindAttic`, the parent of every repo (`--root` overrides).
- **Ticket log** — a repo's tickets in the database, shown as the `## Tickets` table at the end of
  its generated `docs/USER_STORIES.md` view.
- **Level** — where a ticket sits in the planning tree: `epic`, `feature`, `story` or `task`, kept
  as its one `level:` label (`ticket level`).
- **Milestone** — a checkable point on the way to a goal across one or more repos, with its own
  `MS-n` ID (never a ticket ID); tickets and epics join one with a `part-of` link.
- **Project code** — the `code:` front-matter value that prefixes ticket IDs (`M.LNCH-T4`).
- **Local-only work** — anything a folder delete would lose: uncommitted, unpushed or stashed work,
  or ignored files that no build regenerates.
- **Candidate** — a repo whose GitHub copy is archived or lacks the `work-in-progress` topic.
- **Blocker** — a reason a candidate can't be deleted (local work, git error, no GitHub copy, a
  kept dependent).

## Status index (docs/USER_STORIES.md)
- done: 11
- partial: 3
- planned: 37

