using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

public sealed class TicketsDbTests
{
    private string root = "";

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private Repo MakeRepo(string name, string code, string body, string tickets)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            $"---\ncodex: 1\nproject: {name}\ncode: {code}\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# {name} — User Stories\n\n{body}\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" + tickets);
        return new Repo(name, dir);
    }

    private (TicketsDb Db, List<Repo> Repos) Migrated()
    {
        var a = MakeRepo("Alpha", "AL",
            "## Epic A — Core\n- **AL-US-A1 ⬜** As a user, I can start. *(no test yet.)*\n- **AL-US-A2 ✅** Done thing. *(verified by `T`.)*\n\n## Priority backlog\n1. **AL-US-A1** next\n",
            "| AL-T1 | 2026-10-01 | feature | open | build AL-US-A1, blocked by BE-T1 | — |\n| AL-T2 | 2026-10-02 | fix | closed | fix it | AL-T1 |\n");
        var b = MakeRepo("Beta", "BE", "## Priority backlog\n_Nothing recorded yet._\n",
            "| BE-T1 | 2026-10-01 | chore | open | groundwork | superseded by BE-T2 |\n| BE-T2 | 2026-10-03 | chore | open | groundwork, done right | — |\n");
        var db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        db.Import([a, b], new Dictionary<string, IReadOnlyList<string>> { ["AL-T2"] = ["BE-T2"] });
        return (db, [a, b]);
    }

    [Test]
    public void Import_keeps_every_byte_and_structures_stories_tickets_and_links()
    {
        var a = MakeRepo("Alpha", "AL", "## Epic A — Core\n- **AL-US-A1 ⬜** Start.\n", "| AL-T1 | 2026-10-01 | feature | open | build AL-US-A1, blocked by BE-T1 | — |\n");
        var b = MakeRepo("Beta", "BE", "", "| BE-T1 | 2026-10-01 | chore | open | groundwork | superseded by BE-T2 |\n| BE-T2 | 2026-10-03 | chore | open | redo | — |\n");
        using var db = TicketsDb.Open(Path.Combine(root, "w.db"));

        var report = db.Import([a, b]);
        Assert.That(report.Select(r => (r.Repo, r.Stories, r.Tickets, r.RoundTrips)), Is.EqualTo(new[] { ("Alpha", 1, 1, true), ("Beta", 0, 2, true) }));
        Assert.That(db.Export("Alpha"), Is.EqualTo(File.ReadAllBytes(a.StoriesPath)));

        Assert.That(db.Stories("Alpha").Single(), Has.Property("Id").EqualTo("AL-US-A1").And.Property("Status").EqualTo("planned").And.Property("Epic").EqualTo("A"));
        var links = db.Links("AL-T1").Select(l => (l.From, l.Kind, l.To)).ToList();
        Assert.That(links, Does.Contain(("AL-T1", "requires", "BE-T1")), "a prerequisite, from 'blocked by'");
        Assert.That(links, Does.Contain(("AL-T1", "implements", "AL-US-A1")));
        Assert.That(db.Links("BE-T1").Select(l => (l.From, l.Kind, l.To)), Does.Contain(("BE-T2", "supersedes", "BE-T1")));
    }

    [Test]
    public void Reimporting_one_repo_drops_a_stale_supersedes_link_whose_from_id_is_in_another_repo()
    {
        // 'supersedes' is derived with from = the superseding ticket (can live in a different repo) and
        // to = the ticket whose Ref was parsed, so cleaning it up on a reimport must also key off to_id's repo.
        var a = MakeRepo("Alpha", "AL", "", "| AL-T9 | 2026-10-01 | chore | open | newer | — |\n");
        var b = MakeRepo("Beta", "BE", "", "| BE-T5 | 2026-10-01 | chore | open | groundwork | superseded by AL-T9 |\n");
        using var db = TicketsDb.Open(Path.Combine(root, "w.db"));
        db.Import([a, b]);
        Assert.That(db.Links("BE-T5").Select(l => (l.From, l.Kind, l.To)), Does.Contain(("AL-T9", "supersedes", "BE-T5")));

        // BE-T5's Ref no longer says it was superseded; reimporting only Beta must drop the now-stale link.
        var b2 = MakeRepo("Beta", "BE", "", "| BE-T5 | 2026-10-01 | chore | open | groundwork | — |\n");
        db.Import([b2]);

        Assert.That(db.Links("BE-T5").Select(l => (l.From, l.Kind, l.To)), Does.Not.Contain(("AL-T9", "supersedes", "BE-T5")));
    }

    [Test]
    public void A_requires_cycle_is_refused_whatever_case_the_ids_were_typed_in()
    {
        var (db, _) = Migrated(); // AL-T1 requires BE-T1 (from 'blocked by')
        using (db)
        {
            db.AddLink("be-t1", "AL-T2", "requires"); // typed in lower case: the chain is AL-T1 → BE-T1 → AL-T2
            Assert.That(db.Requires("AL-T1", transitive: true), Does.Contain("AL-T2"));
            Assert.That(() => db.AddLink("AL-T2", "AL-T1", "requires"), Throws.InvalidOperationException.With.Message.Contains("cycle"));
        }
    }

    [Test]
    public void Tickets_are_append_only_with_history_and_new_ones_take_the_next_number()
    {
        var (db, repos) = Migrated();
        using (db)
        {
            var t = db.Add("Alpha", "fix", "open", "a new one", "");
            Assert.That(t.Id, Is.EqualTo("AL-T3"));
            db.Close("AL-T1", reference: "the test");
            Assert.That(db.Ticket("al-t1")!.Value.Ticket, Has.Property("Status").EqualTo("closed").And.Property("Ref").EqualTo("the test"));
            Assert.That(db.History("AL-T1").Select(e => (e.Field, e.Old, e.New)), Is.EqualTo(new[] { ("status", (string?)"open", (string?)"closed"), ("ref", "—", "the test") }));
            Assert.That(db.History("AL-T3").Single().Field, Is.EqualTo("created"));

            var text = System.Text.Encoding.UTF8.GetString(db.Export("Alpha")!);
            Assert.That(text, Does.Contain("| AL-T1 | 2026-10-01 | feature | closed | build AL-US-A1, blocked by BE-T1 | the test |"));
            Assert.That(text, Does.EndWith("| AL-T3 | " + TicketRules.Today() + " | fix | open | a new one | — |\n"));

            using var raw = new SqliteConnection($"Data Source={db.Path};Pooling=False");
            raw.Open();
            foreach (var sql in new[] { "UPDATE tickets SET summary = 'reworded' WHERE id = 'AL-T1'", "DELETE FROM tickets WHERE id = 'AL-T1'" })
            {
                using var cmd = raw.CreateCommand();
                cmd.CommandText = sql;
                Assert.That(() => cmd.ExecuteNonQuery(), Throws.InstanceOf<SqliteException>(), sql);
            }
        }
    }

    [Test]
    public void Links_join_any_two_items_and_a_prerequisite_cycle_is_refused()
    {
        var (db, _) = Migrated();
        using (db)
        {
            Assert.That(db.Requires("AL-T2"), Is.EqualTo(new[] { "BE-T2" }), "from the orchestrator's block setting");
            db.AddLink("BE-T2", "AL-T1", "requires");
            Assert.That(db.Requires("BE-T2", transitive: true), Is.EquivalentTo(new[] { "AL-T1", "BE-T1" }));
            Assert.That(() => db.AddLink("BE-T1", "BE-T2", "requires"), Throws.InvalidOperationException.With.Message.Contains("cycle"));
            Assert.That(() => db.AddLink("AL-T1", "AL-T2", "blocks"), Throws.ArgumentException);
            db.AddLink("AL-T2", "AL-US-A2", "implements");
            Assert.That(db.Links("AL-US-A2").Select(l => l.From), Does.Contain("AL-T2"));
            Assert.That(db.RemoveLink("AL-T2", "AL-US-A2", "implements"), Is.True);
            Assert.That(db.Check(), Is.Empty);
        }
    }

    [Test]
    public void Check_flags_a_link_whose_endpoint_is_no_ticket_story_or_code_epic_key_epic()
    {
        var (db, _) = Migrated();
        using (db)
        {
            Assert.That(db.Links("AL-US-A1").Select(l => l.To), Does.Contain("AL-EPIC-A"), "an epic resolves by its repo's CODE-EPIC-KEY");
            Assert.That(db.Check(), Is.Empty);

            using (var raw = new SqliteConnection($"Data Source={db.Path};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "INSERT INTO links (from_id, to_id, kind) VALUES ('AL-US-A2', 'SS-EPIC-G', 'part-of'), ('G5a', 'AL-EPIC-Z', 'part-of'), ('AL-T1', 'H10a', 'related')";
                cmd.ExecuteNonQuery();
            }
            Assert.That(db.Check(), Is.EquivalentTo(new[]
            {
                "link AL-US-A2 part-of SS-EPIC-G: no item SS-EPIC-G",
                "link G5a part-of AL-EPIC-Z: no item G5a",
                "link G5a part-of AL-EPIC-Z: no item AL-EPIC-Z",
                "link AL-T1 related H10a: no item H10a",
            }));
        }
    }

    [Test]
    public void Check_fix_trims_stray_crs_from_links_into_the_tickets_history_and_moves_old_code_epic_links_to_the_current_code()
    {
        var (db, _) = Migrated();
        using (db)
        {
            using (var raw = new SqliteConnection($"Data Source={db.Path};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                // What a CRLF script feeding 'ticket link' left behind (M.TIX-T116): a blank target, a padded one, and a padded duplicate.
                cmd.CommandText = "INSERT INTO links (from_id, to_id, kind) VALUES ('AL-T1', char(13), 'requires'), ('AL-T2', 'BE-T1' || char(13), 'requires'), " +
                                  "('BE-T2', 'AL-T1', 'related'), ('BE-T2', 'AL-T1' || char(13), 'related'); " +
                                  // And a story's epic link under an old code (Prose's SS-EPIC-G with its code now PRS).
                                  "DELETE FROM links WHERE from_id = 'AL-US-A1' AND kind = 'part-of'; " +
                                  "INSERT INTO links (from_id, to_id, kind) VALUES ('AL-US-A1', 'SS-EPIC-A', 'part-of'), ('AL-US-A2', 'SS-EPIC-A', 'part-of')";
                cmd.ExecuteNonQuery();
            }
            Assert.That(db.Check(), Is.EquivalentTo(new[]
            {
                "link AL-T1 requires \\r: no item (blank; run 'tickets db check --fix')",
                "link AL-T2 requires BE-T1\\r: no item BE-T1 (stray whitespace; run 'tickets db check --fix')",
                "link BE-T2 related AL-T1\\r: no item AL-T1 (stray whitespace; run 'tickets db check --fix')",
                "link AL-US-A1 part-of SS-EPIC-A: no item SS-EPIC-A",
                "link AL-US-A2 part-of SS-EPIC-A: no item SS-EPIC-A",
            }));

            Assert.That(db.RepairLinks(), Is.EquivalentTo(new[]
            {
                "link AL-T1 requires \\r: removed (blank endpoint)",
                "link AL-T2 requires BE-T1\\r: trimmed to AL-T2 requires BE-T1",
                "link BE-T2 related AL-T1\\r: removed (the trimmed link already exists)",
                "link AL-US-A1 part-of SS-EPIC-A: retargeted to AL-EPIC-A (the repo's current code)",
                "link AL-US-A2 part-of SS-EPIC-A: removed (AL-US-A2 part-of AL-EPIC-A already exists)",
            }));
            Assert.That(db.Links("AL-US-A1").Where(l => l.Kind == "part-of").Select(l => l.To), Is.EqualTo(new[] { "AL-EPIC-A" }));
            Assert.That(db.Check(), Is.Empty);
            Assert.That(db.RepairLinks(), Is.Empty, "a second run has nothing to do");
            Assert.That(db.Requires("AL-T2", transitive: false), Does.Contain("BE-T1"));
            Assert.That(db.History("AL-T2").Select(e => (e.Field, e.Old, e.New)), Does.Contain(("link", (string?)"requires BE-T1\\r", (string?)"requires BE-T1")));

            db.AddLink("BE-T2", " BE-T1\r", "related");
            Assert.That(db.Check(), Is.Empty, "AddLink trims what it's given");
            Assert.That(() => db.AddLink("BE-T2", "\r", "related"), Throws.ArgumentException);
        }
    }

    [Test]
    public void A_story_status_change_updates_its_row_and_its_markdown()
    {
        var (db, _) = Migrated();
        using (db)
        {
            Assert.That(db.SetStoryStatus("AL-US-A1", "done"), Is.EqualTo(1));
            Assert.That(db.Story("AL-US-A1").Single().Status, Is.EqualTo("done"));
            Assert.That(System.Text.Encoding.UTF8.GetString(db.Export("Alpha")!), Does.Contain("- **AL-US-A1 ✅** As a user, I can start."));
            Assert.That(() => db.SetStoryStatus("AL-US-A1", "finished"), Throws.ArgumentException);
        }
    }

    [Test]
    public void Each_repo_gets_a_generated_view_that_follows_the_database_and_a_hand_edit_is_flagged()
    {
        var (db, repos) = Migrated();
        using (db)
        {
            var ws = new Workspace(root);
            Assert.That(db.Render(ws), Is.EquivalentTo(new[] { "Alpha", "Beta" }));
            var view = File.ReadAllText(repos[0].StoriesPath);
            Assert.That(view, Does.StartWith("---\ncodex: 1"), "front-matter stays first, for the doctors");
            Assert.That(view, Does.Contain("---\n" + TicketsDb.ViewMarker + "\n"));
            Assert.That(view, Does.Contain("- **AL-US-A1 ⬜**").And.Contain("| AL-T1 |"), "the full stories and tickets, so links into it resolve");
            Assert.That(db.Render(ws), Is.Empty, "unchanged: nothing rewritten");
            Assert.That(db.StaleViews(ws), Is.Empty);

            db.Close("AL-T1", reference: "done");
            Assert.That(db.StaleViews(ws), Is.EqualTo(new[] { "Alpha" }));
            db.Render(ws);
            Assert.That(File.ReadAllText(repos[0].StoriesPath), Does.Contain("| closed |"));

            File.AppendAllText(repos[1].StoriesPath, "a hand edit\n");
            Assert.That(db.StaleViews(ws), Is.EqualTo(new[] { "Beta" }));
            Assert.That(db.Import(repos).Select(r => r.Repo), Is.Empty, "a view is never imported back over the database");
        }
    }

    [Test]
    public void A_nonstandard_type_is_corrected_with_a_reason_in_its_history_and_append_only_holds_after()
    {
        var a = MakeRepo("Gamma", "GA", "", "| GA-T1 | 2026-10-01 | bugfix | open | odd type | — |\n");
        using var db = TicketsDb.Open(Path.Combine(root, "c.db"));
        db.Import([a]);
        Assert.That(db.Check(), Has.Some.Contains("unknown type 'bugfix'"));

        Assert.That(db.CorrectType("GA-T1", "fix", "bugfix predates the vocabulary").Type, Is.EqualTo("fix"));
        Assert.That(db.Check(), Is.Empty);
        Assert.That(db.History("GA-T1").Single(), Is.EqualTo(new TicketEvent("GA-T1", db.History("GA-T1")[0].At, "type", "bugfix", "fix (corrected: bugfix predates the vocabulary)")));
        Assert.That(System.Text.Encoding.UTF8.GetString(db.Export("Gamma")!), Does.Contain("| GA-T1 | 2026-10-01 | fix | open | odd type | — |"));
        Assert.That(() => db.CorrectType("GA-T1", "bugfix", "x"), Throws.ArgumentException);

        using var raw = new SqliteConnection($"Data Source={db.Path};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE tickets SET type = 'docs' WHERE id = 'GA-T1'";
        Assert.That(() => cmd.ExecuteNonQuery(), Throws.InstanceOf<SqliteException>(), "the trigger is back");
    }

    [Test]
    public void The_first_backlog_item_lands_in_the_backlog_section_not_after_the_one_that_follows_it()
    {
        var a = MakeRepo("Eta", "ET", "## Priority backlog\n_Nothing recorded yet._\n\n## Notes\nSome prose.\n", "");
        using var db = TicketsDb.Open(Path.Combine(root, "b.db"));
        db.Import([a]);
        db.AddBacklogItem("Eta", "Do the thing");

        var text = System.Text.Encoding.UTF8.GetString(db.Export("Eta")!);
        Assert.That(text, Does.Contain("## Priority backlog\n_Nothing recorded yet._\n1. Do the thing\n\n## Notes\nSome prose.\n"));
    }

    [Test]
    public void Stories_epics_and_backlog_items_are_added_and_edited_in_the_database_and_the_view_follows()
    {
        var (db, _) = Migrated();
        using (db)
        {
            Assert.That(db.AddStory("Alpha", "A", "As a user, I can stop.", "planned"), Is.EqualTo("AL-US-A3"), "the next free number in epic A");
            db.EditStory("AL-US-A1", "As a user, I can start, quickly.");
            db.AddEpic("Alpha", "B", "Reports");
            Assert.That(db.AddStory("Alpha", "B", "As an owner, I see a report."), Is.EqualTo("AL-US-B1"));
            db.AddBacklogItem("Alpha", "Ship AL-US-B1");
            Assert.That(() => db.AddStory("Alpha", "Z", "x"), Throws.InstanceOf<KeyNotFoundException>());
            Assert.That(() => db.AddEpic("Alpha", "B", "again"), Throws.InvalidOperationException);

            var text = System.Text.Encoding.UTF8.GetString(db.Export("Alpha")!);
            Assert.That(text, Does.Contain("- **AL-US-A1 ⬜** As a user, I can start, quickly.\n- **AL-US-A2 ✅** Done thing. *(verified by `T`.)*\n- **AL-US-A3 ⬜** As a user, I can stop."),
                "the edit in place, the new story after the epic's last one");
            Assert.That(text.IndexOf("## Epic B — Reports", StringComparison.Ordinal), Is.GreaterThan(text.IndexOf("AL-US-A3", StringComparison.Ordinal))
                .And.LessThan(text.IndexOf("## Tickets", StringComparison.Ordinal)), "a new epic goes just before the ticket log");
            Assert.That(text, Does.Contain("\n## Epic B — Reports\n- **AL-US-B1 ⬜** As an owner, I see a report.\n\n## Tickets"), "laid out like the existing epics");
            Assert.That(text, Does.Contain("1. **AL-US-A1** next\n2. Ship AL-US-B1"));

            // The view still parses into the same structure: nothing the parser can't read came out of the edits.
            var reparsed = Stories.StoryDocument.Parse(text);
            Assert.That(reparsed.Stories.Select(s => s.Id), Is.EqualTo(new[] { "AL-US-A1", "AL-US-A2", "AL-US-A3", "AL-US-B1" }));
            Assert.That(db.Stories("Alpha").Select(s => (s.Id, s.Epic)), Is.EquivalentTo(new[] { ("AL-US-A1", "A"), ("AL-US-A2", "A"), ("AL-US-A3", "A"), ("AL-US-B1", (string?)"B") }));
            Assert.That(db.Links("AL-US-B1").Select(l => (l.Kind, l.To)), Does.Contain(("part-of", "AL-EPIC-B")));
        }
    }

    [Test]
    public void Renaming_a_repo_moves_every_row_backs_up_first_and_keeps_the_old_name_traceable()
    {
        var (db, _) = Migrated();
        using (db)
        {
            var before = db.Tickets("Alpha").Count;
            var result = db.RenameRepo("Alpha", "Omega", "GTFO.Absurd* -> GTFO.EZ.Absurd*");
            Assert.That(result.NewName, Is.EqualTo("Omega"));
            Assert.That(result.TicketsMoved, Is.EqualTo(before));
            Assert.That(File.Exists(result.BackupPath), "a backup of the database is left behind");

            Assert.That(db.Repos().Select(r => r.Name), Does.Contain("Omega").And.Not.Contain("Alpha"));
            Assert.That(db.Tickets("Omega").Select(t => t.Ticket.Id), Is.EquivalentTo(new[] { "AL-T1", "AL-T2" }), "ticket IDs and codes never change");
            Assert.That(db.Stories("Omega").Select(s => s.Id), Does.Contain("AL-US-A1"));
            var page = System.Text.Encoding.UTF8.GetString(db.Export("Omega")!);
            Assert.That(page, Does.Contain("| AL-T1 |"));
            Assert.That(page, Does.Contain("\nproject: Omega\n").And.Contain("\n# Omega — User Stories\n"), "the page's own name follows the rename");
            Assert.That(page, Does.Not.Contain("project: Alpha").And.Not.Contain("# Alpha "));
            Assert.That(db.Export("Alpha"), Is.Null, "the old name is gone from the live data");

            // Every ticket that moved got its own 'repo' history entry, tracing it back to Alpha.
            Assert.That(db.History("AL-T1").Select(e => (e.Field, e.Old, e.New)), Does.Contain(("repo", (string?)"Alpha", (string?)"Omega")));

            var renames = db.RepoRenames();
            Assert.That(renames.Single(), Is.EqualTo(new RepoRename("Alpha", "Omega", renames[0].At, "GTFO.Absurd* -> GTFO.EZ.Absurd*")));
            Assert.That(db.RepoRenames("alpha").Single().NewName, Is.EqualTo("Omega"), "found by either its old or new name");

            // A reference to the old name still resolves, through the rename history.
            Assert.That(db.Add("Alpha", "fix", "open", "filed under the old name", "").Id, Is.EqualTo("AL-T3"));
            db.AddStory("Alpha", "A", "As a user, I can keep going.");
            db.AddBacklogItem("Alpha", "Ship it");

            Assert.That(() => db.RenameRepo("Nope", "Whatever", "x"), Throws.InstanceOf<KeyNotFoundException>());
            Assert.That(() => db.RenameRepo("Omega", "Omega", "x"), Throws.InvalidOperationException.With.Message.Contains("already named that"));
            Assert.That(() => db.RenameRepo("Beta", "Omega", "x"), Throws.InvalidOperationException.With.Message.Contains("already exists"));
            Assert.That(() => db.RenameRepo("Omega", "", "x"), Throws.ArgumentException);
            Assert.That(() => db.RenameRepo("Omega", "Sigma", " "), Throws.ArgumentException);

            using var raw = new SqliteConnection($"Data Source={db.Path};Pooling=False");
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE tickets SET repo = 'Zed' WHERE id = 'AL-T1'";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.InstanceOf<SqliteException>(), "the append-only trigger is back after the rename");
        }
    }

    [Test]
    public void Reassigning_a_ticket_id_keeps_its_history_links_and_queue_and_the_old_id_still_resolves()
    {
        var (db, _) = Migrated();
        using (db)
        {
            db.Approve("AL-T1");
            db.SetPriority("AL-T1", 7);
            var t = db.ReassignTicketId("AL-T1", "AL-T9", "renumbered");
            Assert.That(t.Id, Is.EqualTo("AL-T9"));
            Assert.That(db.Ticket("AL-T1")?.Ticket.Id, Is.EqualTo("AL-T9"), "the old ID resolves to the ticket");
            Assert.That(db.Tickets("Alpha").Select(x => x.Ticket.Id), Is.EquivalentTo(new[] { "AL-T9", "AL-T2" }));
            Assert.That(db.History("AL-T9").Select(e => e.Field), Does.Contain("created").Or.Contain("id"));
            Assert.That(db.History("AL-T9").Select(e => (e.Field, e.Old)), Does.Contain(("id", (string?)"AL-T1")));
            Assert.That(db.Links("AL-T9").Select(l => (l.From, l.Kind, l.To)), Does.Contain(("AL-T9", "requires", "BE-T1")).And.Contain(("AL-T2", "related", "AL-T9")));
            Assert.That(db.Links("AL-T1"), Is.Empty, "no link is left under the old ID");
            Assert.That(db.QueueState("AL-T9"), Is.EqualTo(("approved", 7L)));
            Assert.That(db.IdHistory("AL-T9").Single(), Has.Property("OldId").EqualTo("AL-T1").And.Property("Kind").EqualTo("ticket"));
            Assert.That(System.Text.Encoding.UTF8.GetString(db.Export("Alpha")!), Does.Contain("| AL-T9 | 2026-10-01 |").And.Not.Contain("| AL-T1 | 2026-10-01 |"));
            Assert.That(db.Check(), Is.Empty);

            db.AddLink("AL-T1", "BE-T2", "related");
            Assert.That(db.Links("AL-T9").Select(l => (l.From, l.To)), Does.Contain(("AL-T9", "BE-T2")), "a link to a former ID lands on the current one");
            Assert.That(db.Add("Alpha", "fix", "open", "next", "").Id, Is.EqualTo("AL-T10"), "numbers keep counting from the highest");

            Assert.That(() => db.ReassignTicketId("AL-T9", "AL-T1", "x"), Throws.InvalidOperationException, "a former ID is never reused");
            Assert.That(() => db.ReassignTicketId("AL-T9", "AL-T2", "x"), Throws.InvalidOperationException);
            Assert.That(() => db.ReassignTicketId("AL-T9", "BE-T50", "x"), Throws.ArgumentException, "the repo's own code only");
            Assert.That(() => db.ReassignTicketId("AL-T9", "AL-X", "x"), Throws.ArgumentException);
            Assert.That(() => db.ReassignTicketId("AL-T9", "AL-T50", " "), Throws.ArgumentException);

            using var raw = new SqliteConnection($"Data Source={db.Path};Pooling=False");
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE tickets SET id = 'AL-T77' WHERE id = 'AL-T9'";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.InstanceOf<SqliteException>(), "the append-only trigger is back");
            cmd.CommandText = "DELETE FROM id_history";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.InstanceOf<SqliteException>(), "ID history is never deleted");
        }
    }

    [Test]
    public void Recoding_a_repo_reassigns_its_ticket_story_and_epic_ids_and_every_old_id_still_resolves()
    {
        var (db, _) = Migrated();
        using (db)
        {
            var r = db.RecodeRepo("Alpha", "A.LPH", "the name was wrong");
            Assert.That((r.OldCode, r.NewCode, r.Tickets, r.Stories, r.Epics), Is.EqualTo(("AL", "A.LPH", 2, 2, 1)));
            Assert.That(File.Exists(r.BackupPath));
            Assert.That(db.Repos().Single(x => x.Name == "Alpha").Code, Is.EqualTo("A.LPH"));
            Assert.That(db.Tickets("Alpha").Select(x => x.Ticket.Id), Is.EquivalentTo(new[] { "A.LPH-T1", "A.LPH-T2" }));
            Assert.That(db.Ticket("AL-T2")?.Ticket.Id, Is.EqualTo("A.LPH-T2"));
            Assert.That(db.Story("AL-US-A1").Single().Id, Is.EqualTo("A.LPH-US-A1"));
            Assert.That(db.Links("A.LPH-US-A1").Select(l => (l.From, l.Kind, l.To)), Does.Contain(("A.LPH-US-A1", "part-of", "A.LPH-EPIC-A")));
            Assert.That(db.Links("A.LPH-T1").Select(l => (l.From, l.Kind, l.To)), Does.Contain(("A.LPH-T1", "implements", "A.LPH-US-A1")));
            Assert.That(db.Add("Alpha", "fix", "open", "new", "").Id, Is.EqualTo("A.LPH-T3"));
            Assert.That(db.Check(), Is.Empty);

            var page = System.Text.Encoding.UTF8.GetString(db.Export("Alpha")!);
            Assert.That(page, Does.Contain("\ncode: A.LPH\n").And.Contain("**A.LPH-US-A1 ⬜**").And.Contain("1. **A.LPH-US-A1** next").And.Contain("| A.LPH-T2 |"));
            Assert.That(page, Does.Contain("build AL-US-A1, blocked by BE-T1"), "a ticket's summary is history and stays as written");

            Assert.That(() => db.RecodeRepo("Beta", "A.LPH", "x"), Throws.InvalidOperationException, "another repo's code");
            Assert.That(() => db.RecodeRepo("Beta", "AL", "x"), Throws.InvalidOperationException, "a former code is never reused");
            Assert.That(() => db.RecodeRepo("Beta", "be-", "x"), Throws.ArgumentException);
        }
    }

    [Test]
    public void The_dump_is_readable_sql_that_lists_every_ticket()
    {
        var (db, _) = Migrated();
        using (db)
        {
            var dump = db.Dump();
            Assert.That(dump, Does.Contain("INSERT INTO tickets").And.Contain("'AL-T1'").And.Contain("INSERT INTO stories"));
            Assert.That(dump, Is.EqualTo(db.Dump()), "deterministic");
        }
    }

    [Test]
    public void Open_disposes_the_connection_when_schema_setup_fails_on_a_corrupt_file()
    {
        // Pooling is off, so if the failed Open()/EnsureSchema() left the SqliteConnection undisposed, the file
        // handle would stay held (until a finalizer eventually ran) and Windows would refuse to overwrite it.
        var path = Path.Combine(root, "corrupt.db");
        File.WriteAllBytes(path, "not a sqlite database"u8.ToArray());

        Assert.Throws<SqliteException>(() => TicketsDb.Open(path));
        Assert.DoesNotThrow(() => File.WriteAllBytes(path, "still not a sqlite database, but overwritable"u8.ToArray()),
            "the connection from the failed Open() must have been disposed, not leaked");
    }

    [Test]
    public void OpenIfMigrated_disposes_the_connection_when_construction_fails_on_a_corrupt_file()
    {
        var path = Path.Combine(root, "corrupt.db");
        File.WriteAllBytes(path, "not a sqlite database"u8.ToArray());
        Environment.SetEnvironmentVariable(TicketsDb.EnvVar, path);
        try
        {
            Assert.Throws<SqliteException>(() => TicketsDb.OpenIfMigrated());
            Assert.DoesNotThrow(() => File.WriteAllBytes(path, "still not a sqlite database, but overwritable"u8.ToArray()),
                "the connection from the failed OpenIfMigrated() must have been disposed, not leaked");
        }
        finally { Environment.SetEnvironmentVariable(TicketsDb.EnvVar, null); }
    }

    [Test]
    public void The_real_workspace_migrates_with_every_file_rebuilt_byte_for_byte()
    {
        var ws = new Workspace(Workspace.DefaultRoot);
        var repos = ws.Repos().Where(r => File.Exists(r.StoriesPath)).ToList();
        if (repos.Count == 0) Assert.Ignore("no workspace on this machine");

        using var db = TicketsDb.Open(Path.Combine(root, "real.db"));
        var report = db.Import(repos);
        Assert.That(report.Where(r => !r.RoundTrips).Select(r => r.Repo), Is.Empty);
        Assert.That(report.Sum(r => r.Tickets), Is.EqualTo(db.Tickets().Count));
        TestContext.Out.WriteLine($"{report.Count} repos, {report.Sum(r => r.Epics)} epics, {report.Sum(r => r.Stories)} stories, " +
            $"{report.Sum(r => r.BacklogItems)} backlog items, {report.Sum(r => r.Tickets)} tickets, {db.AllLinks().Count} links");
    }
}
