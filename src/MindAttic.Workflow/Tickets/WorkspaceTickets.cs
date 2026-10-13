using System.Text.RegularExpressions;

namespace MindAttic.Workflow.Tickets;

/// <summary>A ticket together with the repo it lives in.</summary>
public sealed record RepoTicket(Repo Repo, Ticket Ticket);

/// <summary>A problem found by <see cref="WorkspaceTickets.Check"/>.</summary>
public sealed record TicketProblem(string Repo, string Message);

/// <summary>Ticket queries and checks across every repo in a <see cref="Workspace"/>.</summary>
public static partial class WorkspaceTickets
{
    /// <summary>Every ticket in every repo that has a USER_STORIES.md, in repo then file order.</summary>
    public static IReadOnlyList<RepoTicket> All(IEnumerable<Repo> repos)
    {
        var result = new List<RepoTicket>();
        foreach (var r in repos)
        {
            if (!File.Exists(r.StoriesPath)) continue;
            try { result.AddRange(StoryFile.Load(r.StoriesPath).Tickets.Select(t => new RepoTicket(r, t))); }
            catch (InvalidOperationException) { /* malformed table: reported by Check, not fatal for a listing */ }
        }
        return result;
    }

    /// <summary>
    /// Workspace-wide checks: repos missing USER_STORIES.md or a code, project codes used by more than
    /// one repo (ticket IDs are only unique if codes are), and per-file table problems.
    /// </summary>
    public static IReadOnlyList<TicketProblem> Check(IEnumerable<Repo> repos)
    {
        var problems = new List<TicketProblem>();
        var codes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in repos)
        {
            if (!File.Exists(r.StoriesPath)) { problems.Add(new(r.Name, "no docs/USER_STORIES.md")); continue; }
            StoryFile file;
            try { file = StoryFile.Load(r.StoriesPath); }
            catch (IOException ex) { problems.Add(new(r.Name, ex.Message)); continue; }

            var code = file.Code;
            if (code is null) { problems.Add(new(r.Name, "USER_STORIES.md has no 'code:' front-matter")); continue; }
            (codes.TryGetValue(code, out var list) ? list : codes[code] = []).Add(r.Name);

            if (!file.HasTicketsSection) { problems.Add(new(r.Name, "no '## Tickets' section")); continue; }
            if (!file.TicketsSectionIsLast) problems.Add(new(r.Name, "'## Tickets' isn't the last section"));
            try
            {
                foreach (var p in TicketRules.Validate(code, file.Tickets)) problems.Add(new(r.Name, p));
            }
            catch (InvalidOperationException ex) { problems.Add(new(r.Name, ex.Message)); }
        }

        foreach (var (code, names) in codes.Where(kv => kv.Value.Count > 1))
            problems.Add(new(string.Join(", ", names), $"project code '{code}' is used by {names.Count} repos; ticket IDs would collide"));
        return problems;
    }

    /// <summary>
    /// The Codex project code for a repo name: drop a leading <c>MindAttic.</c> and a trailing year,
    /// take the PascalCase initials (GridGame2026 → GG; MindAttic.Ideas → MAI, since a lone initial
    /// keeps the MindAttic one). Falls back to the first three letters, then adds digits until it's not in
    /// <paramref name="taken"/>.
    /// </summary>
    public static string DeriveCode(string repoName, IReadOnlySet<string> taken)
    {
        var name = repoName;
        var hadPrefix = name.StartsWith("MindAttic.", StringComparison.OrdinalIgnoreCase);
        if (hadPrefix) name = name["MindAttic.".Length..];
        name = TrailingYear().Replace(name, "");

        var initials = string.Concat(WordStarts().Matches(name).Select(m => m.Value[0])).ToUpperInvariant();
        if (hadPrefix && initials.Length == 1) initials = "MA" + initials;
        var letters = new string([.. name.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

        var options = new List<string>();
        if (initials.Length is >= 2 and <= 4) options.Add(initials);
        if (letters.Length >= 3) options.Add(letters[..3]);
        if (letters.Length >= 4) options.Add(letters[..4]);
        foreach (var o in options)
            if (!taken.Contains(o)) return o;

        var stem = options.FirstOrDefault() ?? (letters.Length > 0 ? letters : "X");
        for (var i = 2; ; i++)
            if (!taken.Contains(stem + i)) return stem + i;
    }

    /// <summary>The code in a USER_STORIES.md, if the repo has one.</summary>
    public static string? CodeOf(Repo repo) =>
        File.Exists(repo.StoriesPath) ? StoryFile.Load(repo.StoriesPath).Code : null;

    [GeneratedRegex(@"\d{4}$")]
    private static partial Regex TrailingYear();

    // PascalCase/kebab word splitter: an acronym run (USPS in USPSAddress), a capitalized or lowercase word, or digits.
    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex WordStarts();
}
