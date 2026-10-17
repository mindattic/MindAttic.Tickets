using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace MindAttic.Workflow.Tests;

/// <summary>The ticket commands as an agent runs them: the real `workflow` against a temp --root workspace.</summary>
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
        Assert.That(rows.Select(x => x.Item2).Distinct(), Is.EqualTo(new[] { "MindAttic.Workflow" }));
        Assert.That(rows.Any(x => x.Item3.StartsWith("workflow ticket close AA-T1 ", StringComparison.Ordinal) && x.Item3.EndsWith("exited 0", StringComparison.Ordinal)));
        Assert.That(rows.Any(x => x.Item1 == 4 && x.Item3.StartsWith("workflow ticket close AA-T99", StringComparison.Ordinal) && x.Item3.Contains("failed", StringComparison.Ordinal)),
            "the failure is an Error row");
    }

    private (int Code, string Out) RunWithInput(string? input, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "workflow.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root);
        psi.Environment.Remove("MINDATTIC_WORKFLOW_DB");
        psi.Environment["COLUMNS"] = "400";
        psi.Environment["NO_COLOR"] = "1"; // a redirected stdin otherwise lets Spectre emit ANSI colors
        psi.Environment[Workflow.Repos.GitHubRepoIndex.FileEnvVar] = Path.Combine(root, "github.json");
        psi.Environment["MINDATTIC_WORKFLOW_LOG_DB"] = LogDb;
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
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
        var migrate = Run("db", "migrate", "--cutover");
        Assert.That(migrate.Code, Is.Zero, migrate.Out);

        Assert.That(Run("ticket", "new", "--repo", "A", "--type", "fix", "--summary", "two, blocked by AA-T1").Out.Trim(), Is.EqualTo("AA-T2"));
        Assert.That(Run("ticket", "close", "AA-T1", "--ref", "done").Out.Trim(), Is.EqualTo("AA-T1 closed"), "no --repo needed: IDs are unique");
        var show = JsonDocument.Parse(Run("ticket", "show", "AA-T2", "--json").Out).RootElement;
        Assert.That(show.GetProperty("requires").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(new[] { "AA-T1" }));
        var view = File.ReadAllText(Path.Combine(root, "A", "docs", "USER_STORIES.md"));
        Assert.That(view, Does.Contain("GENERATED from MindAttic.Workflow/data/workflow.db").And.Contain("| AA-T1 | 2026-10-01 | fix | closed | one | done |").And.Contain("| AA-T2 |"));
        Assert.That(Run("db", "check").Code, Is.Zero);
    }

    [Test]
    public void Correct_relabels_a_nonstandard_type_and_only_accepts_the_standard_vocabulary()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | bugfix | open | odd type | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
        Run("db", "migrate", "--cutover");

        var correct = Run("ticket", "correct", "AA-T1", "--type", "fix", "--reason", "bugfix predates the vocabulary");
        Assert.That(correct.Out.Trim(), Is.EqualTo("AA-T1 type fix"));
        Assert.That(Run("db", "check").Code, Is.Zero);
        Assert.That(Run("ticket", "correct", "AA-T1", "--type", "bugfix", "--reason", "x").Code, Is.Not.Zero, "only the standard vocabulary is accepted");
    }

    [Test]
    public void Link_connects_two_tickets_and_refuses_a_cycle()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | one | — |\n| AA-T2 | 2026-10-01 | fix | open | two | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
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
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
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
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
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
        var mw = Path.Combine(root, "MindAttic.Workflow");
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
        Assert.That(sync.Out, Does.Contain("Committed the workflow database: Sync the workflow database (AA-T1)"));
        Assert.That(Workflow.Git.Run(mw, "log", "-1", "--pretty=%s").Output.Trim(), Is.EqualTo("Sync the workflow database (AA-T1)"), "the subject names the ticket the commit carries");
        Assert.That(File.Exists(Path.Combine(mw, "data", "workflow.sql")));

        var log = Workflow.Git.Run(mw, "log", "-1", "--name-only", "--pretty=format:").Output;
        Assert.That(log, Does.Contain("data/workflow.db").And.Contain("data/workflow.sql").And.Not.Contain("scratch.txt"));

        Assert.That(Run("db", "sync", "--no-push").Out, Does.Contain("No changes to the workflow database since the last sync."));
    }

    [Test]
    public void View_writes_a_page_and_a_snapshot_of_every_ticket_and_story_with_markup_escaped()
    {
        MakeRepo("A", "AA", "| AA-T1 | 2026-10-01 | fix | open | close </script><b>x</b> & more | — |\n");
        MakeRepo("B", "BB", "| BB-T1 | 2026-10-02 | chore | closed | other | done |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
        Run("db", "migrate", "--cutover");

        var site = Path.Combine(root, "site");
        var view = Run("view", "--out", site, "--no-open", "--repo", "b");
        Assert.That((view.Code, view.Out.Trim()), Is.EqualTo((0, site)), view.Out);
        Assert.That(File.ReadAllText(Path.Combine(site, "index.html")), Does.Contain("<script src=\"data.js\"></script>"));

        var js = File.ReadAllText(Path.Combine(site, "data.js"));
        Assert.That(js, Does.StartWith("window.WORKFLOW = ").And.Not.Contain("</script>").And.Not.Contain("<b>"), "ticket text can't break out of the script");
        var data = JsonDocument.Parse(js["window.WORKFLOW = ".Length..].TrimEnd().TrimEnd(';')).RootElement;
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
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
        Run("db", "migrate", "--cutover");
        Directory.CreateDirectory(Path.Combine(root, "Gamma", ".git"));

        Assert.That(Run("repos", "rollout", "tickets").Out, Does.Contain("would Gamma: add to the workflow database"));
        Run("repos", "rollout", "tickets", "--apply");
        Assert.That(Run("ticket", "list", "--repo", "Gamma", "--json").Out, Does.Match("\"id\": \"[A-Z][A-Z0-9]*-T1\""), "seeded with its first ticket");
        Assert.That(File.ReadAllText(Path.Combine(root, "Gamma", "docs", "USER_STORIES.md")), Does.Contain("GENERATED from MindAttic.Workflow/data/workflow.db"));
        Assert.That(Run("repos", "rollout", "tickets").Out, Does.Contain("Every repo is already in the workflow database."));
    }

    [Test]
    public void Once_migrated_rollout_imports_a_new_repos_own_stories_file_instead_of_overwriting_it()
    {
        MakeRepo("Alpha", "AL", "| AL-T1 | 2026-10-01 | fix | open | one | — |\n");
        Directory.CreateDirectory(Path.Combine(root, "MindAttic.Workflow", ".git"));
        Run("db", "migrate", "--cutover");
        MakeRepo("Delta", "DE", "| DE-T1 | 2026-10-02 | feature | open | keep me | — |\n");

        Assert.That(Run("repos", "rollout", "tickets").Out, Does.Contain("would Delta: import its USER_STORIES.md"));
        Run("repos", "rollout", "tickets", "--apply");
        Assert.That(Run("ticket", "list", "--repo", "Delta", "--json").Out, Does.Contain("\"id\": \"DE-T1\"").And.Contain("keep me"), "its own tickets, not a blank log");
        Assert.That(File.ReadAllText(Path.Combine(root, "Delta", "docs", "USER_STORIES.md")), Does.Contain("| DE-T1 |").And.Contain("GENERATED from"));
    }

    [Test]
    public void Git_runs_never_prompt_and_a_hung_one_is_killed_at_its_timeout()
    {
        Git(root, "init", "-q");
        var env = Workflow.Git.Run(root, "-c", "alias.envcheck=!echo prompt=$GIT_TERMINAL_PROMPT", "envcheck");
        Assert.That(env.Output.Trim(), Is.EqualTo("prompt=0"), "a credential prompt would hang an agent");

        var started = DateTime.UtcNow;
        var hung = Workflow.Git.Run(root, TimeSpan.FromSeconds(1), "-c", "alias.hang=!sleep 30", "hang");
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
