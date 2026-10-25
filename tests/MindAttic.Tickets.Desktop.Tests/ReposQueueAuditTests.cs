using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The Repos | Queue | Audit tabs (M.TIX-T135): each repo's heading with its counts, git state and Start link; each
/// claimed ticket's live worker in the Queue, read from the Orchestrator's database and opening the Orchestrator on it;
/// and the Orchestrator's audit findings, all read without writing the Orchestrator's database.
/// </summary>
public sealed class ReposQueueAuditTests
{
    private string root = "";
    private string orchDb = "";
    private MainViewModel model = null!;
    private readonly List<string[]> launched = [];

    [SetUp]
    public async Task SetUp()
    {
        launched.Clear();
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-tabs-" + Guid.NewGuid().ToString("N"));
        var alpha = MakeRepo("Alpha", "AL",
            "| AL-T1 | 2026-10-01 | feature | open | alpha feature | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha fix | — |\n");
        var beta = MakeRepo("Beta", "BE",
            "| BE-T1 | 2026-10-04 | feature | open | beta feature | — |\n| BE-T2 | 2026-10-05 | fix | open | beta fix | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([new Repo("Alpha", alpha), new Repo("Beta", beta)]);
            db.AddLink("BE-T2", "AL-T2", "requires");
        }

        orchDb = Path.Combine(root, "MindAttic.Orchestrator", "data", "orchestrator.db");
        MakeOrchestratorDb(orchDb);
        model = new MainViewModel(new TicketStore(root), new DesktopSettings(), orchestrator: new OrchestratorState(orchDb))
        {
            LaunchOrchestrator = args => { launched.Add([.. args]); return null; },
            OpenOrchestrator = target => { launched.Add([.. OrchestratorLink.Arguments(target)]); return null; },
            ReadGit = dir => new RepoGitState("main", Path.GetFileName(dir) == "Alpha" ? 2 : 0, 0, 0),
        };
        await model.RefreshAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        for (var attempt = 1; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50); }
        }
    }

    private string MakeRepo(string name, string code, string rows)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            $"---\ncodex: 1\nproject: {name}\ncode: {code}\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# {name} — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" + rows);
        return dir;
    }

    /// <summary>The Orchestrator's tables this window reads, as its schema has them (assignments, members, state).</summary>
    private static void MakeOrchestratorDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE members (email TEXT PRIMARY KEY, name TEXT, role TEXT NOT NULL, tier TEXT NOT NULL, status TEXT NOT NULL, joined_at REAL NOT NULL, updated_at REAL NOT NULL);
            CREATE TABLE assignments (position INTEGER PRIMARY KEY, ticket_id TEXT, repo TEXT, directory TEXT, provider TEXT, started_utc TEXT, worktree INTEGER,
                pilot INTEGER, pilot_started_utc TEXT, log TEXT, cli INTEGER, cli_session TEXT, model TEXT, reviewed_utc TEXT, account TEXT);
            CREATE TABLE state (name TEXT PRIMARY KEY, value TEXT NOT NULL, written_at REAL NOT NULL);
            INSERT INTO members VALUES ('ryan@example.com', 'Ryan', 'owner', 'premium', 'active', 0, 0);
            INSERT INTO assignments (position, ticket_id, repo, directory, provider, started_utc, worktree, cli_session, model, account)
                VALUES (0, 'AL-T2', 'Alpha', 'D:\wt\AL-T2', 'Claude', '2026-10-23T12:00:00Z', 1, 'sess-1', 'opus', 'Ryan@Example.com');
            INSERT INTO state VALUES ('orphaned-tickets', '[{"TicketId":"BE-T1","Repo":"Beta","State":"orphaned","Why":"its CLI exited","Open":true},{"TicketId":"BE-T2","State":"redispatched"}]', 0);
            INSERT INTO state VALUES ('audit', '{"RanUtc":"2026-10-23T22:16:21Z","Took":"00:00:32","ReposScanned":2,"FilesScanned":40,
                "Drift":[{"File":"AGENTS.md","Repo":"Alpha","Path":"D:\\x\\Alpha\\AGENTS.md","CanonicalPath":"D:\\x\\Beta\\AGENTS.md","CanonicalCount":1,"Total":2,"Variants":2,"Similarity":0.9,"IsCustom":false}],
                "Missing":[{"File":"CLAUDE.md","Repo":"Beta","HaveIt":1,"Eligible":2,"ExamplePath":"D:\\x\\Alpha\\CLAUDE.md"}],
                "Duplicates":[{"Files":[{"Repo":"Alpha","Path":"a.cs","Lines":40},{"Repo":"Beta","Path":"b.cs","Lines":41}],"Identical":false,"Similarity":0.95,"SharedLines":38,"SuggestedHome":"MindAttic.Shared"}]}', 0);
            """;
        cmd.ExecuteNonQuery();
    }

    private TicketRowViewModel QueueRow(string id) => model.QueueRows.Single(r => r.Id == id);

    [Test]
    public void The_tabs_are_Repos_Queue_Audit_and_the_old_Tickets_view_opens_as_Repos()
    {
        Assert.That(model.Views, Is.EqualTo(new[] { "Repos", "Queue", "Audit", "Board", "Activity", "Stats", "Stories" }));
        var old = new MainViewModel(new TicketStore(root), new DesktopSettings { View = "Tickets" }, orchestrator: new OrchestratorState(orchDb));
        Assert.That(old.SelectedView, Is.EqualTo(MainViewModel.ReposView));
        Assert.That(old.IsTicketsView, Is.True);
    }

    [Test]
    public void A_claimed_ticket_shows_its_person_login_and_model()
    {
        var w = QueueRow("AL-T2").Worker;
        Assert.That(w, Is.Not.Null);
        Assert.That((w!.Person, w.Login, w.Model, w.Orphaned), Is.EqualTo(("Ryan", "Ryan@Example.com", "opus", false)));
        Assert.That(QueueRow("AL-T2").WorkerText, Does.Contain("Ryan").And.Contain("opus"));
        Assert.That(QueueRow("AL-T1").Worker, Is.Null, "nobody has claimed it");
        Assert.That(model.Headline, Does.Contain("1 being worked"));
    }

    [Test]
    public void A_claimed_row_shows_its_worktree_and_since_when()
    {
        var row = QueueRow("AL-T2");
        Assert.That(row.Worker!.Worktree, Is.EqualTo("AL-T2"));
        Assert.That(row.WorkerText, Does.Contain("in AL-T2").And.Contain("since "));
        Assert.That(row.WorkerTip, Does.Contain(@"Worktree D:\wt\AL-T2").And.Contain("Since 2026-10-2").And.Contain("Session sess-1"));
        Assert.That(model.TicketRows.Single(r => r.Id == "AL-T2").WorkerText, Is.EqualTo(row.WorkerText), "the Repos tab's row shows it too");
    }

    [Test]
    public async Task The_detail_pane_shows_the_claims_agent_worktree_and_since()
    {
        Assert.That(await model.OpenTicketAsync("AL-T2"), Is.True);
        var d = model.Detail!;
        Assert.That(d.HasWorker, Is.True);
        Assert.That(d.ClaimText, Is.EqualTo("Claimed by Ryan (Ryan@Example.com) on opus."));
        Assert.That(d.ClaimWorktree, Is.EqualTo(@"D:\wt\AL-T2"));
        Assert.That(d.ClaimSince, Does.StartWith("2026-10-2").And.Contain(" ago)"));
        Assert.That(d.ClaimSession, Is.EqualTo("sess-1"));
        Assert.That(d.ClaimSpoken, Does.Contain("Worktree").And.Contain("Since"));

        Assert.That(await model.OpenTicketAsync("AL-T1"), Is.True);
        Assert.That((model.Detail!.HasWorker, model.Detail.ClaimText), Is.EqualTo((false, "")), "nobody has claimed it");

        Assert.That(await model.OpenTicketAsync("BE-T1"), Is.True);
        Assert.That(model.Detail!.ClaimText, Does.StartWith("Orphaned").And.Contain("its CLI exited"));
    }

    [Test]
    public void An_orphaned_ticket_says_so_and_only_orphaned_entries_count()
    {
        Assert.That(QueueRow("BE-T1").Worker, Is.Not.Null.And.Property(nameof(Worker.Orphaned)).True);
        Assert.That(QueueRow("BE-T1").WorkerText, Does.Contain("orphaned"));
        Assert.That(QueueRow("BE-T1").WorkerTip, Does.Contain("its CLI exited"));
        Assert.That(QueueRow("BE-T2").Worker, Is.Null, "redispatched, not orphaned");
    }

    [Test]
    public void The_worker_link_opens_the_Orchestrator_on_the_ticket()
    {
        var row = QueueRow("AL-T2");
        Assert.That(model.ShowWorkerCommand.CanExecute(row), Is.True);
        Assert.That(model.ShowWorkerCommand.CanExecute(QueueRow("AL-T1")), Is.False);

        model.ShowWorkerCommand.Execute(row);

        Assert.That(launched.Single(), Is.EqualTo(new[] { "--show", "AL-T2" }));
        Assert.That(model.StatusMessage, Does.Contain("AL-T2"));
    }

    [Test]
    public void A_failed_launch_is_shown_not_thrown()
    {
        model.LaunchOrchestrator = _ => "The Orchestrator isn't installed.";
        model.StartCliCommand.Execute("Alpha");
        Assert.That(model.StatusMessage, Is.EqualTo("The Orchestrator isn't installed."));
    }

    [Test]
    public async Task Each_repo_heading_has_its_counts_git_state_and_a_Start_link()
    {
        model.SelectedView = MainViewModel.ReposView;
        await model.RefreshGitAsync(force: true);

        var alpha = model.TicketRows.First(r => r.Repo == "Alpha").RepoHeader!;
        var beta = model.TicketRows.First(r => r.Repo == "Beta").RepoHeader!;
        Assert.That(model.TicketRows.Where(r => r.Repo == "Alpha").Select(r => r.RepoHeader), Is.All.SameAs(alpha), "one heading per repo, the list's group");
        Assert.That(alpha.Counts, Is.EqualTo("1 queued · 1 needs approval"));
        Assert.That(beta.Counts, Is.EqualTo("0 queued · 1 needs approval · 1 blocked"));
        Assert.That(alpha.Git, Is.EqualTo("main · 2 ahead"));
        Assert.That(beta.Git, Is.EqualTo("main · clean"));

        model.StartCliCommand.Execute(alpha);
        Assert.That(launched.Single(), Is.EqualTo(new[] { "--start", "Alpha" }));
    }

    [Test]
    public void Audit_shows_the_Orchestrator_findings_narrowed_by_repo_and_search()
    {
        model.SelectedView = MainViewModel.AuditView;

        Assert.That(model.DriftRows.Single().File, Is.EqualTo("AGENTS.md"));
        Assert.That(model.MissingRows.Single().Repo, Is.EqualTo("Beta"));
        Assert.That(model.DuplicateRows.Single().Repos, Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(model.AuditSummary, Does.Contain("2 repos").And.Contain("1 drifted"));
        Assert.That(model.ShowsTicketFilters, Is.False);

        model.RepoFilter = "Beta";
        Assert.That(model.DriftRows, Is.Empty);
        Assert.That(model.MissingRows, Has.Count.EqualTo(1));
        Assert.That(model.DuplicateRows, Has.Count.EqualTo(1), "a duplicate group touching Beta");

        model.RepoFilter = MainViewModel.AllRepos;
        model.Search = "claude.md";
        Assert.That((model.DriftRows.Count, model.MissingRows.Count, model.DuplicateRows.Count), Is.EqualTo((0, 1, 0)));
    }

    [Test]
    public async Task The_Orchestrator_database_is_only_read()
    {
        SqliteConnection.ClearAllPools();
        var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(orchDb)));
        await model.RefreshAsync();
        SqliteConnection.ClearAllPools();
        Assert.That(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(orchDb))), Is.EqualTo(before));
        Assert.That(File.Exists(orchDb + "-journal") || File.Exists(orchDb + "-wal"), Is.False);
    }

    [Test]
    public async Task No_Orchestrator_database_is_no_workers_and_a_note()
    {
        var bare = new MainViewModel(new TicketStore(root), new DesktopSettings(), orchestrator: new OrchestratorState(Path.Combine(root, "nowhere.db")));
        await bare.RefreshAsync();
        bare.SelectedView = MainViewModel.AuditView;

        Assert.That(bare.QueueRows.All(r => r.Worker is null), Is.True);
        Assert.That(bare.OrchestratorNote, Does.Contain("No Orchestrator database"));
        Assert.That(bare.AuditSummary, Does.Contain("No Orchestrator database"));
    }

    [TestCase("## main...origin/main [ahead 2, behind 1]\n M a.cs\n?? b.cs\n", "main", 2, 1, 2)]
    [TestCase("## main...origin/main\n", "main", 0, 0, 0)]
    [TestCase("## feature/x\n", "feature/x", 0, 0, 0)]
    [TestCase("## main...origin/main [behind 3]\n", "main", 0, 3, 0)]
    [TestCase("## main...origin/main [gone]\n", "main", 0, 0, 0)]
    [TestCase("## HEAD (no branch)\n M a.cs\n", null, 0, 0, 1)]
    [TestCase("## No commits yet on main\n", "main", 0, 0, 0)]
    public void Git_status_is_parsed(string output, string? branch, int ahead, int behind, int changed)
    {
        var s = RepoGitState.Parse(output);
        Assert.That((s.Branch, s.Ahead, s.Behind, s.Changed), Is.EqualTo((branch, ahead, behind, changed)));
    }
}
