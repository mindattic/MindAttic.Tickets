using Microsoft.Data.Sqlite;
using MindAttic.Workflow.Data;
using MindAttic.Workflow.Tickets;
using NUnit.Framework;

namespace MindAttic.Workflow.Tests;

public sealed class WorkflowDbTests
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

    private (WorkflowDb Db, List<Repo> Repos) Migrated()
    {
        var a = MakeRepo("Alpha", "AL",
            "## Epic A — Core\n- **AL-US-A1 ⬜** As a user, I can start. *(no test yet.)*\n- **AL-US-A2 ✅** Done thing. *(verified by `T`.)*\n\n## Priority backlog\n1. **AL-US-A1** next\n",
            "| AL-T1 | 2026-10-01 | feature | open | build AL-US-A1, blocked by BE-T1 | — |\n| AL-T2 | 2026-10-02 | fix | closed | fix it | AL-T1 |\n");
        var b = MakeRepo("Beta", "BE", "## Priority backlog\n_Nothing recorded yet._\n",
            "| BE-T1 | 2026-10-01 | chore | open | groundwork | superseded by BE-T2 |\n| BE-T2 | 2026-10-03 | chore | open | groundwork, done right | — |\n");
        var db = WorkflowDb.Open(Path.Combine(root, "MindAttic.Workflow", "data", "workflow.db"));
        db.Import([a, b], new Dictionary<string, IReadOnlyList<string>> { ["AL-T2"] = ["BE-T2"] });
        return (db, [a, b]);
    }

    [Test]
    public void Import_keeps_every_byte_and_structures_stories_tickets_and_links()
    {
        var a = MakeRepo("Alpha", "AL", "## Epic A — Core\n- **AL-US-A1 ⬜** Start.\n", "| AL-T1 | 2026-10-01 | feature | open | build AL-US-A1, blocked by BE-T1 | — |\n");
        var b = MakeRepo("Beta", "BE", "", "| BE-T1 | 2026-10-01 | chore | open | groundwork | superseded by BE-T2 |\n| BE-T2 | 2026-10-03 | chore | open | redo | — |\n");
        using var db = WorkflowDb.Open(Path.Combine(root, "w.db"));

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
            Assert.That(view, Does.Contain("---\n" + WorkflowDb.ViewMarker + "\n"));
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
        using var db = WorkflowDb.Open(Path.Combine(root, "c.db"));
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
    public void The_real_workspace_migrates_with_every_file_rebuilt_byte_for_byte()
    {
        var ws = new Workspace(Workspace.DefaultRoot);
        var repos = ws.Repos().Where(r => File.Exists(r.StoriesPath)).ToList();
        if (repos.Count == 0) Assert.Ignore("no workspace on this machine");

        using var db = WorkflowDb.Open(Path.Combine(root, "real.db"));
        var report = db.Import(repos);
        Assert.That(report.Where(r => !r.RoundTrips).Select(r => r.Repo), Is.Empty);
        Assert.That(report.Sum(r => r.Tickets), Is.EqualTo(db.Tickets().Count));
        TestContext.Out.WriteLine($"{report.Count} repos, {report.Sum(r => r.Epics)} epics, {report.Sum(r => r.Stories)} stories, " +
            $"{report.Sum(r => r.BacklogItems)} backlog items, {report.Sum(r => r.Tickets)} tickets, {db.AllLinks().Count} links");
    }
}
