using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>
/// Who is working a claimed ticket right now, as the Orchestrator's state says (M.TIX-T135): the person and login it was
/// dispatched on, the model, and whether its worker died and left it orphaned.
/// </summary>
public sealed record Worker(string TicketId, string Repo, string? Person, string? Login, string? Model, bool Orphaned,
    string? Directory = null, string? Session = null, DateTime? StartedUtc = null, string? Why = null)
{
    /// <summary>
    /// One line for a row: "Ryan · ryandebraal@mindattic.com · opus · in M.TIX-T76 · since 10-25 14:02", with "orphaned" when
    /// its worker is gone (M.TIX-T76 added the worktree and since).
    /// </summary>
    public string Text => string.Join(" · ", new[] { Orphaned ? "⚠ orphaned" : "▶ " + (Person ?? Login ?? "a CLI"),
        Orphaned ? Person ?? Login : Person is null ? null : Login, Model,
        Worktree is { } wt ? "in " + wt : null,
        StartedUtc is { } t ? "since " + t.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : null }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>The worktree's folder name (the last part of <see cref="Directory"/>), or null when none is known.</summary>
    public string? Worktree => Directory?.TrimEnd('\\', '/') is { Length: > 0 } d ? Path.GetFileName(d) is { Length: > 0 } leaf ? leaf : d : null;

    /// <summary>Who's working it, in words: "Ryan (ryan@…) on opus", or the login alone, or "a CLI".</summary>
    public string Agent =>
        (Person is { } p ? Login is { } l ? $"{p} ({l})" : p : Login ?? "a CLI") + (Model is { Length: > 0 } m ? $" on {m}" : "");

    /// <summary>"2026-10-25 14:02 (3h 12m ago)" in local time, or empty when the Orchestrator didn't record a start.</summary>
    public string SinceText(DateTime nowUtc) => StartedUtc is not { } t ? ""
        : t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + $" ({Ago(nowUtc - t)})";

    /// <summary>The row's tooltip detail: the full worktree path and the start time, each on its own line.</summary>
    public string Detail(DateTime nowUtc) => string.Join("\n", new[]
    {
        Directory is { Length: > 0 } d ? "Worktree " + d : null,
        StartedUtc is null ? null : "Since " + SinceText(nowUtc),
        Session is { Length: > 0 } s ? "Session " + s : null,
    }.Where(s => s is not null));

    private static string Ago(TimeSpan span) =>
        span < TimeSpan.Zero ? "just now"
        : span.TotalMinutes < 1 ? "under a minute ago"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours}h {span.Minutes}m ago"
        : $"{(int)span.TotalDays}d {span.Hours}h ago";

    /// <summary>What <c>MindAttic.Orchestrator.exe --show</c> is given to open this work: the ticket, whose live work it finds.</summary>
    public string ShowTarget => TicketId;
}

/// <summary>A repo whose copy of a shared agent file differs from what most repos have (the Orchestrator's audit).</summary>
public sealed record DriftFinding(string File, string Repo, string Path, string CanonicalPath, int CanonicalCount, int Total, int Variants, double Similarity)
{
    /// <summary>Under 30% alike it isn't a stale copy of the shared file, it's a different file (the Orchestrator's rule).</summary>
    public bool IsCustom => Similarity < 0.3;
    public string SimilarityText => $"{Similarity:P0} alike{(IsCustom ? " (custom)" : "")}";
}

/// <summary>A shared agent file most repos have and this one doesn't.</summary>
public sealed record MissingFinding(string File, string Repo, int HaveIt, int Eligible, string ExamplePath)
{
    public string CountText => $"{HaveIt} of {Eligible} repos have it";
}

public sealed record DuplicateFile(string Repo, string Path, int Lines);

/// <summary>The same code living in more than one repo.</summary>
public sealed record DuplicateGroup(IReadOnlyList<DuplicateFile> Files, bool Identical, double Similarity, int SharedLines, string SuggestedHome)
{
    public IReadOnlyList<string> Repos => [.. Files.Select(f => f.Repo).Distinct(StringComparer.OrdinalIgnoreCase)];
    public string Title => $"{Files.Count} files in {Repos.Count} repos: {string.Join(", ", Repos)}";
    public string Facts => $"{(Identical ? "identical" : $"{Similarity:P0} alike")} · {SharedLines} shared lines · suggested home {SuggestedHome}";
    public string FileList => string.Join("\n", Files.Select(f => $"{f.Repo}: {f.Path} ({f.Lines} lines)"));
}

/// <summary>The drift and duplicate-code findings of the Orchestrator's last audit run.</summary>
public sealed record AuditFindings(DateTime RanUtc, TimeSpan Took, int ReposScanned, int FilesScanned,
    IReadOnlyList<DriftFinding> Drift, IReadOnlyList<MissingFinding> Missing, IReadOnlyList<DuplicateGroup> Duplicates)
{
    public static AuditFindings Empty { get; } = new(DateTime.MinValue, TimeSpan.Zero, 0, 0, [], [], []);
    public bool HasRun => RanUtc > DateTime.MinValue;
}

/// <summary>What Tickets Desktop shows of the Orchestrator: each claimed ticket's worker and the audit findings.</summary>
public sealed record OrchestratorSnapshot(IReadOnlyDictionary<string, Worker> Workers, AuditFindings Audit, string? Error)
{
    public static OrchestratorSnapshot None(string? error) =>
        new(new Dictionary<string, Worker>(StringComparer.OrdinalIgnoreCase), AuditFindings.Empty, error);
}

/// <summary>
/// Reads the Orchestrator's database (<c>MindAttic.Orchestrator\data\orchestrator.db</c>, or the file
/// <c>MINDATTIC_ORCHESTRATOR_DB</c> names, as the Orchestrator finds it) read-only: never a write, never a lock held past
/// the read. Its tables used here (assignments, members, state) are the ones the Orchestrator keeps in this file even when
/// its data lives on SQL Server (they're mirrored here for its hooks). A missing or unreadable file is no workers and no
/// findings, with the reason, never an error the window has to handle.
/// </summary>
public sealed class OrchestratorState(string path)
{
    public const string EnvVar = "MINDATTIC_ORCHESTRATOR_DB";

    public string DbPath { get; } = Path.GetFullPath(path);

    /// <summary>The Orchestrator's database for the workspace at <paramref name="workspaceRoot"/>.</summary>
    public static OrchestratorState For(string workspaceRoot) =>
        new(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env ? env
            : Path.Combine(workspaceRoot, "MindAttic.Orchestrator", "data", "orchestrator.db"));

    public OrchestratorSnapshot Load()
    {
        if (!File.Exists(DbPath)) return OrchestratorSnapshot.None($"No Orchestrator database at {DbPath}.");
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString();
            using var db = new SqliteConnection(cs);
            db.Open();
            var tables = Strings(db, "SELECT name FROM sqlite_master WHERE type = 'table';").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var people = tables.Contains("members") ? Pairs(db, "SELECT email, name FROM members;") : [];
            var state = tables.Contains("state") ? Pairs(db, "SELECT name, value FROM state WHERE name IN ('audit', 'orphaned-tickets');") : [];
            var workers = new Dictionary<string, Worker>(StringComparer.OrdinalIgnoreCase);
            if (tables.Contains("assignments")) foreach (var w in Assignments(db, people)) workers.TryAdd(w.TicketId, w);
            if (state.TryGetValue("orphaned-tickets", out var orphans) && orphans is not null) foreach (var o in Orphans(orphans, people))
                workers[o.TicketId] = workers.TryGetValue(o.TicketId, out var claimed)
                    ? claimed with { Orphaned = true, Why = o.Why }
                    : o;
            var audit = state.TryGetValue("audit", out var json) && json is not null ? ParseAudit(json) : AuditFindings.Empty;
            return new OrchestratorSnapshot(workers, audit, null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Couldn't read the Orchestrator's database {Db}", DbPath);
            return OrchestratorSnapshot.None($"Couldn't read the Orchestrator's database: {ex.Message}");
        }
    }

    private static string? PersonOf(IReadOnlyDictionary<string, string?> people, string? email) =>
        email is null ? null : people.TryGetValue(email.Trim().ToLowerInvariant(), out var n) && n is { Length: > 0 } ? n : null;

    /// <summary>The tickets the Orchestrator's <c>orphaned-tickets</c> state calls orphaned: their worker died with them open.</summary>
    internal static IEnumerable<Worker> Orphans(string text, IReadOnlyDictionary<string, string?> people)
    {
        var list = new List<Worker>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (Str(e, "TicketId") is not { Length: > 0 } id) continue;
                if (Str(e, "State") is { } s && !s.Equals("orphaned", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.TryGetProperty("Open", out var open) && open.ValueKind == JsonValueKind.False) continue;
                var login = Str(e, "Account");
                list.Add(new Worker(id, Str(e, "Repo") ?? "", PersonOf(people, login), login, null, true, Str(e, "Directory"), null, null, Str(e, "Why")));
            }
        }
        catch (JsonException ex) { Log.Warning(ex, "The Orchestrator's orphaned-tickets state isn't readable"); }
        return list;
    }

    private static IEnumerable<Worker> Assignments(SqliteConnection db, IReadOnlyDictionary<string, string?> people)
    {
        var columns = Strings(db, "SELECT name FROM pragma_table_info('assignments');").ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Col(string name) => columns.Contains(name) ? name : "NULL";
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT ticket_id, repo, directory, started_utc, {Col("account")}, {Col("model")}, {Col("cli_session")} FROM assignments ORDER BY position;";
        using var r = cmd.ExecuteReader();
        var list = new List<Worker>();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.GetString(0) is not { Length: > 0 } id) continue;
            string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            var login = S(4);
            var person = PersonOf(people, login);
            DateTime? started = DateTime.TryParse(S(3), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            list.Add(new Worker(id, S(1) ?? "", person, login, S(5), false, S(2), S(6), started));
        }
        return list;
    }

    /// <summary>The Orchestrator's audit report (its <c>audit</c> state row, the AuditReport it serialises), or empty.</summary>
    internal static AuditFindings ParseAudit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return AuditFindings.Empty;
            var ran = DateTime.TryParse(Str(e, "RanUtc"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var r) ? r : DateTime.MinValue;
            var took = TimeSpan.TryParse(Str(e, "Took"), System.Globalization.CultureInfo.InvariantCulture, out var tk) ? tk : TimeSpan.Zero;
            return new AuditFindings(ran, took, Int(e, "ReposScanned"), Int(e, "FilesScanned"),
                [.. Items(e, "Drift").Select(d => new DriftFinding(Str(d, "File") ?? "", Str(d, "Repo") ?? "", Str(d, "Path") ?? "", Str(d, "CanonicalPath") ?? "",
                    Int(d, "CanonicalCount"), Int(d, "Total"), Int(d, "Variants"), Dbl(d, "Similarity")))],
                [.. Items(e, "Missing").Select(m => new MissingFinding(Str(m, "File") ?? "", Str(m, "Repo") ?? "", Int(m, "HaveIt"), Int(m, "Eligible"), Str(m, "ExamplePath") ?? ""))],
                [.. Items(e, "Duplicates").Select(g => new DuplicateGroup(
                    [.. Items(g, "Files").Select(f => new DuplicateFile(Str(f, "Repo") ?? "", Str(f, "Path") ?? "", Int(f, "Lines")))],
                    g.TryGetProperty("Identical", out var i) && i.ValueKind == JsonValueKind.True, Dbl(g, "Similarity"), Int(g, "SharedLines"), Str(g, "SuggestedHome") ?? ""))]);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "The Orchestrator's audit state isn't readable");
            return AuditFindings.Empty;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static List<string> Strings(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        return list;
    }

    private static Dictionary<string, string?> Pairs(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) if (!r.IsDBNull(0)) map[r.GetString(0).Trim().ToLowerInvariant()] = r.IsDBNull(1) ? null : r.GetString(1);
        return map;
    }

    private static readonly Serilog.ILogger Log = TicketsLog.For("Orchestrator");
}
