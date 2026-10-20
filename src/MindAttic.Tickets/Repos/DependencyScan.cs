using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Repos;

/// <summary>A file in one repo that points at another repo's folder by relative path.</summary>
public sealed record FolderDependency(string FromRepo, string File, string ToRepo, string RawPath);

/// <summary>
/// Finds repos that reach into a sibling repo's folder by path — a <c>ProjectReference</c>, a Unity
/// <c>Packages/manifest.json</c> <c>file:</c> entry, a <c>.slnx</c>/<c>.sln</c> project path, or an
/// npm <c>file:</c> dependency. Deleting the target folder would break the referencing repo's build,
/// so the prune planner blocks on these.
/// </summary>
public static partial class DependencyScan
{
    // Not "packages": Unity keeps Packages/manifest.json there, and skipping is case-insensitive.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "Library", "Temp", "Logs", "UserSettings", "node_modules", ".vs",
        "artifacts", "dist", "build", "Build", "TestResults", ".venv", "venv",
    };

    /// <summary>Every cross-repo folder reference from any of <paramref name="repos"/> into another.</summary>
    public static IReadOnlyList<FolderDependency> Scan(IReadOnlyList<Repo> repos)
    {
        var result = new List<FolderDependency>();
        foreach (var from in repos)
            foreach (var file in CandidateFiles(from.Path))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var dir = Path.GetDirectoryName(file)!;
                foreach (var raw in ExtractPaths(Path.GetFileName(file), text))
                {
                    string full;
                    try { full = Path.GetFullPath(Path.Combine(dir, raw.Replace('/', Path.DirectorySeparatorChar))); }
                    catch (ArgumentException) { continue; }
                    catch (NotSupportedException) { continue; }

                    var to = repos.FirstOrDefault(r => r != from && IsUnder(full, r.Path));
                    if (to is not null)
                        result.Add(new FolderDependency(from.Name, Path.GetRelativePath(from.Path, file), to.Name, raw));
                }
            }
        return result;
    }

    /// <summary>Relative paths a file of this kind references. Only relative paths can cross into a sibling.</summary>
    internal static IEnumerable<string> ExtractPaths(string fileName, string text)
    {
        var pattern = fileName.ToLowerInvariant() switch
        {
            "manifest.json" or "package.json" => FileProtocol(),
            var n when n.EndsWith(".slnx", StringComparison.Ordinal) => SlnxPath(),
            var n when n.EndsWith(".sln", StringComparison.Ordinal) => SlnPath(),
            _ => ProjectRef(),
        };
        foreach (Match m in pattern.Matches(text))
        {
            var p = m.Groups[1].Value.Trim();
            if (p.StartsWith("..", StringComparison.Ordinal)) yield return p;
        }
    }

    private static IEnumerable<string> CandidateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
                    (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
                     Path.GetFileName(dir).Equals("Packages", StringComparison.OrdinalIgnoreCase)))
                    yield return f;
            }
            foreach (var d in dirs)
                if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var r = root.TrimEnd('\\', '/');
        return path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"""file:([^""]+)""")]
    private static partial Regex FileProtocol();

    [GeneratedRegex(@"Path=""([^""]+)""")]
    private static partial Regex SlnxPath();

    [GeneratedRegex(@"Project\([^)]*\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""")]
    private static partial Regex SlnPath();

    // Any other attribute (e.g. Condition) may come before Include/Project: MSBuild doesn't fix attribute order,
    // and `<ProjectReference Condition="..." Include="..\Sibling\Sibling.csproj" />` is a common, legitimate pattern
    // that a regex anchored to "the tag name is immediately followed by Include/Project" would silently miss.
    [GeneratedRegex(@"<(?:ProjectReference|Import)\b(?:\s+[A-Za-z_][\w.-]*\s*=\s*""[^""]*"")*?\s+(?:Include|Project)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectRef();
}
