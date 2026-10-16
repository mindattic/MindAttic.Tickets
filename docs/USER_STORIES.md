---
codex: 1
project: MindAttic.Workflow
code: M.FLOW
layer: stories
status: living
updated: 2026-10-12
---
<!-- GENERATED from MindAttic.Workflow/data/workflow.db by `workflow db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the workflow CLI. -->

# MindAttic.Workflow — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.
> Personas: **Dev** (the developer running the workspace), **Agent** (an AI agent working in a repo).

## Epic A — Ticket log
- **M.FLOW-US-A1 ✅** As an Agent, `workflow ticket new` appends a ticket with the next free ID and
  changes nothing else in the file, so logging work never disturbs the stories. *(verified by
  `Add_takes_the_number_after_the_highest_existing_id_never_filling_a_gap`,
  `Add_inserts_only_one_line_and_leaves_the_rest_of_the_file_byte_identical`,
  `Save_preserves_crlf_line_endings_and_the_bom`,
  `Add_creates_the_tickets_section_at_the_end_when_missing`,
  `Add_refuses_when_the_tickets_section_is_not_last`, `Add_refuses_a_file_without_a_code`.)*
- **M.FLOW-US-A2 ✅** As an Agent, `workflow ticket close` moves a ticket to closed or wont-do and only
  touches its Status and Ref. *(verified by `Close_changes_only_status_and_ref_of_that_row`,
  `Close_keeps_the_existing_ref_when_none_is_given_and_supports_wont_do`,
  `Close_throws_for_an_unknown_id`.)*
- **M.FLOW-US-A3 ✅** As a Dev, `workflow ticket verify` fails when a committed row was deleted or
  reworded, or the table breaks the rules. *(verified by
  `CheckAppendOnly_rejects_a_deleted_or_reworded_row`,
  `CheckAppendOnly_allows_status_and_ref_changes_and_new_rows`,
  `Validate_flags_bad_ids_duplicates_wrong_prefix_vocabulary_and_dates`,
  `Validate_accepts_a_well_formed_table`.)*
- **M.FLOW-US-A4 ✅** As a Dev, `workflow ticket list` shows tickets across every repo, filterable by
  repo, status, type and date. *(verified by `All_lists_tickets_across_repos`,
  `Parses_code_and_rows_and_unescapes_pipes`.)*
- **M.FLOW-US-A5 ✅** As a Dev, `workflow codes check` reports duplicate project codes, repos without a
  ticket log, and malformed tables. *(verified by
  `Check_reports_duplicate_codes_missing_files_and_bad_tables`.)*

## Epic B — Workspace jobs
- **M.FLOW-US-B1 ✅** As a Dev, `workflow repos status` shows each repo's local-only work and its GitHub
  state, resolving origins that predate a repo rename. *(verified by
  `Read_counts_uncommitted_unpushed_stashes_and_local_only_ignored_files`,
  `Read_of_a_clean_pushed_repo_has_no_local_only_work`,
  `IsRegenerable_separates_build_output_from_local_data`,
  `GitHubSlugFrom_handles_https_ssh_and_non_github`,
  `FetchAsync_paginates_and_records_archived_and_topics_by_lowercased_slug`,
  `WithRenamesAsync_resolves_a_stale_origin_through_githubs_redirect_and_skips_true_404s`.)*
- **M.FLOW-US-B2 ✅** As a Dev, `workflow repos prune` previews archived or untagged repos and deletes
  only what's safe, so nothing that exists only on this machine is lost by accident. *(verified by
  `Plan_keeps_tagged_repos_and_marks_clean_archived_or_untagged_ones_safe`,
  `Plan_blocks_on_local_only_work_missing_github_copy_and_kept_dependents`,
  `Plan_does_not_block_on_a_dependent_that_is_itself_being_pruned`,
  `Scan_finds_unity_manifest_project_reference_and_slnx_paths_into_sibling_repos`.)*
- **M.FLOW-US-B3 ✅** As a Dev, `workflow repos rollout tickets` gives every repo a USER_STORIES.md with
  a ticket log and a unique project code. *(verified by `DeriveCode_follows_the_codex_rule`,
  `DeriveCode_avoids_codes_already_taken`,
  `New_file_text_has_front_matter_and_an_empty_ticket_table_that_accepts_a_first_ticket`,
  `Repos_lists_only_git_folders_sorted_and_Containing_finds_the_enclosing_repo`.)*

## Priority backlog
- **M.FLOW-US-C1 🟡** As an Agent, `/commit` logs each commit through `workflow ticket` instead of
  hand-editing the table. Wired into the global `/commit` and the per-repo commit skills; it's an
  instruction, so no automated test proves an agent follows it.
- **M.FLOW-US-C2 🟡** As a Dev, the Launcher has a Workflow menu item showing open tickets and repo
  status through this library (MindAttic.Launcher M.LNCH-US-E6). The library calls are tested here;
  the menu itself has no test yet.
- **M.FLOW-US-C3 ⬜** As a Dev, `workflow codes fix` renames a duplicate project code and its ticket
  IDs, so a clash `codes check` reports can be resolved without hand edits.


## Epic D — The database: safety, concurrency and git
- **M.FLOW-US-D1 ⬜** As a Dev, `workflow db restore` rebuilds data/workflow.db from the committed data/workflow.sql dump and proves it matches (row counts per table, `db check` clean, every view renders unchanged), so a corrupt or merge-conflicted binary is never data loss (M.FLOW-LAW-3). Today nothing reads the dump back.
- **M.FLOW-US-D2 ⬜** As an Agent, my feature commits never carry data/workflow.db or data/workflow.sql; only `workflow db sync` commits them, and .gitattributes marks the db binary with a merge that keeps the working copy, so merging a worktree branch can't conflict on the database or roll it back to an older snapshot. Today ordinary commits sweep it in whenever it's modified.
- **M.FLOW-US-D3 ⬜** As an Agent running alongside other autopilot agents, `workflow ticket new` from many processes at once hands out distinct IDs with none lost and no 'database is locked' failure, proved by a test that starts parallel writers against one database (and decides whether WAL beats today's journal_mode=DELETE with a 15s busy_timeout).


## Epic E — CLI output and coverage
- **M.FLOW-US-E1 ⬜** As an Agent, workflow output piped or redirected (not a terminal) has no ANSI escape codes and honors NO_COLOR, so scripts and agents read plain text. Today `ticket list | cat` prints raw color codes around '0 ticket(s)', and help text keeps its underline codes even with NO_COLOR.
- **M.FLOW-US-E2 ⬜** As a Dev, `workflow story list` is readable in an 80-column terminal: one line per story with ID, glyph, epic and the text cut to fit, instead of today's 20-character Story column that wraps each story over seven lines. `--json` is unchanged.
- **M.FLOW-US-E3 ⬜** As a Dev, `workflow ticket list --search <text>` matches ID, summary and ref (case-insensitive) across the workspace, so finding a ticket doesn't mean piping --json through grep.
- **M.FLOW-US-E4 ⬜** As a Dev, the commands agents run that have no CLI test yet are tested through the real `workflow` on a temp workspace: story new/edit/status, epic new, backlog add, ticket link (a refused cycle), ticket correct, db render, and db sync --no-push committing only the database files.


## Epic F — Viewer and canon
- **M.FLOW-US-F1 ⬜** As a Dev, `workflow view` shows each ticket's and story's links (requires, blocks) and a ticket's history, and an ID in a summary or ref links to that item, so I can see from the page why something is blocked.
- **M.FLOW-US-F2 ⬜** As a Dev, the Launcher's Workflow menu (M.LNCH-US-E6) has an 'Open viewer' item that runs `workflow view`, scoped to the repo I'm in.
- **M.FLOW-US-F3 ⬜** As an Agent reading the canon, README.md and docs/BIBLE.md describe the database, not the markdown era: the intro and architecture diagrams say tickets live in SQLite, `ticket new` no longer claims 'only that one line changes', 'stories are still written by hand' goes, the glossary's 'Ticket log' stops pointing at the ## Tickets table, M.FLOW-LAW-2 stops repeating its trigger sentence, and the codex doctor passes.

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| M.FLOW-T1 | 2026-10-12 | feature | closed | Bootstrap MindAttic.Workflow: ticket engine (new/close/list/verify, codes check) and workspace jobs (status, prune, rollout) | M.FLOW-US-A1..A5, M.FLOW-US-B1..B3 |
| M.FLOW-T2 | 2026-10-12 | fix | closed | Fake GitHub handler matched 'page=1' inside 'per_page=100', so a pagination test looped forever | `FetchAsync_paginates_and_records_archived_and_topics_by_lowercased_slug` |
| M.FLOW-T3 | 2026-10-12 | fix | closed | Dependency scan skipped Unity's Packages folder (case-insensitive 'packages' skip), missing GridGameGaiden → MindAttic.Telemetry | `Scan_finds_unity_manifest_project_reference_and_slnx_paths_into_sibling_repos` |
| M.FLOW-T4 | 2026-10-12 | fix | closed | A renamed repo's stale origin made it look missing from GitHub (MindAttic.Cryptography); resolve through GitHub's redirect | `WithRenamesAsync_resolves_a_stale_origin_through_githubs_redirect_and_skips_true_404s` |
| M.FLOW-T5 | 2026-10-12 | feature | closed | prune --override for blocked repos whose only blockers are local work; never for no-GitHub-copy or kept dependents | `Plan_blocks_on_local_only_work_missing_github_copy_and_kept_dependents` |
| M.FLOW-T6 | 2026-10-12 | chore | closed | Pruned 20 archived/untagged folders from the workspace (12 clean + 8 overridden by the owner) | M.FLOW-US-B2 |
| M.FLOW-T7 | 2026-10-12 | feature | closed | /commit logs tickets through `workflow ticket` | ~/.claude/commands/commit.md step 0; per-repo commit skills |
| M.FLOW-T8 | 2026-10-12 | feature | closed | Launcher Workflow menu item | MindAttic.Launcher M.LNCH-US-E6 |
| M.FLOW-T9 | 2026-10-13 | chore | closed | Console-menu spikes (spikes/): Baseline vs FlickerFree vs SpectreLive vs RazorConsole vs XenoAtom vs TerminalGui, to pick the Launcher's next menu renderer | Picked SpectreLive; ported to MindAttic.Launcher Ui/Menu.cs |
| M.FLOW-T10 | 2026-10-13 | feature | closed | WebView2 viewer for tickets/stories: generalize MindAttic.Browser beyond capture so it can host the Workflow UI | workflow view (docs/VIEWER.md): TicketCliTests.View_writes_a_page_and_a_snapshot_of_every_ticket_and_story_with_markup_escaped; real export renders 294 open tickets; MindAttic.Browser needed no change |
| M.FLOW-T11 | 2026-10-13 | docs | closed | Plan the WebView2 viewer for Workflow: how MindAttic.Browser hosts the ticket/story UI (static export vs live view, generalizing Browser beyond capture) | docs/VIEWER.md: static export (index.html + data.js) opened in MindAttic.Browser at https://workflow.local/; the Browser already serves folders, no change needed |
| M.FLOW-T12 | 2026-10-14 | chore | closed | Set a professional public GitHub description (350 chars max, no first person) on every active mindattic repo, written from what each app actually does; archived repos are read-only and skipped | gh repo edit: 15 set (13 blank, MindAttic.Workflow's stale markdown wording, SyntheticExchangeTheory's subtitle-only); all 46 active repos now have one, each <=350 chars, no first person; archived repos skipped |
| M.FLOW-T13 | 2026-10-14 | feature | closed | /commit from the General (workspace root) session commits only the code that session wrote, in every repo it touched, never other agents' pending edits in those repos | ~/.claude/commands/commit.md: 'Commit only what this session wrote' (per-repo commits from the General session via git -C, stage by path, own hunks only via git apply --cached, name what was left); not versioned in any repo |
| M.FLOW-T14 | 2026-10-14 | chore | closed | Common slash commands (/do, /commit, /quicksave, /quickload, /deploy, /bughunt, /throttle, /unthrottle) live only at the user level (~/.claude/commands); delete every repo's duplicate copy, keeping only commands specific to that repo | Verification sweep (2026-10-15): grep for do/commit/quicksave/quickload/deploy/bughunt/throttle/unthrottle across every repo's .claude/commands found none, except 10 repos' genuinely repo-specific deploy.md (Cursory, IdiotProof, MindAttic.Ideas, MindAttic.Web x3, TaxRateCollector, ThinkTank, Tutor, Prose) and Prose's pointer-style quicksave.md/quickload.md. 83 duplicate files removed across 32 repo commits, e.g. MindAttic.Launcher@696fb90, MindAttic.Vault@8141809 |
| M.FLOW-T15 | 2026-10-14 | chore | closed | NUnit tests for the ticket CLI commands (src/MindAttic.Workflow.Cli/Commands/TicketCommands.cs) run through a Spectre CommandApp against a temp --root workspace: new rejects a bad --type/--status or blank --summary and prints the new ID, errors when USER_STORIES.md is missing; close --wont-do; list filters by --repo/--status/--type/--since and emits the documented --json shape; verify returns 1 for a row deleted/reworded vs git HEAD and 0 for an uncommitted new file - verify is the append-only guard every agent relies on | TicketCliTests (5): new/close/list/verify through the real workflow on a temp --root, markdown and database paths |
| M.FLOW-T16 | 2026-10-14 | chore | closed | NUnit tests for the repos CLI commands and Git runner (RepoCommands.cs, Git.cs) on a temp workspace: rollout tickets previews without writing and with --apply seeds USER_STORIES.md using a DeriveCode that avoids codes already taken, never overwriting an existing file; prune without --apply deletes nothing, --only/--override comma lists are trimmed and case-insensitive and --override never frees a no-GitHub-copy repo; Git.Run returns exit -1 with a 'timed out' error on timeout and never prompts (GIT_TERMINAL_PROMPT=0) | TicketCliTests: rollout preview/apply (unused code, never overwrites), rollout into the database once migrated, Git.Run never prompts and kills a hung git at its timeout; prune split to M.FLOW-T28 |
| M.FLOW-T17 | 2026-10-14 | chore | closed | Spike: should tickets move from each repo's docs/USER_STORIES.md table to a SQLite database owned by Workflow (where it lives, history and diffs in git, what the CLI, orchestrator and agents would read, migration path); write up a recommendation, no build | Decided: generated SQLite cache in this repo (workflow ticket index), markdown stays the only store; built as M.FLOW-T18, not a full migration |
| M.FLOW-T18 | 2026-10-15 | feature | closed | Build the generated SQLite ticket index (workflow ticket index): rebuilt from every repo's USER_STORIES.md, markdown stays canonical, db is gitignored | TicketIndexTests (TicketIndexTests.cs); dotnet test tests/MindAttic.Workflow.Tests -c Release: 52 passed; workflow ticket index: 550 tickets across 46 repos, cross-checked against 'ticket list --json' count |
| M.FLOW-T19 | 2026-10-15 | feature | wont-do | Make the SQLite database THE ticket store, replacing the M.FLOW-T18 index: data/tickets.db committed to this repo (with a readable text dump beside it), every ticket command (new/close/list/verify, codes, rollout) reads and writes it with append-only enforced in the db, all 46 repos' ## Tickets tables migrated with IDs, dates, status and refs intact (row counts verified per repo) and replaced by a one-line pointer, BIBLE M.FLOW-LAW-1 amended, package bumped for Orchestrator and Launcher | wrong scope (kept stories in markdown); superseded by M.FLOW-T20 |
| M.FLOW-T20 | 2026-10-15 | feature | closed | One SQLite database (data/workflow.db, committed to this repo with a readable dump) holds ALL of every repo's USER_STORIES.md in a structured schema (repos and front-matter, epics, stories with status, priority, acceptance criteria and notes, backlog, tickets, and ordered sections for any free-form prose); every repo's file is migrated with nothing lost (verified per repo), the workflow CLI reads and writes the db, and the orchestrator reads only the db; replaces the M.FLOW-T18 index | WorkflowDbTests.The_real_workspace_migrates_with_every_file_rebuilt_byte_for_byte; migrate: 46 repos, 1046 stories, 561 tickets, 831 links, all rebuild byte-for-byte |
| M.FLOW-T21 | 2026-10-15 | feature | closed | codex.ps1 digest/doctor read story glyphs from the database (workflow story list --json) instead of USER_STORIES.md, which is now a pointer, so each repo's Status index isn't empty; one shared copy instead of 20 drifting ones | the generated view gives codex.ps1 the full stories again: 19/20 doctors pass on it unchanged (ChiMesh's deploy.md failure predates the migration) |
| M.FLOW-T22 | 2026-10-15 | feature | closed | Story editing in the database: workflow story new/edit (text, epic, acceptance, evidence) and epic and backlog commands, so stories can be added and reworded without a markdown file | WorkflowDbTests.Stories_epics_and_backlog_items_are_added_and_edited_in_the_database_and_the_view_follows |
| M.FLOW-T23 | 2026-10-15 | chore | wont-do | Prose, ThinkTank and IdiotProof's own commit commands still say to stage docs/USER_STORIES.md; point them at the workflow database like ~/.claude/commands/commit.md (or drop them per M.FLOW-T14) | no change needed: with the generated view (M.FLOW-T27) the CLI rewrites docs/USER_STORIES.md after each ticket change, so 'stage it with the rest' is right again |
| M.FLOW-T24 | 2026-10-15 | chore | wont-do | Delete every repo's docs/USER_STORIES.md pointer: first move what still keys off the file onto the database (GitHub intake's repo filter, repos rollout, codes check / CodeOf, codex-standard docs and agent instructions), then remove the 46 files with one commit per repo | user chose a generated view instead (84 links into the file); see M.FLOW-T27 |
| M.FLOW-T25 | 2026-10-15 | fix | closed | Normalize the 11 tickets with non-standard types (GridGameGaiden bugfix x7, idea, decision; SyntheticExchangeTheory design; UnitySolarSystem bug) to fix/feature/chore/docs through a recorded correction (history event), since the append-only triggers forbid a plain update | WorkflowDbTests.A_nonstandard_type_is_corrected_with_a_reason_in_its_history_and_append_only_holds_after; 11 corrected, db check: no problems |
| M.FLOW-T26 | 2026-10-15 | chore | wont-do | MindAttic.Bob is archived on GitHub, so its USER_STORIES.md pointer commit is local only: unarchive, push and re-archive, or drop the local commit (and the file, with the deletion ticket) | Bob is archived and retiring (M.LNCH-T23): its archived GitHub copy keeps the original stories, and its two local view commits are left for M.LNCH-T23 to keep or drop |
| M.FLOW-T27 | 2026-10-15 | feature | closed | Each repo's USER_STORIES.md is a generated, read-only view of the database (front-matter, a do-not-edit marker, then the full stories and tickets), rewritten after every CLI change and on each sync; db check flags a hand edit, and a view is never imported back, so the 84 links into it, the codex doctors and GitHub keep working | WorkflowDbTests.Each_repo_gets_a_generated_view_that_follows_the_database_and_a_hand_edit_is_flagged; 19/20 codex doctors pass (ChiMesh failed before) |
| M.FLOW-T28 | 2026-10-15 | chore | closed | NUnit tests for repos prune (split from M.FLOW-T16): without --apply nothing is deleted, --only/--override lists are trimmed and case-insensitive, and --override never frees a repo with no GitHub copy; needs a fake GitHub index so it runs offline | TicketCliTests: Prune_previews_without_deleting_and_only_and_override_lists_are_trimmed_and_case_insensitive, Prune_apply_deletes_nothing_without_a_typed_yes, Prune_override_never_frees_a_repo_with_no_github_copy; offline via MINDATTIC_WORKFLOW_GITHUB_INDEX; 72 passed |
| M.FLOW-T29 | 2026-10-15 | docs | closed | Write ten improvement stories in three new epics (D database safety/concurrency/git, E CLI output and coverage, F viewer and canon), each from a gap observed in the code or output | M.FLOW-US-D1..D3, M.FLOW-US-E1..E4, M.FLOW-US-F1..F3 |
| M.FLOW-T30 | 2026-10-15 | chore | closed | Bring MindAttic.Workflow fully up to date and ready for everything the orchestrator and hooks rely on: hunt and fix bugs, add missing tests, update the README, and confirm the tickets table (id, repo, status, ordinal), ticket new/close/correct and db render/sync still match what the orchestrator uses (requested 2026-10-15) | 78 passing NUnit tests incl. TicketCliTests (Correct/Link/Story_epic_and_backlog/Render/Sync); commit 55930e4 on main |
| M.FLOW-T31 | 2026-10-15 | chore | open | NUnit tests for WorkflowDb.Open / OpenIfMigrated / DefaultPath (src/MindAttic.Workflow/Data/WorkflowDb.cs): DefaultPath is <root>/MindAttic.Workflow/data/workflow.db for a given and the default root, OpenIfMigrated returns null with no database file and a usable db once migrated, Open creates the schema on a fresh file and reopening keeps every row | — |
| M.FLOW-T32 | 2026-10-15 | chore | open | NUnit contract tests for the tickets schema the orchestrator's Python hooks query directly (context-gate.py, pace-gate.py): table tickets has columns id, repo, status, ordinal; SELECT ... WHERE repo = ? COLLATE NOCASE AND status = 'open' ORDER BY ordinal returns a repo's open tickets in filing order; id lookup COLLATE NOCASE finds M.ORCH-T1 from mao-t1; journal_mode is DELETE and busy_timeout is 15000 on every connection (a schema change must fail these first) | — |
| M.FLOW-T33 | 2026-10-15 | chore | open | NUnit concurrency tests for WorkflowDb.Add (BEGIN IMMEDIATE): two connections (and two processes) filing in the same repo at once get distinct consecutive ids with no lost or duplicated row, a filer blocked by a held write lock waits within busy_timeout instead of failing, and parallel filing into two repos keeps each repo's numbering independent | — |
| M.FLOW-T34 | 2026-10-15 | chore | open | NUnit tests for WorkflowDb.Ticket(id) and Tickets(repo): lookup is case-insensitive on id and repo name, an unknown id returns null, Tickets() with no repo returns every repo's rows, Tickets(repo) for an unknown repo returns empty, and rows come back in ordinal (filing) order | — |
| M.FLOW-T35 | 2026-10-15 | chore | open | NUnit tests for WorkflowDb.Close and CorrectType error paths: an unknown id throws with the id in the message, an invalid status is refused, closing an already closed ticket keeps its first ref unless a new one is given, wont-do via Close is recorded in History, and CorrectType on an unknown id or to a non-standard type throws | — |
| M.FLOW-T36 | 2026-10-15 | chore | open | NUnit tests for WorkflowDb.AddRepo / Export / IsPointer: AddRepo registers a repo with its code (duplicate code or name refused), Export rebuilds that repo's USER_STORIES.md bytes from the database, Export of an unknown repo is null, and IsPointer recognises a generated view and rejects hand-written markdown | — |
| M.FLOW-T37 | 2026-10-15 | chore | open | NUnit tests for WorkflowDb links beyond the cycle case: RemoveLink returns false for a link that doesn't exist and true once, Requires(transitive: true) on a diamond lists each prerequisite once, a link kind other than requires never appears in Requires, and Links(id) shows both directions | — |
| M.FLOW-T38 | 2026-10-15 | chore | open | NUnit tests for TicketRules.ParseNumber and Today (src/MindAttic.Workflow/Tickets/TicketRules.cs): ParseNumber reads M.ORCH-T12 as 12 and returns null for MAO-12, MAO-Tx, empty and a lower-case t; Today is yyyy-MM-dd under a non-English current culture | — |
| M.FLOW-T39 | 2026-10-15 | chore | open | NUnit tests for StoryDocument.StatusOf / GlyphOf (src/MindAttic.Workflow/Stories/StoryDocument.cs): each status maps to its glyph and back (round trip for done, partial, planned), an unknown glyph or null gives null, and an unknown status has a defined fallback | — |
| M.FLOW-T40 | 2026-10-15 | chore | open | NUnit tests for Workspace.IsRepo / Find / Containing (src/MindAttic.Workflow/Workspace.cs): IsRepo is true for a .git folder and for a .git file (a worktree) and false otherwise, Find is case-insensitive and null for an unknown name, Containing resolves a nested folder and returns null for the workspace root and for a path outside it | — |
| M.FLOW-T41 | 2026-10-15 | chore | open | NUnit tests for WorkspaceTickets.CodeOf (src/MindAttic.Workflow/Tickets/WorkspaceTickets.cs): reads the code from a repo's USER_STORIES.md front matter, null for a repo with no file or no code, and agrees with the code the database has for that repo after migration | — |
| M.FLOW-T42 | 2026-10-15 | chore | open | CLI tests for repo resolution (src/MindAttic.Workflow.Cli/Commands/Common.cs ResolveRepo): --repo wins over the current folder, a run from a nested folder or a worktree of a repo resolves that repo, an unknown --repo exits non-zero with a clear message, and a run from the workspace root without --repo is refused | — |
| M.FLOW-T43 | 2026-10-15 | chore | open | CLI tests for ticket close options and exit codes: --wont-do records wont-do, a missing or unknown id exits non-zero, and an unknown option such as --status wont-do is rejected instead of silently ignored (found live: 'ticket close X --status wont-do' closed X as closed); turn on strict parsing if needed | — |
| M.FLOW-T44 | 2026-10-15 | chore | open | CLI tests for ticket list filters: --type and --since narrow the rows, an unparseable --since date exits non-zero, --status wont-do lists only wont-do rows, and --json output with no matches is an empty array | — |
| M.FLOW-T45 | 2026-10-15 | chore | open | CLI tests for ticket show and ticket correct: show prints the row, its history and links (prerequisites and dependents), --json is valid JSON with those fields, an unknown id exits non-zero; correct without --reason or with a non-standard --type exits non-zero and changes nothing | — |
| M.FLOW-T46 | 2026-10-15 | chore | open | CLI tests for story status/show/edit and epic/backlog error paths: an unknown story id exits non-zero, an invalid status is refused, epic new with a duplicate key is refused, backlog add with story ids links them, and each command leaves the generated view matching the database | — |
| M.FLOW-T47 | 2026-10-15 | chore | open | CLI tests for db migrate / export / check: migrate --db into a scratch path imports every repo and a second run is a no-op, --cutover rewrites views to generated pointers, export --out writes the same bytes as WorkflowDb.Export, and check exits non-zero on a dangling link or a bad id and zero on a clean database | — |
| M.FLOW-T48 | 2026-10-15 | chore | open | CLI tests for db sync --no-push and db render --repo: sync with --no-push commits only data/ (the database and its dump) and never pushes, sync with nothing changed is a no-op with exit 0, a failing git step exits non-zero, and render --repo rewrites only that repo's view | — |
| M.FLOW-T49 | 2026-10-15 | chore | open | CLI tests for repos status (src/MindAttic.Workflow.Cli/Commands/RepoCommands.cs): --json lists every repo with local-only work and GitHub state, a repo with no remote is reported rather than failing, and a GitHub lookup failure degrades to local-only data with a warning instead of a crash | — |
| M.FLOW-T50 | 2026-10-15 | chore | open | Spike (investigate only, change nothing): could GitHub Issues be the source of truth for tickets, with Workflow as the sync pipeline and local SQLite mirror the hooks and orchestrator keep reading? Answer in a written report: issue title/label scheme keeping existing IDs ([ID] summary; type, status:proposed/approved, priority labels); gh API cost and rate limits for sync, filing and closing at the orchestrator's pace; offline queueing; repos without a GitHub origin; requires links (body text vs GitHub issue relationships); one-time import of every existing ticket and its rate-limit plan; approving from the phone via the approved label replacing orchestrator approve; what workflow ticket new/close/list/show and db render/sync become; risks, an effort estimate, and a go/no-go recommendation. No code or data changes. | — |
| M.FLOW-T51 | 2026-10-16 | fix | open | workflow ticket close silently ignores options it doesn't know: 'ticket close M.ORCH-T120 --status wont-do' printed 'closed' and recorded closed, not wont-do; reject unknown options with a non-zero exit and say which (and accept --status wont-do as --wont-do), across every ticket subcommand | — |
| M.FLOW-T52 | 2026-10-16 | docs | closed | Agent standard (workspace root repo): every commit names its workflow ticket, created first if missing, and holds one ticket/story only, no cross-contamination | codex-standard repo: mindattic-agent-standard/AGENTS.md |
| M.FLOW-T53 | 2026-10-16 | chore | closed | Rename ticket codes to family prefixes: MindAttic.* repos to M.<4 letters> (M.AUTH, M.ORCH, M.FLOW...), Unity* to U.EVE/U.RON/U.SPC, GTFO.* to G.AWPN/G.CTD/G.EZ/G.SD; every ticket, story, epic, law, amendment and section ID renamed in the database, views and docs; codes and IDs may now carry a one-letter family and a dot; GTFO.Weapons registered under its folder name GTFO.AbsurdWeapons; GTFO.CrashTestDummies and GTFO.SkipDrop added; MindAttic.Bob (archived) keeps MAB | TicketRulesTests.A_family_code_with_a_dot_numbers_validates_and_parses, StoryFileTests.Parses_a_family_code_with_a_dot |
| M.FLOW-T54 | 2026-10-16 | chore | closed | commit-msg hook in every workspace repo refuses a commit whose subject doesn't end with an existing workflow ticket ID (shared/hooks/commit-msg + install-commit-hooks.ps1, run by install.ps1) | codex-standard repo; git hook run commit-msg verified in all 50 repos |
