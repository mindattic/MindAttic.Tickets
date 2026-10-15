---
codex: 1
project: MindAttic.Workflow
code: WF
layer: stories
status: living
updated: 2026-10-12
---
<!-- GENERATED from MindAttic.Workflow/data/workflow.db by `workflow db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the workflow CLI. -->

# MindAttic.Workflow — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.
> Personas: **Dev** (the developer running the workspace), **Agent** (an AI agent working in a repo).

## Epic A — Ticket log
- **WF-US-A1 ✅** As an Agent, `workflow ticket new` appends a ticket with the next free ID and
  changes nothing else in the file, so logging work never disturbs the stories. *(verified by
  `Add_takes_the_number_after_the_highest_existing_id_never_filling_a_gap`,
  `Add_inserts_only_one_line_and_leaves_the_rest_of_the_file_byte_identical`,
  `Save_preserves_crlf_line_endings_and_the_bom`,
  `Add_creates_the_tickets_section_at_the_end_when_missing`,
  `Add_refuses_when_the_tickets_section_is_not_last`, `Add_refuses_a_file_without_a_code`.)*
- **WF-US-A2 ✅** As an Agent, `workflow ticket close` moves a ticket to closed or wont-do and only
  touches its Status and Ref. *(verified by `Close_changes_only_status_and_ref_of_that_row`,
  `Close_keeps_the_existing_ref_when_none_is_given_and_supports_wont_do`,
  `Close_throws_for_an_unknown_id`.)*
- **WF-US-A3 ✅** As a Dev, `workflow ticket verify` fails when a committed row was deleted or
  reworded, or the table breaks the rules. *(verified by
  `CheckAppendOnly_rejects_a_deleted_or_reworded_row`,
  `CheckAppendOnly_allows_status_and_ref_changes_and_new_rows`,
  `Validate_flags_bad_ids_duplicates_wrong_prefix_vocabulary_and_dates`,
  `Validate_accepts_a_well_formed_table`.)*
- **WF-US-A4 ✅** As a Dev, `workflow ticket list` shows tickets across every repo, filterable by
  repo, status, type and date. *(verified by `All_lists_tickets_across_repos`,
  `Parses_code_and_rows_and_unescapes_pipes`.)*
- **WF-US-A5 ✅** As a Dev, `workflow codes check` reports duplicate project codes, repos without a
  ticket log, and malformed tables. *(verified by
  `Check_reports_duplicate_codes_missing_files_and_bad_tables`.)*

## Epic B — Workspace jobs
- **WF-US-B1 ✅** As a Dev, `workflow repos status` shows each repo's local-only work and its GitHub
  state, resolving origins that predate a repo rename. *(verified by
  `Read_counts_uncommitted_unpushed_stashes_and_local_only_ignored_files`,
  `Read_of_a_clean_pushed_repo_has_no_local_only_work`,
  `IsRegenerable_separates_build_output_from_local_data`,
  `GitHubSlugFrom_handles_https_ssh_and_non_github`,
  `FetchAsync_paginates_and_records_archived_and_topics_by_lowercased_slug`,
  `WithRenamesAsync_resolves_a_stale_origin_through_githubs_redirect_and_skips_true_404s`.)*
- **WF-US-B2 ✅** As a Dev, `workflow repos prune` previews archived or untagged repos and deletes
  only what's safe, so nothing that exists only on this machine is lost by accident. *(verified by
  `Plan_keeps_tagged_repos_and_marks_clean_archived_or_untagged_ones_safe`,
  `Plan_blocks_on_local_only_work_missing_github_copy_and_kept_dependents`,
  `Plan_does_not_block_on_a_dependent_that_is_itself_being_pruned`,
  `Scan_finds_unity_manifest_project_reference_and_slnx_paths_into_sibling_repos`.)*
- **WF-US-B3 ✅** As a Dev, `workflow repos rollout tickets` gives every repo a USER_STORIES.md with
  a ticket log and a unique project code. *(verified by `DeriveCode_follows_the_codex_rule`,
  `DeriveCode_avoids_codes_already_taken`,
  `New_file_text_has_front_matter_and_an_empty_ticket_table_that_accepts_a_first_ticket`,
  `Repos_lists_only_git_folders_sorted_and_Containing_finds_the_enclosing_repo`.)*

## Priority backlog
- **WF-US-C1 🟡** As an Agent, `/commit` logs each commit through `workflow ticket` instead of
  hand-editing the table. Wired into the global `/commit` and the per-repo commit skills; it's an
  instruction, so no automated test proves an agent follows it.
- **WF-US-C2 🟡** As a Dev, the Launcher has a Workflow menu item showing open tickets and repo
  status through this library (MindAttic.Launcher MCO-US-E6). The library calls are tested here;
  the menu itself has no test yet.
- **WF-US-C3 ⬜** As a Dev, `workflow codes fix` renames a duplicate project code and its ticket
  IDs, so a clash `codes check` reports can be resolved without hand edits.

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| WF-T1 | 2026-10-12 | feature | closed | Bootstrap MindAttic.Workflow: ticket engine (new/close/list/verify, codes check) and workspace jobs (status, prune, rollout) | WF-US-A1..A5, WF-US-B1..B3 |
| WF-T2 | 2026-10-12 | fix | closed | Fake GitHub handler matched 'page=1' inside 'per_page=100', so a pagination test looped forever | `FetchAsync_paginates_and_records_archived_and_topics_by_lowercased_slug` |
| WF-T3 | 2026-10-12 | fix | closed | Dependency scan skipped Unity's Packages folder (case-insensitive 'packages' skip), missing GridGameGaiden → MindAttic.Telemetry | `Scan_finds_unity_manifest_project_reference_and_slnx_paths_into_sibling_repos` |
| WF-T4 | 2026-10-12 | fix | closed | A renamed repo's stale origin made it look missing from GitHub (MindAttic.Cryptography); resolve through GitHub's redirect | `WithRenamesAsync_resolves_a_stale_origin_through_githubs_redirect_and_skips_true_404s` |
| WF-T5 | 2026-10-12 | feature | closed | prune --override for blocked repos whose only blockers are local work; never for no-GitHub-copy or kept dependents | `Plan_blocks_on_local_only_work_missing_github_copy_and_kept_dependents` |
| WF-T6 | 2026-10-12 | chore | closed | Pruned 20 archived/untagged folders from the workspace (12 clean + 8 overridden by the owner) | WF-US-B2 |
| WF-T7 | 2026-10-12 | feature | closed | /commit logs tickets through `workflow ticket` | ~/.claude/commands/commit.md step 0; per-repo commit skills |
| WF-T8 | 2026-10-12 | feature | closed | Launcher Workflow menu item | MindAttic.Launcher MCO-US-E6 |
| WF-T9 | 2026-10-13 | chore | closed | Console-menu spikes (spikes/): Baseline vs FlickerFree vs SpectreLive vs RazorConsole vs XenoAtom vs TerminalGui, to pick the Launcher's next menu renderer | Picked SpectreLive; ported to MindAttic.Launcher Ui/Menu.cs |
| WF-T10 | 2026-10-13 | feature | open | WebView2 viewer for tickets/stories: generalize MindAttic.Browser beyond capture so it can host the Workflow UI | — |
| WF-T11 | 2026-10-13 | docs | closed | Plan the WebView2 viewer for Workflow: how MindAttic.Browser hosts the ticket/story UI (static export vs live view, generalizing Browser beyond capture) | docs/VIEWER.md: static export (index.html + data.js) opened in MindAttic.Browser at https://workflow.local/; the Browser already serves folders, no change needed |
| WF-T12 | 2026-10-14 | chore | open | Set a professional public GitHub description (350 chars max, no first person) on every active mindattic repo, written from what each app actually does; archived repos are read-only and skipped | — |
| WF-T13 | 2026-10-14 | feature | open | /commit from the General (workspace root) session commits only the code that session wrote, in every repo it touched, never other agents' pending edits in those repos | — |
| WF-T14 | 2026-10-14 | chore | closed | Common slash commands (/do, /commit, /quicksave, /quickload, /deploy, /bughunt, /throttle, /unthrottle) live only at the user level (~/.claude/commands); delete every repo's duplicate copy, keeping only commands specific to that repo | Verification sweep (2026-10-15): grep for do/commit/quicksave/quickload/deploy/bughunt/throttle/unthrottle across every repo's .claude/commands found none, except 10 repos' genuinely repo-specific deploy.md (Cursory, IdiotProof, MindAttic.Ideas, MindAttic.Web x3, TaxRateCollector, ThinkTank, Tutor, Prose) and Prose's pointer-style quicksave.md/quickload.md. 83 duplicate files removed across 32 repo commits, e.g. MindAttic.Launcher@696fb90, MindAttic.Vault@8141809 |
| WF-T15 | 2026-10-14 | chore | closed | NUnit tests for the ticket CLI commands (src/MindAttic.Workflow.Cli/Commands/TicketCommands.cs) run through a Spectre CommandApp against a temp --root workspace: new rejects a bad --type/--status or blank --summary and prints the new ID, errors when USER_STORIES.md is missing; close --wont-do; list filters by --repo/--status/--type/--since and emits the documented --json shape; verify returns 1 for a row deleted/reworded vs git HEAD and 0 for an uncommitted new file - verify is the append-only guard every agent relies on | TicketCliTests (5): new/close/list/verify through the real workflow on a temp --root, markdown and database paths |
| WF-T16 | 2026-10-14 | chore | closed | NUnit tests for the repos CLI commands and Git runner (RepoCommands.cs, Git.cs) on a temp workspace: rollout tickets previews without writing and with --apply seeds USER_STORIES.md using a DeriveCode that avoids codes already taken, never overwriting an existing file; prune without --apply deletes nothing, --only/--override comma lists are trimmed and case-insensitive and --override never frees a no-GitHub-copy repo; Git.Run returns exit -1 with a 'timed out' error on timeout and never prompts (GIT_TERMINAL_PROMPT=0) | TicketCliTests: rollout preview/apply (unused code, never overwrites), rollout into the database once migrated, Git.Run never prompts and kills a hung git at its timeout; prune split to WF-T28 |
| WF-T17 | 2026-10-14 | chore | closed | Spike: should tickets move from each repo's docs/USER_STORIES.md table to a SQLite database owned by Workflow (where it lives, history and diffs in git, what the CLI, orchestrator and agents would read, migration path); write up a recommendation, no build | Decided: generated SQLite cache in this repo (workflow ticket index), markdown stays the only store; built as WF-T18, not a full migration |
| WF-T18 | 2026-10-15 | feature | closed | Build the generated SQLite ticket index (workflow ticket index): rebuilt from every repo's USER_STORIES.md, markdown stays canonical, db is gitignored | TicketIndexTests (TicketIndexTests.cs); dotnet test tests/MindAttic.Workflow.Tests -c Release: 52 passed; workflow ticket index: 550 tickets across 46 repos, cross-checked against 'ticket list --json' count |
| WF-T19 | 2026-10-15 | feature | wont-do | Make the SQLite database THE ticket store, replacing the WF-T18 index: data/tickets.db committed to this repo (with a readable text dump beside it), every ticket command (new/close/list/verify, codes, rollout) reads and writes it with append-only enforced in the db, all 46 repos' ## Tickets tables migrated with IDs, dates, status and refs intact (row counts verified per repo) and replaced by a one-line pointer, BIBLE WF-LAW-1 amended, package bumped for Orchestrator and Launcher | wrong scope (kept stories in markdown); superseded by WF-T20 |
| WF-T20 | 2026-10-15 | feature | closed | One SQLite database (data/workflow.db, committed to this repo with a readable dump) holds ALL of every repo's USER_STORIES.md in a structured schema (repos and front-matter, epics, stories with status, priority, acceptance criteria and notes, backlog, tickets, and ordered sections for any free-form prose); every repo's file is migrated with nothing lost (verified per repo), the workflow CLI reads and writes the db, and the orchestrator reads only the db; replaces the WF-T18 index | WorkflowDbTests.The_real_workspace_migrates_with_every_file_rebuilt_byte_for_byte; migrate: 46 repos, 1046 stories, 561 tickets, 831 links, all rebuild byte-for-byte |
| WF-T21 | 2026-10-15 | feature | closed | codex.ps1 digest/doctor read story glyphs from the database (workflow story list --json) instead of USER_STORIES.md, which is now a pointer, so each repo's Status index isn't empty; one shared copy instead of 20 drifting ones | the generated view gives codex.ps1 the full stories again: 19/20 doctors pass on it unchanged (ChiMesh's deploy.md failure predates the migration) |
| WF-T22 | 2026-10-15 | feature | closed | Story editing in the database: workflow story new/edit (text, epic, acceptance, evidence) and epic and backlog commands, so stories can be added and reworded without a markdown file | WorkflowDbTests.Stories_epics_and_backlog_items_are_added_and_edited_in_the_database_and_the_view_follows |
| WF-T23 | 2026-10-15 | chore | wont-do | Prose, ThinkTank and IdiotProof's own commit commands still say to stage docs/USER_STORIES.md; point them at the workflow database like ~/.claude/commands/commit.md (or drop them per WF-T14) | no change needed: with the generated view (WF-T27) the CLI rewrites docs/USER_STORIES.md after each ticket change, so 'stage it with the rest' is right again |
| WF-T24 | 2026-10-15 | chore | wont-do | Delete every repo's docs/USER_STORIES.md pointer: first move what still keys off the file onto the database (GitHub intake's repo filter, repos rollout, codes check / CodeOf, codex-standard docs and agent instructions), then remove the 46 files with one commit per repo | user chose a generated view instead (84 links into the file); see WF-T27 |
| WF-T25 | 2026-10-15 | fix | closed | Normalize the 11 tickets with non-standard types (GridGameGaiden bugfix x7, idea, decision; SyntheticExchangeTheory design; UnitySolarSystem bug) to fix/feature/chore/docs through a recorded correction (history event), since the append-only triggers forbid a plain update | WorkflowDbTests.A_nonstandard_type_is_corrected_with_a_reason_in_its_history_and_append_only_holds_after; 11 corrected, db check: no problems |
| WF-T26 | 2026-10-15 | chore | wont-do | MindAttic.Bob is archived on GitHub, so its USER_STORIES.md pointer commit is local only: unarchive, push and re-archive, or drop the local commit (and the file, with the deletion ticket) | Bob is archived and retiring (MCO-T23): its archived GitHub copy keeps the original stories, and its two local view commits are left for MCO-T23 to keep or drop |
| WF-T27 | 2026-10-15 | feature | closed | Each repo's USER_STORIES.md is a generated, read-only view of the database (front-matter, a do-not-edit marker, then the full stories and tickets), rewritten after every CLI change and on each sync; db check flags a hand edit, and a view is never imported back, so the 84 links into it, the codex doctors and GitHub keep working | WorkflowDbTests.Each_repo_gets_a_generated_view_that_follows_the_database_and_a_hand_edit_is_flagged; 19/20 codex doctors pass (ChiMesh failed before) |
| WF-T28 | 2026-10-15 | chore | closed | NUnit tests for repos prune (split from WF-T16): without --apply nothing is deleted, --only/--override lists are trimmed and case-insensitive, and --override never frees a repo with no GitHub copy; needs a fake GitHub index so it runs offline | TicketCliTests: Prune_previews_without_deleting_and_only_and_override_lists_are_trimmed_and_case_insensitive, Prune_apply_deletes_nothing_without_a_typed_yes, Prune_override_never_frees_a_repo_with_no_github_copy; offline via MINDATTIC_WORKFLOW_GITHUB_INDEX; 72 passed |
