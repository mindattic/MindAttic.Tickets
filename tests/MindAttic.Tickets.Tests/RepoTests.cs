using System.Net;
using System.Text;
using MindAttic.Tickets.Repos;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

[TestFixture]
public sealed class RepoStatusTests
{
    private string root = "";

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("wf-git-").FullName;

    [TearDown]
    public void TearDown() => PrunePlanner.DeleteFolder(root);

    private static void G(string repo, params string[] args)
    {
        var r = Git.Run(repo, ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. args]);
        Assert.That(r.Ok, Is.True, r.Error);
    }

    private Repo Init(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        G(path, "init", "-q");
        return new Repo(name, path);
    }

    [Test]
    public void Read_counts_uncommitted_unpushed_stashes_and_local_only_ignored_files()
    {
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote);
        G(remote, "init", "-q", "--bare");

        var repo = Init("Work");
        File.WriteAllText(Path.Combine(repo.Path, ".gitignore"), "bin/\ndrafts/\n");
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "1");
        G(repo.Path, "add", ".");
        G(repo.Path, "commit", "-q", "-m", "first");
        G(repo.Path, "remote", "add", "origin", remote);
        G(repo.Path, "push", "-q", "origin", "HEAD:refs/heads/main");
        G(repo.Path, "fetch", "-q", "origin");

        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "2");
        G(repo.Path, "commit", "-q", "-am", "local only");          // 1 unpushed
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "3");
        G(repo.Path, "stash", "-q");                                // 1 stash
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "x"); // 1 uncommitted
        Directory.CreateDirectory(Path.Combine(repo.Path, "bin"));
        File.WriteAllText(Path.Combine(repo.Path, "bin", "out.dll"), "x");    // regenerable: not counted
        Directory.CreateDirectory(Path.Combine(repo.Path, "drafts"));
        File.WriteAllText(Path.Combine(repo.Path, "drafts", "art.png"), "x"); // local-only

        var s = RepoStatus.Read(repo);

        Assert.That(s.Error, Is.Null);
        Assert.That((s.Uncommitted, s.Unpushed, s.Stashes), Is.EqualTo((1, 1, 1)));
        Assert.That(s.LocalOnlyIgnored, Is.EqualTo(new[] { "drafts/" }));
        Assert.That(s.HasLocalOnlyWork, Is.True);
    }

    [Test]
    public void Read_of_a_clean_pushed_repo_has_no_local_only_work()
    {
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote);
        G(remote, "init", "-q", "--bare");
        var repo = Init("Clean");
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "1");
        G(repo.Path, "add", ".");
        G(repo.Path, "commit", "-q", "-m", "first");
        G(repo.Path, "remote", "add", "origin", remote);
        G(repo.Path, "push", "-q", "origin", "HEAD:refs/heads/main");
        G(repo.Path, "fetch", "-q", "origin");

        Assert.That(RepoStatus.Read(repo).HasLocalOnlyWork, Is.False);
    }

    [Test]
    public void Read_counts_commits_on_a_detached_head_and_an_empty_repo_reads_cleanly()
    {
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote);
        G(remote, "init", "-q", "--bare");
        var repo = Init("Detached");
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "1");
        G(repo.Path, "add", ".");
        G(repo.Path, "commit", "-q", "-m", "first");
        G(repo.Path, "remote", "add", "origin", remote);
        G(repo.Path, "push", "-q", "origin", "HEAD:refs/heads/main");
        G(repo.Path, "fetch", "-q", "origin");
        // Work on a detached HEAD (a checked-out commit, an interrupted rebase) is on no branch and no remote.
        G(repo.Path, "checkout", "-q", "--detach");
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "2");
        G(repo.Path, "commit", "-q", "-am", "only on the detached head");

        var s = RepoStatus.Read(repo);
        Assert.That((s.Error, s.Unpushed), Is.EqualTo(((string?)null, 1)));
        Assert.That(s.HasLocalOnlyWork, Is.True, "deleting the folder would lose that commit");

        var empty = RepoStatus.Read(Init("Empty")); // no commits yet: HEAD doesn't resolve
        Assert.That((empty.Error, empty.Unpushed), Is.EqualTo(((string?)null, 0)));
    }

    [Test]
    public void Read_counts_uncommitted_work_in_the_repos_linked_worktrees()
    {
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote);
        G(remote, "init", "-q", "--bare");
        var repo = Init("Main");
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "1");
        G(repo.Path, "add", ".");
        G(repo.Path, "commit", "-q", "-m", "first");
        G(repo.Path, "remote", "add", "origin", remote);
        G(repo.Path, "push", "-q", "origin", "HEAD:refs/heads/main");
        G(repo.Path, "fetch", "-q", "origin");
        // A worktree elsewhere (as agents use) on a pushed commit, with an edit only there: deleting Main deletes the
        // .git that worktree lives on.
        var wt = Path.Combine(root, "worktrees", "Main-T1");
        G(repo.Path, "worktree", "add", "-q", "--detach", wt);
        File.WriteAllText(Path.Combine(wt, "a.txt"), "edited in the worktree");

        var s = RepoStatus.Read(repo);
        Assert.That((s.Error, s.Uncommitted), Is.EqualTo(((string?)null, 1)));
        Assert.That(s.HasLocalOnlyWork, Is.True);
    }

    [TestCase("https://github.com/mindattic/MindAttic.Launcher.git", "mindattic/mindattic.launcher")]
    [TestCase("https://github.com/mindattic/Tutor", "mindattic/tutor")]
    [TestCase("git@github.com:Someone/Repo.git", "someone/repo")]
    [TestCase("https://gitlab.com/x/y.git", null)]
    [TestCase(null, null)]
    public void GitHubSlugFrom_handles_https_ssh_and_non_github(string? url, string? expected)
    {
        Assert.That(RepoStatus.GitHubSlugFrom(url), Is.EqualTo(expected));
    }

    [TestCase("bin/", true)]
    [TestCase("src/App/obj/", true)]
    [TestCase("Library/", true)]
    [TestCase("node_modules/", true)]
    [TestCase("Gaiden.Core.csproj", true)]
    [TestCase(".claude/settings.local.json", true)]
    [TestCase(".claude/scheduled_tasks.lock", true)]
    [TestCase("content-drafts/", false)]
    [TestCase("secrets/ftp.json", false)]
    public void IsRegenerable_separates_build_output_from_local_data(string path, bool expected)
    {
        Assert.That(RepoStatus.IsRegenerable(path), Is.EqualTo(expected));
    }
}

[TestFixture]
public sealed class GitHubRepoIndexTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Page(IEnumerable<string> items) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"[{string.Join(",", items)}]", Encoding.UTF8, "application/json"),
    };

    private static string Repo(string name, bool archived, params string[] topics) =>
        $"{{\"full_name\":\"{name}\",\"archived\":{(archived ? "true" : "false")},\"topics\":[{string.Join(",", topics.Select(t => $"\"{t}\""))}]}}";

    [Test]
    public async Task FetchAsync_paginates_and_records_archived_and_topics_by_lowercased_slug()
    {
        using var handler = new FakeHandler(req => req.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal)
            ? Page(Enumerable.Range(0, 100).Select(i => Repo($"mindattic/R{i}", false)))
            : Page([Repo("mindattic/Shelved", true, "work-in-progress"), Repo("mindattic/Live", false, "work-in-progress", "dotnet")]));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };

        var index = await new GitHubRepoIndex(http).FetchAsync("token");

        Assert.That(handler.Calls, Is.EqualTo(2));
        Assert.That(index, Has.Count.EqualTo(102));
        Assert.That(index["mindattic/shelved"].Archived, Is.True);
        Assert.That(index["MindAttic/Live"].HasTopic("WORK-IN-PROGRESS"), Is.True);
        Assert.That(index["mindattic/r0"].Topics, Is.Empty);
    }

    [Test]
    public async Task WithRenamesAsync_resolves_a_stale_origin_through_githubs_redirect_and_skips_true_404s()
    {
        using var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/repos/mindattic/cryptography" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Repo("mindattic/MindAttic.Cryptography", false, "work-in-progress"), Encoding.UTF8, "application/json"),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var index = new Dictionary<string, GitHubRepoInfo> { ["mindattic/live"] = new("mindattic/live", false, []) };

        var result = await new GitHubRepoIndex(http).WithRenamesAsync("token", index, ["mindattic/live", "mindattic/cryptography", "mindattic/gone"]);

        Assert.That(handler.Calls, Is.EqualTo(2), "already-indexed slugs aren't looked up");
        Assert.That(result["mindattic/cryptography"].Slug, Is.EqualTo("mindattic/mindattic.cryptography"));
        Assert.That(result["mindattic/cryptography"].HasTopic("work-in-progress"), Is.True);
        Assert.That(result.ContainsKey("mindattic/gone"), Is.False);
    }

    [Test]
    public void FetchAsync_throws_on_a_non_success_response()
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };

        Assert.ThrowsAsync<InvalidOperationException>(() => new GitHubRepoIndex(http).FetchAsync("bad"));
    }
}

[TestFixture]
public sealed class DependencyScanTests
{
    private string root = "";

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("wf-deps-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private Repo Make(string name)
    {
        var p = Path.Combine(root, name);
        Directory.CreateDirectory(p);
        return new Repo(name, p);
    }

    private static void Put(Repo r, string rel, string text)
    {
        var p = Path.Combine(r.Path, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Test]
    public void Scan_finds_unity_manifest_project_reference_and_slnx_paths_into_sibling_repos()
    {
        var game = Make("GridGameGaiden");
        var telemetry = Make("MindAttic.Telemetry");
        var lib = Make("Lib");
        var app = Make("App");
        Put(game, "Packages/manifest.json", """{ "dependencies": { "com.mindattic.telemetry": "file:../../MindAttic.Telemetry/unity/pkg", "com.unity.ugui": "2.0.0" } }""");
        Put(app, "src/App/App.csproj", """<Project><ItemGroup><ProjectReference Include="..\..\..\Lib\Lib.csproj" /></ItemGroup></Project>""");
        Put(app, "App.slnx", """<Solution><Project Path="src/App/App.csproj" /><Project Path="../Lib/Lib.csproj" /></Solution>""");
        Put(app, "bin/Stale.csproj", """<ProjectReference Include="..\..\Lib\x.csproj" />""");   // skipped: build output

        var deps = DependencyScan.Scan([game, telemetry, lib, app]);

        Assert.That(deps.Select(d => (d.FromRepo, d.ToRepo, d.File.Replace('\\', '/'))), Is.EquivalentTo(new[]
        {
            ("GridGameGaiden", "MindAttic.Telemetry", "Packages/manifest.json"),
            ("App", "Lib", "src/App/App.csproj"),
            ("App", "Lib", "App.slnx"),
        }));
    }

    [Test]
    public void Scan_finds_a_project_reference_or_import_with_an_attribute_before_include_or_project()
    {
        // MSBuild doesn't fix attribute order: Condition (or anything else) commonly comes before Include/Project.
        var lib = Make("Lib");
        var app = Make("App");
        Put(app, "App.csproj",
            """<Project><ItemGroup><ProjectReference Condition="'$(Configuration)'=='Debug'" Include="..\Lib\Lib.csproj" /></ItemGroup>""" +
            """<Import Condition="Exists('..\Lib\x.props')" Project="..\Lib\x.props" /></Project>""");

        var deps = DependencyScan.Scan([lib, app]);

        Assert.That(deps.Select(d => (d.FromRepo, d.ToRepo, d.File)), Is.EquivalentTo(new[]
        {
            ("App", "Lib", "App.csproj"),
            ("App", "Lib", "App.csproj"),
        }));
    }
}

[TestFixture]
public sealed class PrunePlannerTests
{
    private static RepoStatus S(string name, string? slug, int dirty = 0, int unpushed = 0, int stash = 0, string[]? ignored = null) =>
        new(new Repo(name, $@"X:\ws\{name}"), slug is null ? null : $"https://github.com/{slug}", slug, dirty, unpushed, stash, ignored ?? [], null);

    private static readonly Dictionary<string, GitHubRepoInfo> GitHub = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mindattic/live"] = new("mindattic/live", false, ["wip"]),
        ["mindattic/old"] = new("mindattic/old", true, ["wip"]),
        ["mindattic/untagged"] = new("mindattic/untagged", false, ["dotnet"]),
        ["mindattic/dirty"] = new("mindattic/dirty", true, []),
        ["mindattic/telemetry"] = new("mindattic/telemetry", false, []),
    };

    [Test]
    public void Plan_keeps_tagged_repos_and_marks_clean_archived_or_untagged_ones_safe()
    {
        var plan = PrunePlanner.Plan([S("Live", "mindattic/live"), S("Old", "mindattic/old"), S("Untagged", "mindattic/untagged")], GitHub, []);

        Assert.That(plan.Select(c => (c.Status.Repo.Name, c.Reason, c.Safe)), Is.EqualTo(new[]
        {
            ("Old", "archived on GitHub", true),
            ("Untagged", "not tagged 'wip'", true),
        }));
    }

    [Test]
    public void Plan_blocks_on_local_only_work_missing_github_copy_and_kept_dependents()
    {
        var plan = PrunePlanner.Plan(
            [
                S("Dirty", "mindattic/dirty", dirty: 2, unpushed: 1, stash: 1, ignored: ["content-drafts/"]),
                S("Gone", "mindattic/gone"),
                S("NoRemote", null),
                S("MindAttic.Telemetry", "mindattic/telemetry"),
                S("GridGameGaiden", "mindattic/live"),
            ],
            GitHub,
            [new FolderDependency("GridGameGaiden", "Packages/manifest.json", "MindAttic.Telemetry", "file:../../MindAttic.Telemetry")]);

        var byName = plan.ToDictionary(c => c.Status.Repo.Name);
        Assert.That(plan.All(c => !c.Safe), Is.True);
        Assert.That(byName["Dirty"].Blockers, Has.Count.EqualTo(4));
        Assert.That(byName["Dirty"].Overridable, Is.True, "local work alone can be knowingly given up");
        Assert.That(byName["Gone"].Blockers, Is.EqualTo(new[] { new PruneBlocker(BlockerKind.NoGitHubCopy, "no GitHub copy to restore from") }));
        Assert.That(byName["Gone"].Overridable, Is.False);
        Assert.That(byName["NoRemote"].Reason, Is.EqualTo("no origin remote"));
        Assert.That(byName["MindAttic.Telemetry"].Blockers.Single().Text, Does.StartWith("GridGameGaiden depends on it"));
        Assert.That(byName["MindAttic.Telemetry"].Overridable, Is.False, "a kept dependent can never be overridden");
    }

    [Test]
    public void Plan_does_not_block_on_a_dependent_that_is_itself_being_pruned()
    {
        var plan = PrunePlanner.Plan(
            [S("Old", "mindattic/old"), S("Untagged", "mindattic/untagged")],
            GitHub,
            [new FolderDependency("Old", "x.csproj", "Untagged", "../Untagged/x.csproj")]);

        Assert.That(plan.All(c => c.Safe), Is.True);
    }

    [Test]
    public void Plan_blocks_a_candidate_depended_on_by_another_candidate_that_ends_up_staying()
    {
        // "Blocked" is an archived candidate, but its own uncommitted work means it isn't actually going anywhere;
        // its dependency on "DependedOn" (also a candidate) must therefore block "DependedOn" too, the same as a
        // kept repo's dependency would - not be waved through just because "Blocked" was an initial candidate.
        var plan = PrunePlanner.Plan(
            [S("Blocked", "mindattic/old", dirty: 1), S("DependedOn", "mindattic/untagged")],
            GitHub,
            [new FolderDependency("Blocked", "x.csproj", "DependedOn", "../DependedOn/x.csproj")]);

        var byName = plan.ToDictionary(c => c.Status.Repo.Name);
        Assert.That(byName["Blocked"].Safe, Is.False, "has its own uncommitted work");
        Assert.That(byName["DependedOn"].Blockers.Select(b => b.Kind), Does.Contain(BlockerKind.Dependent));
        Assert.That(byName["DependedOn"].Overridable, Is.False, "a dependent that is actually staying can never be overridden");
    }
}
