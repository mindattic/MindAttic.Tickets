using System.ComponentModel;
using MindAttic.Vault.Credentials;
using MindAttic.Workflow.Repos;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Workflow.Cli.Commands;

/// <summary>Options every command takes.</summary>
public class WorkspaceSettings : CommandSettings
{
    [CommandOption("--root <DIR>")]
    [Description("Workspace root holding every repo. Default: D:\\Projects\\MindAttic.")]
    public string Root { get; init; } = Workspace.DefaultRoot;

    public Workspace Workspace => new(Root);
}

/// <summary>Options for commands that act on one repo.</summary>
public class RepoSettings : WorkspaceSettings
{
    [CommandOption("--repo <NAME>")]
    [Description("Repo folder name. Default: the repo containing the current directory.")]
    public string? Repo { get; init; }

    public Repo ResolveRepo()
    {
        var ws = Workspace;
        if (!string.IsNullOrWhiteSpace(Repo))
            return ws.Find(Repo) ?? throw new InvalidOperationException($"No repo named '{Repo}' under {ws.Root}.");
        return ws.Containing(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException($"The current directory isn't inside a repo under {ws.Root}; pass --repo.");
    }
}

internal static class Output
{
    public static string Esc(string? s) => Markup.Escape(s ?? "");

    /// <summary>The GitHub index, or null (with a printed reason) when there's no token or the call fails.</summary>
    public static IReadOnlyDictionary<string, GitHubRepoInfo>? TryGitHub(bool required, IEnumerable<string>? localSlugs = null)
    {
        if (Environment.GetEnvironmentVariable(GitHubRepoIndex.FileEnvVar) is { Length: > 0 } file)
            return GitHubRepoIndex.Load(file);
        var token = TokenStore.ForBucket(GitHubRepoIndex.VaultBucket).Get(GitHubRepoIndex.VaultTokenName);
        if (string.IsNullOrWhiteSpace(token))
        {
            var msg = "No GitHub token in Vault (bucket 'GitHub', key 'github'); set it from the Launcher's Settings menu.";
            if (required) throw new InvalidOperationException(msg);
            AnsiConsole.MarkupLine($"[yellow]{Esc(msg)}[/] GitHub columns are blank.");
            return null;
        }
        try
        {
            var client = new GitHubRepoIndex();
            var index = client.FetchAsync(token).GetAwaiter().GetResult();
            // Local origins that predate a repo rename still resolve through GitHub's redirect.
            return localSlugs is null ? index : client.WithRenamesAsync(token, index, localSlugs).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (!required && ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[yellow]Couldn't read GitHub ({Esc(ex.Message)}).[/] GitHub columns are blank.");
            return null;
        }
    }
}
