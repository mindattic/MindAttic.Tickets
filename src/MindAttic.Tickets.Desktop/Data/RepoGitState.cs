using System.IO;
using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>
/// A repo's git state for the Repos tab (M.TIX-T135): its branch, commits ahead of and behind its upstream, and how many
/// files are changed but not committed. Read with one <c>git status --porcelain=v1 -b</c> (no fetch: what this machine knows).
/// </summary>
public sealed partial record RepoGitState(string? Branch, int Ahead, int Behind, int Changed, string? Error = null)
{
    public static RepoGitState Missing { get; } = new(null, 0, 0, 0, "no checkout");

    /// <summary>"main · 2 ahead · 3 changed", "main · clean", or what went wrong.</summary>
    public string Text => Error is { } e ? e : string.Join(" · ", new[]
    {
        Branch ?? "detached",
        Ahead > 0 ? $"{Ahead} ahead" : null,
        Behind > 0 ? $"{Behind} behind" : null,
        Changed > 0 ? $"{Changed} changed" : null,
        Ahead == 0 && Behind == 0 && Changed == 0 ? "clean" : null,
    }.Where(s => s is not null));

    public static RepoGitState Read(string directory)
    {
        if (!Workspace.IsRepo(directory)) return Missing;
        try
        {
            var r = Git.Run(directory, TimeSpan.FromSeconds(20), "status", "--porcelain=v1", "-b");
            return r.Ok ? Parse(r.Output) : new RepoGitState(null, 0, 0, 0, r.Error.Trim() is { Length: > 0 } e ? e : $"git exited {r.ExitCode}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new RepoGitState(null, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>Parses <c>git status --porcelain=v1 -b</c>: the <c>## branch...upstream [ahead n, behind m]</c> line, then one line per change.</summary>
    public static RepoGitState Parse(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var head = lines.FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal));
        string? branch = null;
        int ahead = 0, behind = 0;
        if (head is not null)
        {
            var m = Header().Match(head);
            branch = m.Groups["branch"].Value;
            if (branch.StartsWith("HEAD (no branch)", StringComparison.Ordinal)) branch = null;
            else if (branch.StartsWith("No commits yet on ", StringComparison.Ordinal)) branch = branch["No commits yet on ".Length..];
            if (m.Groups["ahead"].Success) ahead = int.Parse(m.Groups["ahead"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (m.Groups["behind"].Success) behind = int.Parse(m.Groups["behind"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return new RepoGitState(branch, ahead, behind, lines.Count(l => !l.StartsWith("## ", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"^## (?<branch>.+?)(?:\.\.\.\S+)?(?: \[(?:ahead (?<ahead>\d+))?(?:, )?(?:behind (?<behind>\d+))?(?:gone)?\])?$")]
    private static partial Regex Header();
}
