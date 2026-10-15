using System.Net.Http.Headers;
using System.Text.Json;

namespace MindAttic.Workflow.Repos;

/// <summary>What GitHub says about one repo.</summary>
public sealed record GitHubRepoInfo(string Slug, bool Archived, IReadOnlyList<string> Topics)
{
    public bool HasTopic(string topic) => Topics.Contains(topic, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One paginated sweep of <c>GET /user/repos</c>: every repo the token can see, keyed by
/// lower-cased <c>owner/repo</c>. The caller supplies the token from MindAttic.Vault (bucket
/// <see cref="VaultBucket"/>, key <see cref="VaultTokenName"/> — the one the Launcher's roster
/// filter uses), so credentials never live in this repo (HOUSE-LAW-3) and the library itself
/// has no package dependencies.
/// </summary>
public sealed class GitHubRepoIndex(HttpClient? http = null)
{
    public const string WorkInProgressTopic = "wip";
    public const string VaultBucket = "GitHub";
    public const string VaultTokenName = "github";

    /// <summary>
    /// Set to a JSON file of <c>/user/repos</c> objects (<c>full_name</c>, <c>archived</c>, <c>topics</c>)
    /// to read the index from it instead of GitHub, so the repo jobs can be run offline.
    /// </summary>
    public const string FileEnvVar = "MINDATTIC_WORKFLOW_GITHUB_INDEX";
    private const int PageSize = 100;

    private readonly HttpClient http = http ?? Shared.Value;

    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MindAttic.Workflow");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    });

    /// <summary>Throws <see cref="InvalidOperationException"/> on a non-success response.</summary>
    public async Task<IReadOnlyDictionary<string, GitHubRepoInfo>> FetchAsync(string token, CancellationToken ct = default)
    {
        var result = new Dictionary<string, GitHubRepoInfo>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"user/repos?per_page={PageSize}&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub API returned {(int)response.StatusCode} listing repos.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var count = 0;
            foreach (var repo in doc.RootElement.EnumerateArray())
            {
                count++;
                if (Parse(repo) is { } info) result[info.Slug] = info;
            }

            if (count < PageSize) break;
        }
        return result;
    }

    /// <summary>
    /// Looks up one repo by a possibly-stale <c>owner/repo</c>. GitHub redirects a renamed repo's old
    /// name to the new one, so a local <c>origin</c> that predates a rename still resolves here (and
    /// the returned <see cref="GitHubRepoInfo.Slug"/> is the current name). Null on 404.
    /// </summary>
    public async Task<GitHubRepoInfo?> LookupAsync(string token, string slug, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{slug}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub API returned {(int)response.StatusCode} looking up {slug}.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    /// <summary>
    /// Adds an entry under each slug in <paramref name="slugs"/> that the sweep didn't find but a
    /// direct lookup resolves (a renamed repo). Slugs that still don't resolve are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GitHubRepoInfo>> WithRenamesAsync(
        string token, IReadOnlyDictionary<string, GitHubRepoInfo> index, IEnumerable<string> slugs, CancellationToken ct = default)
    {
        var result = new Dictionary<string, GitHubRepoInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in index) result[k] = v;
        foreach (var slug in slugs.Distinct(StringComparer.OrdinalIgnoreCase).Where(s => !result.ContainsKey(s)).ToList())
            if (await LookupAsync(token, slug, ct).ConfigureAwait(false) is { } info)
                result[slug] = info;
        return result;
    }

    /// <summary>The index from a JSON array in the shape <c>GET /user/repos</c> returns.</summary>
    public static IReadOnlyDictionary<string, GitHubRepoInfo> Load(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var result = new Dictionary<string, GitHubRepoInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in doc.RootElement.EnumerateArray())
            if (Parse(repo) is { } info) result[info.Slug] = info;
        return result;
    }

    private static GitHubRepoInfo? Parse(JsonElement repo)
    {
        if (!repo.TryGetProperty("full_name", out var nameEl) || nameEl.GetString() is not { } name) return null;
        var archived = repo.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
        var topics = new List<string>();
        if (repo.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var topic in t.EnumerateArray())
                if (topic.GetString() is { Length: > 0 } s) topics.Add(s);
        return new GitHubRepoInfo(name.ToLowerInvariant(), archived, topics);
    }
}
