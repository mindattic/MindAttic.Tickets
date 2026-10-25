using System.Text.RegularExpressions;
using MindAttic.Tickets.Repos;

namespace MindAttic.Tickets.Log;

/// <summary>
/// One commit that names a ticket in its subject (M.TIX-T85): its hash, when and by whom, the subject, and its page on GitHub
/// (null when the repo's origin isn't on GitHub).
/// </summary>
public sealed record TicketCommit(string Hash, string ShortHash, string Date, string Author, string Subject, string? Url);

/// <summary>The commits that mention a ticket, for its end-to-end view (taken over from <c>orchestrator show</c>, M.TIX-T134).</summary>
public static class TicketCommits
{
    /// <summary>Commits on any branch of the repo at <paramref name="repoPath"/> whose message names <paramref name="ticketId"/> as a
    /// whole word, newest first, each with the files it touched (or, with <paramref name="diff"/>, its full diff); "" when there
    /// are none or the folder isn't a repo.</summary>
    public static string Log(string ticketId, string repoPath, bool diff = false)
    {
        if (!Directory.Exists(repoPath)) return "";
        try
        {
            var r = Git.Run(repoPath, "log", "--all", "--extended-regexp", $"--grep={WholeWord(ticketId)}", "--date=format-local:%Y-%m-%d %H:%M",
                "--format=%n%h %ad %an%n    %s", diff ? "--patch" : "--stat");
            return r.Ok ? r.Output.Trim() : "";
        }
        catch (System.ComponentModel.Win32Exception) { return ""; } // no git on the PATH
    }

    /// <summary>
    /// The ticket's commits (M.TIX-T85): those on any branch of the repo at <paramref name="repoPath"/> whose subject line names
    /// <paramref name="ticketId"/> as a whole word (the commit-msg rule puts it there), newest first, each linked to its page on
    /// GitHub when origin is a GitHub remote. Empty when there are none, the folder isn't a repo, or git isn't on the PATH.
    /// </summary>
    public static IReadOnlyList<TicketCommit> Find(string ticketId, string repoPath)
    {
        if (!Directory.Exists(repoPath)) return [];
        try
        {
            // --grep searches the whole message; only a subject naming the ticket counts, so the subject is checked again here.
            var r = Git.Run(repoPath, "log", "--all", "--extended-regexp", "--regexp-ignore-case", $"--grep={WholeWord(ticketId)}",
                "--date=format-local:%Y-%m-%d %H:%M", "--format=%H%x1f%h%x1f%ad%x1f%an%x1f%s%x1e");
            if (!r.Ok) return [];
            var origin = Git.Run(repoPath, "remote", "get-url", "origin");
            var slug = origin.Ok ? RepoStatus.GitHubSlugFrom(origin.Output) : null;
            return Parse(r.Output, ticketId, slug);
        }
        catch (System.ComponentModel.Win32Exception) { return []; } // no git on the PATH
    }

    /// <summary>
    /// Reads <c>git log --format=%H%x1f%h%x1f%ad%x1f%an%x1f%s%x1e</c> output, keeping the commits whose subject names
    /// <paramref name="ticketId"/> as a whole word (any case), each linked to <c>github.com/&lt;slug&gt;/commit/&lt;hash&gt;</c>
    /// when <paramref name="gitHubSlug"/> is given.
    /// </summary>
    public static IReadOnlyList<TicketCommit> Parse(string log, string ticketId, string? gitHubSlug)
    {
        var subjectNames = new Regex(WholeWord(ticketId), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var found = new List<TicketCommit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in log.Split('\x1e'))
        {
            var f = record.Trim('\r', '\n').Split('\x1f');
            if (f.Length < 5 || f[0].Length == 0 || !subjectNames.IsMatch(f[4]) || !seen.Add(f[0])) continue;
            found.Add(new TicketCommit(f[0], f[1], f[2], f[3], f[4], gitHubSlug is null ? null : $"https://github.com/{gitHubSlug}/commit/{f[0]}"));
        }
        return found;
    }

    private static string WholeWord(string ticketId) => $"(^|[^A-Za-z0-9]){Regex.Escape(ticketId)}([^A-Za-z0-9]|$)";
}
