using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Log;

/// <summary>The commits that mention a ticket, for its end-to-end view (taken over from <c>orchestrator show</c>, M.TIX-T134).</summary>
public static class TicketCommits
{
    /// <summary>Commits on any branch of the repo at <paramref name="repoPath"/> whose message names <paramref name="ticketId"/> as a
    /// whole word, newest first, each with the files it touched (or, with <paramref name="diff"/>, its full diff); "" when there
    /// are none or the folder isn't a repo.</summary>
    public static string Log(string ticketId, string repoPath, bool diff = false)
    {
        if (!Directory.Exists(repoPath)) return "";
        var wholeWord = $"(^|[^A-Za-z0-9]){Regex.Escape(ticketId)}([^A-Za-z0-9]|$)";
        try
        {
            var r = Git.Run(repoPath, "log", "--all", "--extended-regexp", $"--grep={wholeWord}", "--date=format-local:%Y-%m-%d %H:%M",
                "--format=%n%h %ad %an%n    %s", diff ? "--patch" : "--stat");
            return r.Ok ? r.Output.Trim() : "";
        }
        catch (System.ComponentModel.Win32Exception) { return ""; } // no git on the PATH
    }
}
