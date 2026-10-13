AUTHORITATIVE - full detail in docs/BIBLE.md
<!-- generatedFrom: WF-bible -->

# MindAttic.Workflow (WF) - Bible digest

## The one sentence (#WF-§1)
MindAttic.Workflow is the workspace's ticket engine and repo job runner: a .NET library plus a
`workflow` CLI that keeps every repo's permanent ticket log (the `## Tickets` table in
`docs/USER_STORIES.md`) and runs workspace-wide jobs (status, prune, rollout) safely across every
repo under `D:\Projects\MindAttic`.

## What it is NOT (#WF-§3)
- NOT a database or a hosted tracker. The markdown files are the store; the engine only parses and
  writes them. There is no index file, cache or server to fall out of sync.
- NOT an agent and NOT an LLM client. No code path calls an LLM SDK or API.
- NOT the Launcher. MindAttic.Launcher launches agent tabs and orchestrates; it calls this library
  as one front door ([HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6)). This repo owns
  tickets and workspace jobs.
- NOT a git client for commits. `/commit` commits; it calls `workflow ticket` to log the work.
- NOT a story editor. Stories (`CODE-US-…`) stay hand-written; the engine only manages the
  `## Tickets` section and creates a skeleton file when a repo has none.

## The Laws (#WF-§5)
This project **inherits** the org-wide laws in
[`MindAttic.HouseRules.md`](../../MindAttic.HouseRules.md) by reference. The directly
load-bearing ones: [HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1) (whole-number
versions), [HOUSE-LAW-3](../../MindAttic.HouseRules.md#HOUSE-LAW-3) (the GitHub token comes from
Vault), [HOUSE-LAW-6](../../MindAttic.HouseRules.md#HOUSE-LAW-6) (one engine; CLI, Launcher and
`/commit` are front doors), [HOUSE-LAW-8](../../MindAttic.HouseRules.md#HOUSE-LAW-8) (every ✅
story cites a test).

### {#WF-LAW-1} Markdown in each repo is the only ticket store.
Tickets live in that repo's `docs/USER_STORIES.md` and nowhere else. No database, cache or
central index; every query re-reads the files.

### {#WF-LAW-2} Ticket rows are append-only.
A row is never deleted or reworded. Only Status (open → closed / wont-do) and Ref change. IDs are
the highest existing number + 1 and are never reused. `ticket verify` fails if a committed row was
deleted or reworded.

### {#WF-LAW-3} Edits are surgical.
Adding or closing a ticket changes exactly one line; the rest of the file, its line endings and its
BOM are preserved byte-for-byte. `## Tickets` stays the last section, and the engine refuses to
append when it isn't.

### {#WF-LAW-4} Destructive jobs preview by default and never lose what only exists here.
`prune` and `rollout` change nothing without `--apply`; `prune --apply` also needs a typed `yes`.
A folder with uncommitted, unpushed or stashed work or local-only ignored files is blocked unless
named in `--override`. A repo with no GitHub copy, or one a kept repo depends on by path, can never
be overridden.

### {#WF-LAW-5} No LLM.
No code path links an LLM SDK or calls an LLM API.

## Glossary (#WF-§9)
- **Workspace root** — `D:\Projects\MindAttic`, the parent of every repo (`--root` overrides).
- **Ticket log** — the `## Tickets` table at the end of a repo's `docs/USER_STORIES.md`.
- **Project code** — the `code:` front-matter value that prefixes ticket IDs (`MCO-T4`).
- **Local-only work** — anything a folder delete would lose: uncommitted, unpushed or stashed work,
  or ignored files that no build regenerates.
- **Candidate** — a repo whose GitHub copy is archived or lacks the `work-in-progress` topic.
- **Blocker** — a reason a candidate can't be deleted (local work, git error, no GitHub copy, a
  kept dependent).

## Status index (docs/USER_STORIES.md)
- done: 10
- partial: 3
- planned: 2

