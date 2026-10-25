using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The ticket commands as an agent runs them: the real `tickets` against a temp --root workspace.</summary>
public sealed class TicketCliTests
{
    private string root = "";

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private string LogDb => Path.Combine(root, "log", "MindAttic.Log.db");

    private (int Code, string Out) Run(params string[] args) => RunWithInput(null, args);

    [Test]
    public void Every_command_is_logged_to_mindattic_log_with_its_outcome()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        Assert.That(Run("ticket", "close", "AA-T1", "--repo", "A").Code, Is.Zero);
        Assert.That(Run("ticket", "close", "AA-T99", "--repo", "A").Code, Is.EqualTo(1));

        using var c = new SqliteConnection($"Data Source={LogDb};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Level, Application, Message FROM MindAttic_Log ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var rows = new List<(long, string, string)>();
        while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        Assert.That(rows.Select(x => x.Item2).Distinct(), Is.EqualTo(new[] { "MindAttic.Tickets" }));
        Assert.That(rows.Any(x => x.Item3.StartsWith("tickets ticket close AA-T1 ", StringComparison.Ordinal) && x.Item3.EndsWith("exited 0", StringComparison.Ordinal)));
        Assert.That(rows.Any(x => x.Item1 == 4 && x.Item3.StartsWith("tickets ticket close AA-T99", StringComparison.Ordinal) && x.Item3.Contains("failed", StringComparison.Ordinal)),
            "the failure is an Error row");
    }

    private (int Code, string Out) RunWithInput(string? input, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "tickets.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root);
        psi.Environment.Remove("MINDATTIC_TICKETS_DB");
        psi.Environment["COLUMNS"] = "400";
        psi.Environment["NO_COLOR"] = "1"; // a redirected stdin otherwise lets Spectre emit ANSI colors
        psi.Environment[Tickets.Repos.GitHubRepoIndex.FileEnvVar] = Path.Combine(root, "github.json");
        psi.Environment["MINDATTIC_TICKETS_LOG_DB"] = LogDb;
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(input ?? "");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }

    private string MakeRepo(string name, string code, string rows = "")
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            $"---\ncode: {code}\n---\n\n# {name}\n\n## Tickets\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" + rows);
        return dir;
    }

    [Test]
    public void New_prints_the_next_id_and_rejects_a_bad_type_status_or_summary()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        var ok = Run("ticket", "new", "--repo", "A", "--type", "chore", "--summary", "two");
        Assert.That((ok.Code, ok.Out.Trim()), Is.EqualTo((0, "AA-T2")));
        Assert.That(File.ReadAllText(Path.Combine(root, "A", "docs", "USER_STORIES.md")), Does.Contain("| AA-T2 |"));

        Assert.That(Run("ticket", "new", "--repo", "A", "--type", "bugfix", "--summary", "x").Code, Is.Not.Zero);
        Assert.That(Run("ticket", "new", "--repo", "A", "--type", "fix", "--status", "done", "--summary", "x").Code, Is.Not.Zero);
        Assert.That(Run("ticket", "new", "--repo", "A", "--type", "fix", "--summary", " ").Code, Is.Not.Zero);

        Directory.CreateDirectory(Path.Combine(root, "B", ".git"));
        var missing = Run("ticket", "new", "--repo", "B", "--type", "fix", "--summary", "x");
        Assert.That(missing.Code, Is.EqualTo(1));
        Assert.That(missing.Out, Does.Contain("no docs/USER_STORIES.md"));
    }

    [Test]
    public void Close_moves_a_ticket_to_closed_or_wont_do()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n| AA-T2 | 2026-10-01 | fix | open | two | — |\n");
        Assert.That(Run("ticket", "close", "AA-T1", "--repo", "A", "--ref", "the test").Out.Trim(), Is.EqualTo("AA-T1 closed"));
        Assert.That(Run("ticket", "close", "AA-T2", "--repo", "A", "--wont-do").Out.Trim(), Is.EqualTo("AA-T2 wont-do"));
        var text = File.ReadAllText(Path.Combine(root, "A", "docs", "USER_STORIES.md"));
        Assert.That(text, Does.Contain("| AA-T1 | 2026-10-01 | fix | closed | one | the test |").And.Contain("| AA-T2 | 2026-10-01 | fix | wont-do | two | — |"));
    }

    [Test]
    public void Close_accepts_status_wont_do_as_an_alias_for_wont_do_and_rejects_bad_or_conflicting_status()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n| AA-T2 | 2026-10-01 | fix | open | two | — |\n");

        // The bug this fixes: 'ticket close ... --status wont-do' used to be silently parsed as nothing
        // (an unknown option to this command) and the ticket closed as 'closed' instead.
        Assert.That(Run("ticket", "close", "AA-T1", "--repo", "A", "--status", "wont-do").Out.Trim(), Is.EqualTo("AA-T1 wont-do"));
        Assert.That(Run("ticket", "close", "AA-T2", "--repo", "A", "--status", "closed").Out.Trim(), Is.EqualTo("AA-T2 closed"));

        var badStatus = Run("ticket", "close", "AA-T1", "--repo", "A", "--status", "bogus");
        Assert.That(badStatus.Code, Is.Not.Zero);
        Assert.That(badStatus.Out, Does.Contain("--status"));

        var conflict = Run("ticket", "close", "AA-T1", "--repo", "A", "--status", "closed", "--wont-do");
        Assert.That(conflict.Code, Is.Not.Zero);
        Assert.That(conflict.Out, Does.Contain("--status").And.Contain("--wont-do"));
    }

    [TestCase("new", "--repo", "A", "--type", "fix", "--summary", "x")]
    [TestCase("close", "AA-T1")]
    [TestCase("list")]
    [TestCase("show", "AA-T2")]
    [TestCase("link", "AA-T2", "requires", "AA-T1")]
    [TestCase("correct", "AA-T2", "--type", "fix", "--reason", "x")]
    [TestCase("verify", "--repo", "A")]
    [TestCase("queue")]
    [TestCase("approve", "AA-T2")]
    [TestCase("reject", "AA-T2", "--reason", "x")]
    [TestCase("priority", "AA-T1", "1")]
    [TestCase("bump", "AA-T1")]
    [TestCase("assign", "AA-T1", "a@b.co")]
    [TestCase("block", "AA-T2", "--by", "AA-T1")]
    [TestCase("reserve", "AA-T1", "2M")]
    [TestCase("working", "AA-T1", "--login", "a@b.co")]
    [TestCase("hygiene")]
    [TestCase("note", "AA-T1", "a note")]
    public void Every_ticket_subcommand_rejects_an_option_it_doesnt_know_instead_of_silently_ignoring_it(string subcommand, params string[] args)
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n| AA-T2 | 2026-10-01 | fix | open | two | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover"); // IDs are then unique workspace-wide, so the ID-only commands need no --repo

        var ok = Run(["ticket", subcommand, .. args]);
        Assert.That(ok.Code, Is.Zero, ok.Out); // the base invocation is valid on its own
        var bad = Run(["ticket", subcommand, .. args, "--this-option-does-not-exist", "x"]);
        Assert.That(bad.Code, Is.Not.EqualTo(0), bad.Out);
        Assert.That(bad.Out, Does.Contain("this-option-does-not-exist"));
    }

    [Test]
    public void The_orchestrators_ticket_functions_work_from_the_cli_and_show_reports_them()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | feature | open | one | — |\n| AA-T2 | 2026-10-01 | fix | open | two | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        Assert.That(Run("ticket", "bump", "AA-T1", "--by", "3").Out.Trim(), Is.EqualTo("AA-T1 priority 3"));
        Assert.That(Run("ticket", "assign", "AA-T1", "Ryan@Example.com").Out, Does.Contain("assigned to ryan@example.com"));
        Assert.That(Run("ticket", "assign", "AA-T1", "Ryan").Code, Is.EqualTo(1), "not an email");
        Assert.That(Run("ticket", "block", "AA-T1", "--by", "AA-T2").Out.Trim(), Is.EqualTo("AA-T1 requires AA-T2."));
        Assert.That(Run("ticket", "block", "AA-T2", "--by", "AA-T1").Code, Is.EqualTo(1), "a cycle");
        Assert.That(Run("ticket", "reserve", "AA-T1", "2M").Out, Does.Contain("AA-T1 has 2M reserved"));
        Assert.That(Run("ticket", "reserve", "AA-T1", "lots").Code, Is.Not.Zero);
        Assert.That(Run("ticket", "working", "AA-T1", "--login", "ryan@example.com", "--session", "s-1").Code, Is.Zero);
        var approve = Run("ticket", "approve", "AA-T1", "AA-T99");
        Assert.That(approve.Code, Is.EqualTo(1));
        Assert.That(approve.Out, Does.Contain("No ticket AA-T99"));
        Assert.That(Run("ticket", "approve").Out, Does.Contain("AA-T1").And.Not.Contain("AA-T2"), "fixes need no approval");

        var show = JsonDocument.Parse(Run("ticket", "show", "AA-T1", "--json").Out).RootElement;
        Assert.That(show.GetProperty("priority").GetInt64(), Is.EqualTo(3));
        Assert.That(show.GetProperty("approval").GetString(), Is.EqualTo("proposed"), "the bad id stopped the whole approve");
        Assert.That(show.GetProperty("assignee").GetString(), Is.EqualTo("ryan@example.com"));
        Assert.That(show.GetProperty("reserved").GetInt64(), Is.EqualTo(2_000_000));
        Assert.That(show.GetProperty("working").GetProperty("session").GetString(), Is.EqualTo("s-1"));
        Assert.That(show.GetProperty("requires").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(new[] { "AA-T2" }));
        var queue = JsonDocument.Parse(Run("ticket", "queue", "--json").Out).RootElement.EnumerateArray().First();
        Assert.That(queue.GetProperty("assignee").GetString(), Is.EqualTo("ryan@example.com"));
    }

    [Test]
    public void Ticket_graph_prints_the_requires_chains_both_ways_with_blockers_marked()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | feature | open | one | — |\n| AA-T2 | 2026-10-01 | fix | open | two | — |\n" +
                            "| AA-T3 | 2026-10-01 | fix | closed | three | — |\n| AA-T4 | 2026-10-01 | feature | open | four | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");
        Run("ticket", "link", "AA-T1", "requires", "AA-T2");
        Run("ticket", "link", "AA-T2", "requires", "AA-T3");
        Run("ticket", "link", "AA-T4", "requires", "AA-T1");

        var text = Run("ticket", "graph", "aa-t1");
        Assert.That(text.Code, Is.Zero);
        Assert.That(text.Out, Does.Contain("Waits for (1 open blocker):").And.Contain("  AA-T2, open, blocking: two")
            .And.Contain("    AA-T3, closed: three").And.Contain("Needed by (1 blocked by it):").And.Contain("  AA-T4, open, blocked by it: four"));

        var json = JsonDocument.Parse(Run("ticket", "graph", "AA-T1", "--json").Out).RootElement;
        Assert.That(json.GetProperty("blockers").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(new[] { "AA-T2" }));
        Assert.That(json.GetProperty("waitsFor").EnumerateArray().Select(x => (x.GetProperty("id").GetString(), x.GetProperty("depth").GetInt32())),
            Is.EqualTo(new[] { ("AA-T2", 1), ("AA-T3", 2) }));
        Assert.That(Run("ticket", "graph", "AA-T99").Code, Is.EqualTo(1));
    }

    [Test]
    public void Ticket_note_appends_a_comment_that_show_lists()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | feature | open | one | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        var added = Run("ticket", "note", "aa-t1", "First **finding**", "--author", "ryan");
        Assert.That(added.Code, Is.Zero, added.Out);
        Assert.That(added.Out, Does.StartWith("AA-T1 note 1 by ryan at "));
        Assert.That(Run("ticket", "note", "AA-T1", "second").Code, Is.Zero, "the author defaults");
        Assert.That(Run("ticket", "note", "AA-T99", "x").Code, Is.EqualTo(1));
        Assert.That(Run("ticket", "note", "AA-T1", " ").Code, Is.EqualTo(1));

        var notes = JsonDocument.Parse(Run("ticket", "show", "AA-T1", "--json").Out).RootElement.GetProperty("notes").EnumerateArray().ToList();
        Assert.That(notes.Select(n => n.GetProperty("markdown").GetString()), Is.EqualTo(new[] { "First **finding**", "second" }));
        Assert.That(notes[0].GetProperty("author").GetString(), Is.EqualTo("ryan"));
        Assert.That(notes[1].GetProperty("author").GetString(), Is.Not.Empty);
        Assert.That(Run("ticket", "show", "AA-T1").Out, Does.Contain("Notes (2)").And.Contain("First **finding**"));
    }

    [Test]
    public void List_filters_and_emits_the_documented_json()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-09-01 | fix | open | old | — |\n| AA-T2 | 2026-10-05 | chore | closed | new | x |\n");
        MakeRepo("B", "BB", "| BB-T1 | 2026-10-06 | fix | open | other | — |\n");
        List<JsonElement> Json(params string[] filter) =>
            [.. JsonDocument.Parse(Run(["ticket", "list", "--json", .. filter]).Out).RootElement.EnumerateArray()];

        Assert.That(Json().Select(x => x.GetProperty("id").GetString()), Is.EqualTo(new[] { "AA-T1", "AA-T2", "BB-T1" }));
        Assert.That(Json("--repo", "b").Select(x => x.GetProperty("id").GetString()), Is.EqualTo(new[] { "BB-T1" }));
        Assert.That(Json("--status", "open", "--type", "fix").Count, Is.EqualTo(2));
        Assert.That(Json("--since", "2026-10-01").Select(x => x.GetProperty("id").GetString()), Is.EqualTo(new[] { "AA-T2", "BB-T1" }));
        Assert.That(Json()[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "repo", "id", "date", "type", "status", "summary", "ref" }));
    }

    [Test]
    public void Verify_fails_a_deleted_or_reworded_committed_row_and_passes_a_new_uncommitted_file()
    {
        var a = MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        Assert.That(Run("ticket", "verify", "--repo", "A").Code, Is.Zero, "nothing committed yet: nothing to compare against");
        Git(a, "add", "-A");
        Git(a, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init");

        var path = Path.Combine(a, "docs", "USER_STORIES.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("| one |", "| reworded |"));
        var reworded = Run("ticket", "verify", "--repo", "A");
        Assert.That(reworded.Code, Is.EqualTo(1));
        Assert.That(reworded.Out, Does.Contain("reworded"));

        File.WriteAllText(path, File.ReadAllText(path).Replace("| AA-T1 | 2026-10-01 | fix | open | reworded | — |\n", ""));
        Assert.That(Run("ticket", "verify", "--repo", "A").Out, Does.Contain("deleted"));
    }

    [Test]
    public void Once_migrated_the_same_commands_read_and_write_the_database_and_the_view_follows()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        var migrate = Run("db", "migrate", "--cutover");
        Assert.That(migrate.Code, Is.Zero, migrate.Out);

        Assert.That(Run("ticket", "new", "--repo", "A", "--type", "fix", "--summary", "two, blocked by AA-T1").Out.Trim(), Is.EqualTo("AA-T2"));
        Assert.That(Run("ticket", "close", "AA-T1", "--ref", "done").Out.Trim(), Is.EqualTo("AA-T1 closed"), "no --repo needed: IDs are unique");
        var show = JsonDocument.Parse(Run("ticket", "show", "AA-T2", "--json").Out).RootElement;
        Assert.That(show.GetProperty("requires").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(new[] { "AA-T1" }));
        var view = File.ReadAllText(Path.Combine(root, "A", "docs", "USER_STORIES.md"));
        Assert.That(view, Does.Contain("GENERATED from MindAttic.Tickets/data/Tickets.db").And.Contain("| AA-T1 | 2026-10-01 | fix | closed | one | done |").And.Contain("| AA-T2 |"));
        Assert.That(Run("db", "check").Code, Is.Zero);
    }

    [Test]
    public void Correct_relabels_a_nonstandard_type_and_only_accepts_the_standard_vocabulary()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | bugfix | open | odd type | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        var correct = Run("ticket", "correct", "AA-T1", "--type", "fix", "--reason", "bugfix predates the vocabulary");
        Assert.That(correct.Out.Trim(), Is.EqualTo("AA-T1 type fix"));
        Assert.That(Run("db", "check").Code, Is.Zero);
        Assert.That(Run("ticket", "correct", "AA-T1", "--type", "bugfix", "--reason", "x").Code, Is.Not.Zero, "only the standard vocabulary is accepted");
    }

    [Test]
    public void Correct_rewords_a_summary_and_describe_sets_a_description_apart_from_it()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | stale wording | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        Assert.That(Run("ticket", "correct", "AA-T1", "--summary", "fresh wording", "--reason", "pre-rename").Out.Trim(), Is.EqualTo("AA-T1 summary: fresh wording"));
        Assert.That(Run("ticket", "correct", "AA-T1", "--summary", "x").Code, Is.Not.Zero, "a reason is required");
        Assert.That(Run("ticket", "correct", "AA-T1", "--reason", "x").Code, Is.Not.Zero, "something to correct is required");
        Assert.That(File.ReadAllText(Path.Combine(root, "A", "docs", "USER_STORIES.md")), Does.Contain("| AA-T1 | 2026-10-01 | fix | open | fresh wording | — |"), "the view follows");

        Assert.That(Run("ticket", "new", "--repo", "A", "--type", "fix", "--summary", "two", "--description", "Body **two**.").Out.Trim(), Is.EqualTo("AA-T2"));
        Assert.That(RunWithInput("# Heading\nline\n", "ticket", "describe", "AA-T1", "--stdin").Out.Trim(), Is.EqualTo("AA-T1 description set"));
        Assert.That(Run("ticket", "describe", "AA-T1", "--text", "x", "--clear").Code, Is.Not.Zero, "one of --text, --file, --stdin or --clear");

        var one = JsonDocument.Parse(Run("ticket", "show", "AA-T1", "--json").Out).RootElement;
        Assert.That(one.GetProperty("summary").GetString(), Is.EqualTo("fresh wording"));
        Assert.That(one.GetProperty("description").GetString(), Is.EqualTo("# Heading\nline"));
        Assert.That(one.GetProperty("history").EnumerateArray().Select(e => e.GetProperty("field").GetString()), Is.EqualTo(new[] { "summary", "description" }));
        Assert.That(JsonDocument.Parse(Run("ticket", "show", "AA-T2", "--json").Out).RootElement.GetProperty("description").GetString(), Is.EqualTo("Body **two**."));
        var shown = Run("ticket", "show", "AA-T1").Out;
        Assert.That(shown, Does.Contain("Description").And.Contain("# Heading").And.Contain("description added"));

        Assert.That(Run("ticket", "describe", "AA-T1", "--clear").Out.Trim(), Is.EqualTo("AA-T1 description cleared"));
        Assert.That(JsonDocument.Parse(Run("ticket", "show", "AA-T1", "--json").Out).RootElement.GetProperty("description").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void Link_connects_two_tickets_and_refuses_a_cycle()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n| AA-T2 | 2026-10-01 | fix | open | two | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        Assert.That(Run("ticket", "link", "AA-T2", "requires", "AA-T1").Out.Trim(), Is.EqualTo("AA-T2 requires AA-T1"));
        var show = JsonDocument.Parse(Run("ticket", "show", "AA-T2", "--json").Out).RootElement;
        Assert.That(show.GetProperty("requires").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(new[] { "AA-T1" }));

        var cycle = Run("ticket", "link", "AA-T1", "requires", "AA-T2");
        Assert.That(cycle.Code, Is.Not.Zero);
        Assert.That(cycle.Out, Does.Contain("cycle"));

        Assert.That(Run("ticket", "link", "AA-T2", "requires", "AA-T1", "--remove").Out.Trim(), Is.EqualTo("removed AA-T2 requires AA-T1"));
        Assert.That(Run("ticket", "link", "AA-T2", "requires", "AA-T1", "--remove").Out.Trim(), Is.EqualTo("no such link"));
    }

    [Test]
    public void Story_epic_and_backlog_commands_edit_the_database_through_the_cli()
    {
        var dir = Path.Combine(root, "A");
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            "---\ncode: AA\n---\n\n# A\n\n## Priority backlog\n_Nothing recorded yet._\n\n## Tickets\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        Assert.That(Run("epic", "new", "--repo", "A", "--key", "A", "--title", "Core").Out.Trim(), Is.EqualTo("Epic A added"));
        Assert.That(Run("story", "new", "--repo", "A", "--epic", "A", "--text", "As a user, I can begin.").Out.Trim(), Is.EqualTo("AA-US-A1"));
        Assert.That(Run("story", "edit", "AA-US-A1", "--text", "As a user, I can begin, revised.").Out.Trim(), Is.EqualTo("AA-US-A1 updated"));
        Assert.That(Run("story", "status", "AA-US-A1", "done").Out.Trim(), Is.EqualTo("AA-US-A1 done"));
        Assert.That(Run("backlog", "add", "--repo", "A", "--text", "Ship AA-US-A1").Out.Trim(), Is.EqualTo("added to the priority backlog"));
        Assert.That(Run("db", "check").Code, Is.Zero);

        var view = File.ReadAllText(Path.Combine(dir, "docs", "USER_STORIES.md"));
        Assert.That(view, Does.Contain("- **AA-US-A1 ✅** As a user, I can begin, revised."));
        Assert.That(view, Does.Contain("1. Ship AA-US-A1"));
    }

    [Test]
    public void Render_rewrites_a_view_that_fell_out_of_date()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        var storiesPath = Path.Combine(root, "A", "docs", "USER_STORIES.md");
        File.Delete(storiesPath);
        Assert.That(Run("db", "render").Out.Trim(), Is.EqualTo("Rewrote A"));
        Assert.That(File.Exists(storiesPath));
        Assert.That(Run("db", "render").Out.Trim(), Is.EqualTo("Every view is current."));
    }

    [Test]
    public void Sync_commits_only_the_database_files_and_a_second_run_is_a_no_op()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        var mw = Path.Combine(root, "MindAttic.Tickets");
        Directory.CreateDirectory(mw);
        Git(mw, "init", "-q", "-b", "main");
        Git(mw, "config", "user.email", "t@t");
        Git(mw, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(mw, "README.md"), "placeholder");
        Git(mw, "add", "-A");
        Git(mw, "commit", "-qm", "init");

        Run("db", "migrate", "--cutover");
        File.WriteAllText(Path.Combine(mw, "scratch.txt"), "untracked work");

        var sync = Run("db", "sync", "--no-push");
        Assert.That(sync.Code, Is.Zero, sync.Out);
        Assert.That(sync.Out, Does.Contain("Committed the ticket database: Sync the ticket database (AA-T1)"));
        Assert.That(Tickets.Git.Run(mw, "log", "-1", "--pretty=%s").Output.Trim(), Is.EqualTo("Sync the ticket database (AA-T1)"), "the subject names the ticket the commit carries");
        Assert.That(File.Exists(Path.Combine(mw, "data", "Tickets.sql")));

        var log = Tickets.Git.Run(mw, "log", "-1", "--name-only", "--pretty=format:").Output;
        Assert.That(log, Does.Contain("data/Tickets.db").And.Contain("data/Tickets.sql").And.Not.Contain("scratch.txt"));

        Assert.That(Run("db", "sync", "--no-push").Out, Does.Contain("No changes to the ticket database since the last sync."));
    }

    [Test]
    public void Sync_fails_instead_of_reporting_no_changes_when_git_add_cant_stage()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        var mw = MakeRepo("MindAttic.Tickets", "MW");
        Git(mw, "config", "user.email", "t@t");
        Git(mw, "config", "user.name", "t");
        Run("db", "migrate", "--cutover");

        // Another git (the daemon's sync, an agent's commit) holding the index lock makes 'git add' fail.
        File.WriteAllText(Path.Combine(mw, ".git", "index.lock"), "");
        var sync = Run("db", "sync", "--no-push");
        Assert.That(sync.Code, Is.Not.Zero, sync.Out);
        Assert.That(sync.Out, Does.Contain("git add failed").And.Not.Contain("No changes"));
    }

    [Test]
    public void Sync_with_no_ticketed_change_opens_a_closed_chore_ticket_in_MindAttic_Tickets_to_carry_it()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        var mw = MakeRepo("MindAttic.Tickets", "MW");
        Git(mw, "config", "user.email", "t@t");
        Git(mw, "config", "user.name", "t");

        Run("db", "migrate", "--cutover");
        Assert.That(Run("db", "sync", "--no-push").Out, Does.Contain("Sync the ticket database (AA-T1)"), "the baseline sync carries the only ticket so far");

        // An epic edit alone touches no ticket row or history, so a plain sync has nothing to name itself after.
        Assert.That(Run("epic", "new", "--repo", "A", "--key", "E", "--title", "Something").Code, Is.Zero);

        var sync = Run("db", "sync", "--no-push");
        Assert.That(sync.Code, Is.Zero, sync.Out);
        Assert.That(sync.Out, Does.Contain("Committed the ticket database: Sync the ticket database (MW-T1)"));
        Assert.That(Tickets.Git.Run(mw, "log", "-1", "--pretty=%s").Output.Trim(), Is.EqualTo("Sync the ticket database (MW-T1)"));

        var show = Run("ticket", "show", "MW-T1");
        Assert.That(show.Out, Does.Contain("chore").And.Contain("closed"), "the new ticket is already closed, not left open");

        var log = Tickets.Git.Run(mw, "log", "-1", "--name-only", "--pretty=format:").Output;
        Assert.That(log, Does.Contain("data/Tickets.db").And.Contain("data/Tickets.sql").And.Contain("docs/USER_STORIES.md"),
            "MindAttic.Tickets's own re-rendered view rides along with the ticket that now appears in it");
        Assert.That(File.ReadAllText(Path.Combine(mw, "docs", "USER_STORIES.md")), Does.Contain("MW-T1"));
    }

    [Test]
    public void A_failed_commit_never_leaves_the_database_staged_for_the_next_commit_to_sweep_in()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n");
        var mw = Path.Combine(root, "MindAttic.Tickets");
        Directory.CreateDirectory(mw);
        Git(mw, "init", "-q", "-b", "main");
        Git(mw, "config", "user.email", "t@t");
        Git(mw, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(mw, "README.md"), "placeholder");
        Git(mw, "add", "-A");
        Git(mw, "commit", "-qm", "init");

        Run("db", "migrate", "--cutover");
        Assert.That(Run("db", "sync", "--no-push").Code, Is.Zero, "the database is tracked and committed before the failing hook is installed");

        // Another agent's own ticket work dirties the shared database again.
        Assert.That(Run("ticket", "new", "--repo", "A", "--type", "chore", "--summary", "two").Out.Trim(), Is.EqualTo("AA-T2"));

        var hooks = Path.Combine(mw, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "commit-msg"), "#!/bin/sh\nexit 1\n");

        var failed = Run("db", "sync", "--ticket", "AA-T2", "--no-push");
        Assert.That(failed.Code, Is.Not.Zero, failed.Out);

        // The failed commit must not leave data/Tickets.db or data/Tickets.sql staged: nothing in the
        // index may differ from HEAD for them, so an unrelated commit made next in this repo can never
        // sweep in the database's current (partly someone else's pending) content.
        Assert.That(Tickets.Git.Run(mw, "diff", "--cached", "--quiet", "--", "data/Tickets.db", "data/Tickets.sql").Ok,
            "the index must be clean for the database files after a failed sync");

        File.Delete(Path.Combine(hooks, "commit-msg"));
        File.WriteAllText(Path.Combine(mw, "code.txt"), "unrelated ticket's own file");
        Git(mw, "add", "--", "code.txt");
        Git(mw, "commit", "-qm", "unrelated work (AA-T2)");
        var carried = Tickets.Git.Run(mw, "log", "-1", "--name-only", "--pretty=format:").Output;
        Assert.That(carried, Does.Contain("code.txt").And.Not.Contain("data/Tickets.db").And.Not.Contain("data/Tickets.sql"));
    }

    [Test]
    public void View_writes_a_page_and_a_snapshot_of_every_ticket_and_story_with_markup_escaped()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | close </script><b>x</b> & more | — |\n");
        MakeRepo("B", "BB", "| BB-T1 | 2026-10-02 | chore | closed | other | done |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        var site = Path.Combine(root, "site");
        var view = Run("view", "--out", site, "--no-open", "--repo", "b");
        Assert.That((view.Code, view.Out.Trim()), Is.EqualTo((0, site)), view.Out);
        Assert.That(File.ReadAllText(Path.Combine(site, "index.html")), Does.Contain("<script src=\"data.js\"></script>"));

        var js = File.ReadAllText(Path.Combine(site, "data.js"));
        Assert.That(js, Does.StartWith("window.TICKETS = ").And.Not.Contain("</script>").And.Not.Contain("<b>"), "ticket text can't break out of the script");
        var data = JsonDocument.Parse(js["window.TICKETS = ".Length..].TrimEnd().TrimEnd(';')).RootElement;
        Assert.That(data.GetProperty("selectedRepo").GetString(), Is.EqualTo("b"));
        var tickets = data.GetProperty("tickets").EnumerateArray().ToList();
        Assert.That(tickets.Select(t => t.GetProperty("id").GetString()), Is.EquivalentTo(new[] { "AA-T1", "BB-T1" }));
        Assert.That(tickets.Single(t => t.GetProperty("id").GetString() == "AA-T1").GetProperty("summary").GetString(), Is.EqualTo("close </script><b>x</b> & more"));
        Assert.That(tickets[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "repo", "id", "date", "type", "status", "summary", "ref" }));

        Assert.That(Run("view", "--out", site, "--no-open", "--repo", "Nope").Code, Is.Not.Zero, "an unknown repo is an error, not an empty page");
    }

    [Test]
    public void Rollout_previews_then_seeds_a_new_file_with_an_unused_code_and_never_overwrites_one()
    {
        Directory.CreateDirectory(Path.Combine(root, "Alpha", ".git"));
        MakeRepo("Beta", "AL"); // already has the code Alpha would get first
        var before = File.ReadAllText(Path.Combine(root, "Beta", "docs", "USER_STORIES.md"));

        Assert.That(Run("repos", "rollout", "tickets").Out, Does.Contain("would Alpha"));
        Assert.That(File.Exists(Path.Combine(root, "Alpha", "docs", "USER_STORIES.md")), Is.False, "a preview writes nothing");

        Run("repos", "rollout", "tickets", "--apply");
        var alpha = File.ReadAllText(Path.Combine(root, "Alpha", "docs", "USER_STORIES.md"));
        Assert.That(alpha, Does.Contain("code: ALP").And.Contain("| ALP-T1 |"), "AL is taken, so the next derived code");
        Assert.That(File.ReadAllText(Path.Combine(root, "Beta", "docs", "USER_STORIES.md")), Is.EqualTo(before), "an existing file is left alone");
    }

    [Test]
    public void Once_migrated_rollout_adds_a_new_repo_to_the_database_with_its_view()
    {
        MakeRepo("Alpha", "AL", "| AL-T1 | 2026-10-01 | fix | open | one | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");
        Directory.CreateDirectory(Path.Combine(root, "Gamma", ".git"));

        Assert.That(Run("repos", "rollout", "tickets").Out, Does.Contain("would Gamma: add to the ticket database"));
        Run("repos", "rollout", "tickets", "--apply");
        Assert.That(Run("ticket", "list", "--repo", "Gamma", "--json").Out, Does.Match("\"id\": \"[A-Z][A-Z0-9]*-T1\""), "seeded with its first ticket");
        Assert.That(File.ReadAllText(Path.Combine(root, "Gamma", "docs", "USER_STORIES.md")), Does.Contain("GENERATED from MindAttic.Tickets/data/Tickets.db"));
        Assert.That(Run("repos", "rollout", "tickets").Out, Does.Contain("Every repo is already in the ticket database."));
    }

    [Test]
    public void Once_migrated_rollout_imports_a_new_repos_own_stories_file_instead_of_overwriting_it()
    {
        MakeRepo("Alpha", "AL", "| AL-T1 | 2026-10-01 | fix | open | one | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");
        MakeRepo("Delta", "DE", "| DE-T1 | 2026-10-02 | feature | open | keep me | — |\n");

        Assert.That(Run("repos", "rollout", "tickets").Out, Does.Contain("would Delta: import its USER_STORIES.md"));
        Run("repos", "rollout", "tickets", "--apply");
        Assert.That(Run("ticket", "list", "--repo", "Delta", "--json").Out, Does.Contain("\"id\": \"DE-T1\"").And.Contain("keep me"), "its own tickets, not a blank log");
        Assert.That(File.ReadAllText(Path.Combine(root, "Delta", "docs", "USER_STORIES.md")), Does.Contain("| DE-T1 |").And.Contain("GENERATED from"));
    }

    [Test]
    public void Rename_moves_a_repos_tickets_to_its_new_name_and_old_name_references_still_resolve()
    {
        MakeRepo("Old", "OL", "| OL-T1 | 2026-10-01 | fix | open | one | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Tickets", ".git"));
        Run("db", "migrate", "--cutover");

        var rename = Run("repos", "rename", "Old", "New", "--reason", "folder renamed");
        Assert.That(rename.Code, Is.Zero, rename.Out);
        Assert.That(rename.Out, Does.Contain("Old -> New").And.Contain("1 ticket(s)").And.Contain("backed up to"));

        Assert.That(Run("repos", "renames").Out, Does.Contain("Old").And.Contain("New").And.Contain("folder renamed"));
        Assert.That(Run("ticket", "show", "OL-T1", "--json").Out, Does.Contain("\"repo\": \"New\""));

        // A ticket command naming the old folder still finds the (now renamed) repo.
        Assert.That(Run("ticket", "new", "--repo", "Old", "--type", "fix", "--summary", "two").Out.Trim(), Is.EqualTo("OL-T2"));
        Assert.That(Run("db", "check").Code, Is.Zero);

        Assert.That(Run("repos", "rename", "Nope", "Whatever", "--reason", "x").Code, Is.Not.Zero);
    }

    [Test]
    public void Git_runs_never_prompt_and_a_hung_one_is_killed_at_its_timeout()
    {
        Git(root, "init", "-q");
        var env = Tickets.Git.Run(root, "-c", "alias.envcheck=!echo prompt=$GIT_TERMINAL_PROMPT", "envcheck");
        Assert.That(env.Output.Trim(), Is.EqualTo("prompt=0"), "a credential prompt would hang an agent");

        var started = DateTime.UtcNow;
        var hung = Tickets.Git.Run(root, TimeSpan.FromSeconds(1), "-c", "alias.hang=!sleep 30", "hang");
        Assert.That(hung.ExitCode, Is.EqualTo(-1));
        Assert.That(hung.Error, Does.Contain("timed out after 1s"));
        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(20)));
    }

    /// <summary>A committed repo whose origin is on GitHub, plus the fake GitHub index entry for it.</summary>
    private string MakeGitHubRepo(string name, bool archived = false, string topic = "", bool dirty = false)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "remote", "add", "origin", $"https://github.com/mindattic/{name}.git");
        File.WriteAllText(Path.Combine(dir, "README.md"), name);
        Git(dir, "add", "-A");
        Git(dir, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init");
        // A remote-tracking ref for the commit, so it isn't counted as unpushed.
        Git(dir, "update-ref", "refs/remotes/origin/main", "HEAD");
        if (dirty) File.WriteAllText(Path.Combine(dir, "draft.txt"), "only here");
        github.Add(new { full_name = $"mindattic/{name}", archived, topics = topic.Length == 0 ? Array.Empty<string>() : [topic] });
        File.WriteAllText(Path.Combine(root, "github.json"), JsonSerializer.Serialize(github));
        return dir;
    }

    private readonly List<object> github = [];

    [Test]
    public void Prune_previews_without_deleting_and_only_and_override_lists_are_trimmed_and_case_insensitive()
    {
        var old = MakeGitHubRepo("Old", archived: true);
        var stale = MakeGitHubRepo("Stale");
        var kept = MakeGitHubRepo("Kept", topic: "wip");
        var dirty = MakeGitHubRepo("Dirty", archived: true, dirty: true);

        var plan = Run("repos", "prune");
        Assert.That(plan.Code, Is.Zero, plan.Out);
        Assert.That(plan.Out, Does.Contain("3 candidate(s): 2 safe, 1 blocked").And.Contain("uncommitted").And.Not.Contain("Kept"));
        Assert.That(new[] { old, stale, kept, dirty }.All(Directory.Exists), "a preview deletes nothing");

        var apply = RunWithInput("yes\n", "repos", "prune", "--apply", "--only", " old , DIRTY", "--override", " dirty ");
        Assert.That(apply.Code, Is.Zero, apply.Out);
        Assert.That((Directory.Exists(old), Directory.Exists(dirty)), Is.EqualTo((false, false)), "named in --only (and --override for the blocked one)");
        Assert.That((Directory.Exists(stale), Directory.Exists(kept)), Is.EqualTo((true, true)), "safe but not in --only; not a candidate");
    }

    [Test]
    public void Prune_only_never_deletes_a_folder_that_a_repo_left_on_disk_reaches_into()
    {
        var old = MakeGitHubRepo("Old", archived: true);
        var stale = MakeGitHubRepo("Stale"); // untagged, so also safe to delete - but --only leaves it behind
        File.WriteAllText(Path.Combine(stale, "Stale.csproj"), """<Project><ItemGroup><ProjectReference Include="..\Old\Old.csproj" /></ItemGroup></Project>""");
        Git(stale, "add", "-A");
        Git(stale, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "ref");
        Git(stale, "update-ref", "refs/remotes/origin/main", "HEAD");

        var apply = RunWithInput("yes\n", "repos", "prune", "--apply", "--only", "Old");
        Assert.That(apply.Out, Does.Contain("Stale depends on it"));
        Assert.That(Directory.Exists(old), "Stale stays and reaches into Old by path");
    }

    [Test]
    public void Prune_apply_deletes_nothing_without_a_typed_yes()
    {
        var old = MakeGitHubRepo("Old", archived: true);
        var cancelled = RunWithInput("y\n", "repos", "prune", "--apply");
        Assert.That(cancelled.Code, Is.EqualTo(1));
        Assert.That(cancelled.Out, Does.Contain("Cancelled; nothing deleted."));
        Assert.That(Directory.Exists(old));
    }

    [Test]
    public void Prune_override_never_frees_a_repo_with_no_github_copy()
    {
        MakeGitHubRepo("Kept", topic: "wip");
        var local = Path.Combine(root, "LocalOnly");
        Directory.CreateDirectory(local);
        Git(local, "init", "-q", "-b", "main");
        Git(local, "remote", "add", "origin", "https://github.com/mindattic/LocalOnly.git"); // not in the index
        File.WriteAllText(Path.Combine(local, "draft.txt"), "only here");

        var refused = RunWithInput("yes\n", "repos", "prune", "--apply", "--override", "localonly");
        Assert.That(refused.Code, Is.Not.Zero);
        Assert.That(refused.Out, Does.Contain("can't override").And.Contain("no GitHub copy"));
        Assert.That(Directory.Exists(local), "nothing is deleted");
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
    }
}
