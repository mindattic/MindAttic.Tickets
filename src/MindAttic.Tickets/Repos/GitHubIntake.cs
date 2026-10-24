using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Repos;

/// <summary>Something on GitHub that should be a ticket: its marker (GH#12, CI#987), type, summary and link.</summary>
public sealed record IntakeItem(string Marker, string Type, string Summary, string Url);

/// <summary>
/// Files GitHub's signals as tickets so they land in the queue (taken over from the Orchestrator's intake, M.TIX-T134): open
/// issues labelled <c>orchestrator</c>, and CI runs that failed on main/master in the last day. Each is filed once (its marker
/// goes in the summary) and nothing is written back to GitHub. Reads through the <c>gh</c> CLI, so it uses whatever account
/// <c>gh auth</c> has.
/// </summary>
public static partial class GitHubIntake
{
    public const string Label = "orchestrator";

    [GeneratedRegex(@"github\.com[:/](?<slug>[^/\s]+/[^/\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Remote();

    /// <summary>owner/name from a GitHub remote URL (https or ssh); null for anything else.</summary>
    public static string? Slug(string remote) => Remote().Match(remote.Trim()) is { Success: true } m ? m.Groups["slug"].Value : null;

    /// <summary>How long one <c>gh</c> call may run before it's killed.</summary>
    public static readonly TimeSpan GhTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Runs <c>gh</c>: its exit code and output (its error text when it failed); -1 when it isn't installed or ran past
    /// <see cref="GhTimeout"/>.</summary>
    public static (int Code, string Output) Gh(params string[] args)
    {
        var psi = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start gh.");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(GhTimeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, $"gh took over {GhTimeout.TotalSeconds:0}s; stopped it");
            }
            return (p.ExitCode, p.ExitCode == 0 ? stdout.GetAwaiter().GetResult() : stderr.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, "the GitHub CLI (gh) isn't installed"); }
    }

    /// <summary>What on GitHub repo <paramref name="slug"/> should be a ticket, asked through <paramref name="gh"/>.</summary>
    public static IReadOnlyList<IntakeItem> Find(string slug, Func<string[], (int Code, string Output)> gh, DateTime nowUtc)
    {
        var items = new List<IntakeItem>();
        // gh returns 30 unless told otherwise: issue 31 on was never filed (M.ORCH-T548).
        var (code, json) = gh(["issue", "list", "--repo", slug, "--label", Label, "--state", "open", "--limit", "1000", "--json", "number,title,labels,url"]);
        if (code == 0)
            foreach (var issue in Parse(json))
            {
                var labels = issue.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                    ? l.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToList() : [];
                // Any case, as GitHub's own labels go ("Feature", "Enhancement"): only "bug" was, so a feature filed as a fix
                // was worked without approval (M.ORCH-T443).
                bool Has(params string[] names) => labels.Any(x => names.Any(n => x.Equals(n, StringComparison.OrdinalIgnoreCase)));
                var type = Has("bug") ? "fix"
                    : Has("enhancement", "feature") ? "feature"
                    : Has("documentation", "docs") ? "docs"
                    : Has("chore") ? "chore" : "fix";
                var number = issue.GetProperty("number").GetInt32();
                items.Add(new IntakeItem($"GH#{number}", type, $"GH#{number}: {Str(issue, "title")}", Str(issue, "url")));
            }

        // Asked per main line: one --limit over every branch let feature-branch failures hide one on main (M.ORCH-T548).
        var seen = new HashSet<long>();
        foreach (var mainline in new[] { "main", "master" })
        {
            (code, json) = gh(["run", "list", "--repo", slug, "--branch", mainline, "--status", "failure", "--limit", "100", "--json", "databaseId,name,headBranch,createdAt,url"]);
            if (code != 0) continue;
            foreach (var run in Parse(json))
            {
                var branch = Str(run, "headBranch");
                if (branch is not ("main" or "master")) continue;
                if (!DateTime.TryParse(Str(run, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at) || nowUtc - at > TimeSpan.FromDays(1)) continue;
                var id = run.GetProperty("databaseId").GetInt64();
                if (!seen.Add(id)) continue;
                items.Add(new IntakeItem($"CI#{id}", "fix", $"CI#{id}: {Str(run, "name")} failed on {branch}", Str(run, "url")));
            }
        }
        return items;
    }

    /// <summary>Adds the items no ticket mentions yet (by marker) to a story-file repo's log, returning what was filed; the caller
    /// saves the file.</summary>
    public static IReadOnlyList<Ticket> FileNew(StoryFile stories, IEnumerable<IntakeItem> items)
    {
        var filed = new List<Ticket>();
        foreach (var item in items)
        {
            var marker = Marker(item);
            if (stories.Tickets.Any(t => marker.IsMatch(t.Summary))) continue;
            filed.Add(stories.Add(item.Type, TicketRules.Open, item.Summary.Replace('|', '/'), item.Url));
        }
        return filed;
    }

    /// <summary>Adds the items no ticket in <paramref name="repo"/> mentions yet (by marker) to the ticket database; with
    /// <paramref name="dryRun"/>, only says which it would add (ids unassigned).</summary>
    public static IReadOnlyList<Ticket> FileNew(TicketsDb db, string repo, IEnumerable<IntakeItem> items, bool dryRun = false)
    {
        var existing = db.Tickets(repo).Select(t => t.Ticket).ToList();
        var filed = new List<Ticket>();
        foreach (var item in items)
        {
            var marker = Marker(item);
            if (existing.Concat(filed).Any(t => marker.IsMatch(t.Summary))) continue;
            filed.Add(dryRun ? new Ticket("(new)", TicketRules.Today(), item.Type, TicketRules.Open, item.Summary, item.Url)
                : db.Add(repo, item.Type, TicketRules.Open, item.Summary, item.Url));
        }
        return filed;
    }

    /// <summary>
    /// Files new GitHub issues and CI failures as tickets in every workspace repo (or only <paramref name="only"/>) whose
    /// <c>origin</c> is on GitHub: into the ticket database when it holds the repo, else into the repo's own USER_STORIES.md.
    /// </summary>
    public static IEnumerable<(string Repo, Ticket Ticket)> Intake(Workspace workspace, TicketsDb? db, bool dryRun = false,
        Func<string[], (int Code, string Output)>? gh = null, Func<Repo, string?>? remote = null, string? only = null, DateTime? nowUtc = null)
    {
        gh ??= Gh;
        remote ??= r => Git.Run(r.Path, "remote", "get-url", "origin") is { Ok: true } o ? o.Output.Trim() : null;
        var inDb = db?.Repos().Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var repo in workspace.Repos())
        {
            if (only is not null && !repo.Name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            var held = inDb.Contains(repo.Name);
            if (!held && !File.Exists(repo.StoriesPath)) continue;
            if (remote(repo) is not { } url || Slug(url) is not { } slug) continue;
            var items = Find(slug, gh, nowUtc ?? DateTime.UtcNow);
            if (items.Count == 0) continue;
            if (held)
            {
                foreach (var t in FileNew(db!, repo.Name, items, dryRun)) yield return (repo.Name, t);
                continue;
            }
            var stories = StoryFile.Load(repo.StoriesPath);
            var filed = FileNew(stories, items);
            if (filed.Count > 0 && !dryRun) stories.Save();
            foreach (var t in filed) yield return (repo.Name, t);
        }
    }

    private static Regex Marker(IntakeItem item) => new($@"{Regex.Escape(item.Marker)}\b");

    private static IEnumerable<JsonElement> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())] : [];
        }
        catch (JsonException) { return []; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
