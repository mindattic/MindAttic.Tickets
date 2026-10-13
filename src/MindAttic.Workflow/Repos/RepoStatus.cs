using System.Text.RegularExpressions;

namespace MindAttic.Workflow.Repos;

/// <summary>
/// What a repo holds that exists only on this machine. Anything non-zero here is work a folder
/// delete would lose, so <see cref="PrunePlanner"/> treats it as a blocker.
/// </summary>
public sealed partial record RepoStatus(
    Repo Repo,
    string? OriginUrl,
    string? GitHubSlug,
    int Uncommitted,
    int Unpushed,
    int Stashes,
    IReadOnlyList<string> LocalOnlyIgnored,
    string? Error)
{
    public bool HasLocalOnlyWork => Uncommitted > 0 || Unpushed > 0 || Stashes > 0 || LocalOnlyIgnored.Count > 0;

    /// <summary>Reads the status of <paramref name="repo"/> via git.</summary>
    public static RepoStatus Read(Repo repo)
    {
        var origin = Git.Run(repo.Path, "remote", "get-url", "origin");
        var url = origin.Ok ? origin.Output.Trim() : null;

        var status = Git.Run(repo.Path, "status", "--porcelain");
        if (!status.Ok)
            return new RepoStatus(repo, url, GitHubSlugFrom(url), 0, 0, 0, [], status.Error.Trim());

        var unpushed = Git.Run(repo.Path, "log", "--branches", "--not", "--remotes", "--oneline");
        var stashes = Git.Run(repo.Path, "stash", "list");
        var ignored = Git.Run(repo.Path, "ls-files", "--others", "--ignored", "--exclude-standard", "--directory");

        return new RepoStatus(
            repo, url, GitHubSlugFrom(url),
            status.Lines.Count,
            unpushed.Ok ? unpushed.Lines.Count : 0,
            stashes.Ok ? stashes.Lines.Count : 0,
            ignored.Ok ? [.. ignored.Lines.Where(p => !IsRegenerable(p))] : [],
            null);
    }

    /// <summary>
    /// Ignored paths that a build, restore or editor regenerates (bin, obj, Unity Library, node_modules…)
    /// and so aren't worth protecting. Everything else ignored — scratch art, local config, drafts — is
    /// treated as local-only data.
    /// </summary>
    public static bool IsRegenerable(string path) => Regenerable().IsMatch(path.Replace('\\', '/'));

    [GeneratedRegex(@"(^|/)(bin|obj|Library|Temp|Logs|UserSettings|node_modules|\.vs|\.idea|packages|artifacts|dist|build|Build|\.venv|venv|__pycache__|TestResults|\.gradle|\.next)(/|$)|\.(csproj|sln|user|suo|log)$|(^|/)\.claude/(settings\.local\.json|scheduled_tasks\.lock|quicksave\.md.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Regenerable();

    /// <summary><c>owner/repo</c> (lower-cased) for a github.com HTTPS or SSH remote; null otherwise.</summary>
    public static string? GitHubSlugFrom(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = GitHubRemote().Match(url.Trim());
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}".ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"github\.com[/:]([^/]+)/([^/]+?)(\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRemote();
}
