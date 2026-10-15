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

    private (int Code, string Out) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "workflow.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root);
        psi.Environment.Remove("MINDATTIC_WORKFLOW_DB");
        psi.Environment["COLUMNS"] = "400";
        using var p = Process.Start(psi)!;
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
