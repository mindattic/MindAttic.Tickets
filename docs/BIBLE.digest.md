AUTHORITATIVE - full detail in docs/BIBLE.md
<!-- generatedFrom: M.FLOW-bible -->

# MindAttic.Workflow (WF) - Bible digest

## The one sentence (#M.FLOW-§1)
MindAttic.Workflow is the workspace's ticket engine and repo job runner: a .NET library plus a
`workflow` CLI that keeps every repo's stories and permanent ticket log in one SQLite database
(`data/workflow.db`) and runs workspace-wide jobs (status, prune, rollout) safely across every
repo under `D:\Projects\MindAttic`.

## What it is NOT (#M.FLOW-§3)
- NOT a hosted tracker or a server. The store is one SQLite file in this repo (`data/workflow.db`),
  committed with a readable dump (`data/workflow.sql`); there is no second copy to fall out of sync.
- NOT an agent and NOT an LLM client. No code path calls an LLM SDK or API.
- NOT the Launcher. MindAttic.Launcher launches agent tabs and orchestrates; it calls this library
  as one front door ([HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6)). This repo owns
  tickets and workspace jobs.
- NOT a git client for commits. `/commit` commits; it calls `workflow ticket` to log the work.
- NOT a free-form document editor. Stories (`CODE-US-…`), epics and backlog items are edited through the CLI\n  (`story new`/`edit`/`status`, `epic new`, `backlog add`), which keeps the database and each view in step.

## The Laws (#M.FLOW-§5)
This project **inherits** the org-wide laws in
[`MindAttic.HouseRules.md`](../../MindAttic.HouseRules.md) by reference. The directly
load-bearing ones: [HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1) (whole-number
versions), [HOUSE-LAW-3](../../MindAttic.HouseRules.md#HOUSE-LAW-3) (the GitHub token comes from
Vault), [HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6) (one engine; CLI, Launcher and
`/commit` are front doors), [HOUSE-LAW-8](../../MindAttic.HouseRules.md#HOUSE-LAW-8) (every ✅
story cites a test).

### {#M.FLOW-LAW-1} The workflow database is the only store.
Every repo's stories, epics, backlog and tickets, and the links between them, live in
`data/workflow.db` and nowhere else (amended 2026-10-15 by M.FLOW-T20; markdown was the store before). A
repo's `docs/USER_STORIES.md` is a generated, read-only view (`workflow db render`); `db check` flags a hand edit.

### {#M.FLOW-LAW-2} Ticket rows are append-only.
A row is never deleted or reworded. The database enforces it with triggers: a ticket can't be deleted, and only
its status and ref can be updated; every change is recorded in `ticket_events`. Only Status (open → closed / wont-do) and Ref change. IDs are
the highest existing number + 1 and are never reused. `ticket verify` fails if a committed row was
deleted or reworded.

### {#M.FLOW-LAW-3} Nothing is lost.
Each repo's file was imported as ordered blocks as well as structured rows, and the migration only
cut a repo over after its file rebuilt from the database byte-for-byte (line endings and BOM included).

### {#M.FLOW-LAW-4} Destructive jobs preview by default and never lose what only exists here.
`prune` and `rollout` change nothing without `--apply`; `prune --apply` also needs a typed `yes`.
A folder with uncommitted, unpushed or stashed work or local-only ignored files is blocked unless
named in `--override`. A repo with no GitHub copy, or one a kept repo depends on by path, can never
be overridden.

### {#M.FLOW-LAW-5} No LLM.
No code path links an LLM SDK or calls an LLM API.

## Glossary (#M.FLOW-§9)
- **Workspace root** — `D:\Projects\MindAttic`, the parent of every repo (`--root` overrides).
- **Ticket log** — a repo's tickets in the database, shown as the `## Tickets` table at the end of
  its generated `docs/USER_STORIES.md` view.
- **Project code** — the `code:` front-matter value that prefixes ticket IDs (`M.LNCH-T4`).
- **Local-only work** — anything a folder delete would lose: uncommitted, unpushed or stashed work,
  or ignored files that no build regenerates.
- **Candidate** — a repo whose GitHub copy is archived or lacks the `work-in-progress` topic.
- **Blocker** — a reason a candidate can't be deleted (local work, git error, no GitHub copy, a
  kept dependent).

## Status index (docs/USER_STORIES.md)
- done: 10
- partial: 3
- planned: 12

