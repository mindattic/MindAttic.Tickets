namespace MindAttic.Tickets;

/// <summary>A git repo directly under the workspace root.</summary>
public sealed record Repo(string Name, string Path)
{
    /// <summary>Full path of this repo's <c>docs/USER_STORIES.md</c> (which may not exist).</summary>
    public string StoriesPath => System.IO.Path.Combine(Path, "docs", "USER_STORIES.md");
}

/// <summary>
/// The MindAttic workspace: every git repo that is an immediate subdirectory of the root (a
/// <c>.git</c> directory, or a <c>.git</c> file for worktrees) — the same rule the Launcher's
/// project discovery uses.
/// </summary>
public sealed class Workspace(string root)
{
    public const string DefaultRoot = @"D:\Projects\MindAttic";

    public string Root { get; } = System.IO.Path.GetFullPath(root);

    public IReadOnlyList<Repo> Repos()
    {
        if (!Directory.Exists(Root)) return [];
        return [.. Directory.GetDirectories(Root)
            .Where(IsRepo)
            .Select(d => new Repo(System.IO.Path.GetFileName(d), d))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The repo (by folder name, case-insensitive), or null.</summary>
    public Repo? Find(string name) =>
        Repos().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The workspace repo containing <paramref name="directory"/> (walking up), or null when the
    /// directory isn't inside one. Used so <c>--repo</c> can default to "the repo I'm standing in".
    /// </summary>
    public Repo? Containing(string directory)
    {
        var full = System.IO.Path.GetFullPath(directory).TrimEnd('\\', '/');
        foreach (var r in Repos())
        {
            var p = r.Path.TrimEnd('\\', '/');
            if (full.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(p + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    public static bool IsRepo(string dir)
    {
        var git = System.IO.Path.Combine(dir, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }
}
