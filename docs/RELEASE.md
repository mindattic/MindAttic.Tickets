---
layer: release
status: living
---
# MindAttic.Tickets — Release charter

> The one finish line this repo is working toward. Every open ticket in this repo sits under one of the epics
> below, and every epic is part of the milestone. Work that does not fit is out of scope until this milestone is
> reached (owner, 2026-11-27, MAT-T319).

## 1. What this is
The workspace's ticket engine: a .NET library, the `tickets` CLI and the Tickets Desktop WPF app that hold every
repo's tickets, links, milestones and history in one store (local SQL Server, with the committed `data/Tickets.db`
copy) and run workspace-wide repo jobs.

## 2. The milestone: MindAttic.Tickets 7.0, the planning tree {#milestone}
- **Milestone:** MS-15, "MindAttic.Tickets 7.0: the planning tree"
- **Published means:** an internal tool. Tickets Desktop 7.0.0 in `R:\MindAttic\Apps\MindAttic.Tickets\Debug` and
  `\Release`, and the `tickets` CLI 7.0.0 reinstalled from it.
- **The natural stopping point:** the ticket tree (milestone > epic > feature > story > task, built from part-of
  links and level labels) is the only planning model. The legacy stories/epics/backlog tables are archived and
  dropped. Tickets Desktop shows that tree, refreshes by delta instead of reloading 5,500+ tickets on every change,
  and takes its dialogs and toasts from MindAttic.Components.Wpf. With that, the tool matches how the portfolio is
  now planned, and nothing else is needed to run it.

## 3. Done when (acceptance checks)
Each line is also a `tickets milestone check` on MS-15.
1. build — `MindAttic.Tickets.slnx` builds clean (0 warnings) on main.
2. exists — the 7.0.0 Tickets Desktop exe is deployed at
   `R:\MindAttic\Apps\MindAttic.Tickets\Release\MindAttic.Tickets.Desktop.exe`.
3. signoff — the owner opens Tickets Desktop, sees MS-15's tree with rollups, closes a task and sees only that row
   refresh; the legacy story tables are gone, with their archive in `data/`.

## 4. Epics, in priority order
| Priority | Epic (ticket) | What it delivers | Features |
|---|---|---|---|
| 900 | M.TIX-T390 Retire legacy planning tables | The ticket tree becomes the only planning model | M.TIX-T394 Freeze legacy writes (990), M.TIX-T395 Archive and drop tables (980) |
| 800 | M.TIX-T391 Desktop planning tree | Milestone > epic > feature > task in Tickets Desktop, with rollups and execution order | M.TIX-T396 Milestone tree view (890), M.TIX-T397 Execution order (880) |
| 700 | M.TIX-T392 Desktop refresh and controls | Delta refresh, and shared Components.Wpf dialogs and toasts | M.TIX-T235 Incremental refresh (790), M.TIX-T398 Shared Components controls (780) |
| 600 | M.TIX-T393 Release 7.0 and hygiene | Orchestrator claim integration, a bughunt, docs that match the store, and the deploy | M.TIX-T399 Workspace integration (690), M.TIX-T400 Hardening and docs (680), M.TIX-T401 Ship 7.0 (670) |

Tasks: T402 (freeze writes); T403 archive, then T404 drop tables; T405 tree view, then T188 epic filter; T186,
T195; T369, T370, T371; T181, then T176-T180; T337; T381, T406; T385, T407 (bump and deploy 7.0.0).

The portfolio-reset tooling tickets M.TIX-T387, T388 and T389 (MAT-T321) are being implemented separately and are
not part of this tree.

## 5. Out of scope (not before this milestone)
- The rest of the 50-item Desktop UI/UX polish umbrella (M.TIX-T175): palette extras, swimlanes, WIP limits,
  spacing and contrast audits, shortcut legends. Audit-generated polish.
- Story renumbering and duplicate-story checks (M.TIX-T170): stories are being retired.
- Client-machine writes to the host's store (M.TIX-T323): remote use is RDP onto the host.
- Repos tab extras such as stash counts and GitHub topic badges (M.TIX-T376).
- A Blazor or web shell for Tickets Desktop.

## 6. Current verified state
- The CLI covers tickets, links, history, levels (`ticket level`, `new --parent/--level`), `split`, parent
  roll-up close, `--unplanned`/`--containers` filters and milestones with scope lock and checks (M.TIX-T335,
  T347, T362-T366).
- Tickets Desktop has a Milestones view (Ctrl+9, M.TIX-T336), a Graph view, a command palette and a Stats view.
- The SQL Server store with change-tracked file copy (M.TIX-T342, T344) and the paused-serverless retry
  (M.TIX-T374, 6.0.1).
- run.ps1 uses the shared deploy-lib run menu; Debug and Release are deployed to `R:\MindAttic\Apps\MindAttic.Tickets`
  (e41bbec, 2026-11-26).
- `tickets ticket verify --repo MindAttic.Tickets`: OK.

## 7. Decision record
- 2026-11-27: charter drafted from README, BIBLE, git log and the open tickets (M.TIX-T386). Legion vote:
  **Approve**, 9 of 9 voters, plurality quorum reached, consensus 1.0; no dissent.
- Triage: 61 open before (including the job ticket and three MAT-T321 tooling tickets left alone) → done 2
  (T269, T384), duplicate 0, wont-do 39 (T170 and its 2 children, T175 and its 33 polish children, T323, T376), kept
  15 existing tasks plus T235 recast as a feature, and 6 new tasks (T402-T407) under 4 epics and 8 new features.
