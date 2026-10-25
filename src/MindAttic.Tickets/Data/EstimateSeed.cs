using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace MindAttic.Tickets.Data;

/// <summary>What one session the Orchestrator dispatched on a ticket spent on it (uncached tokens): a row of its
/// <c>ticket_costs</c> (TicketCosts' SessionCost).</summary>
public sealed record PastCost(string TicketId, string? Type, string Repo, long Fresh);

/// <summary>An estimate worked out from past costs: <paramref name="Tokens"/>, the median of <paramref name="Basis"/>
/// finished tickets, described by <paramref name="Of"/> ("fix tickets in MindAttic.Tickets", "feature tickets", "tickets").</summary>
public sealed record CostMedian(long Tokens, int Basis, string Of);

/// <summary>The estimate <see cref="TicketsDb.SeedEstimates"/> gave (or would give) one open ticket.</summary>
public sealed record SeededEstimate(string Repo, string TicketId, CostMedian Estimate);

/// <summary>
/// Effort estimates seeded from the Orchestrator's TicketCosts (M.TIX-T75), worked out as its <c>TicketCosts.Estimate</c>
/// does: each ticket's cost is every session on it added up, and a ticket's estimate is the median cost of tickets of its
/// type in its repo when there are at least <see cref="MinBasis"/>, else of its type anywhere, else of every ticket.
/// </summary>
public sealed class EstimateSeed
{
    public const int MinBasis = 3;

    private readonly List<(string TicketId, string? Type, string Repo, long Fresh)> tickets;

    public EstimateSeed(IEnumerable<PastCost> sessions) =>
        tickets = [.. sessions.GroupBy(s => s.TicketId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Select(s => s.Type).FirstOrDefault(t => t is not null), g.First().Repo, g.Sum(s => s.Fresh)))];

    /// <summary>How many finished tickets the costs cover.</summary>
    public int Tickets => tickets.Count;

    /// <summary>The estimate for a ticket of <paramref name="type"/> in <paramref name="repo"/> (not counting
    /// <paramref name="excludeTicket"/>'s own cost so far), or null with no costs at all.</summary>
    public CostMedian? Estimate(string repo, string type, string? excludeTicket = null)
    {
        var done = tickets.Where(t => !string.Equals(t.TicketId, excludeTicket, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (pool, of) in new[]
        {
            (done.Where(t => string.Equals(t.Repo, repo, StringComparison.OrdinalIgnoreCase) && t.Type == type).ToList(), $"{type} tickets in {repo}"),
            (done.Where(t => t.Type == type).ToList(), $"{type} tickets"),
            (done, "tickets"),
        })
        {
            if (pool.Count < MinBasis && of != "tickets") continue;
            if (pool.Count == 0) return null;
            var sorted = pool.Select(t => t.Fresh).Order().ToList();
            return new CostMedian(sorted[sorted.Count / 2], sorted.Count, of);
        }
        return null;
    }
}

/// <summary>
/// Reads the Orchestrator's TicketCosts (its <c>ticket_costs</c> table) read-only: from its SQL Server database when this
/// machine has one configured (<c>MINDATTIC_ORCHESTRATOR_SQLSERVER</c>, else <c>%APPDATA%\MindAttic\MindAttic.Orchestrator\database.json</c>),
/// as the Orchestrator itself finds it, else from its file (<c>MINDATTIC_ORCHESTRATOR_DB</c>, else
/// <c>&lt;workspace&gt;\MindAttic.Orchestrator\data\orchestrator.db</c>).
/// </summary>
public static class OrchestratorCosts
{
    public const string DbEnvVar = "MINDATTIC_ORCHESTRATOR_DB";
    public const string ServerEnvVar = "MINDATTIC_ORCHESTRATOR_SQLSERVER";

    public static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MindAttic", "MindAttic.Orchestrator", "database.json");

    public static string FilePath(string? workspaceRoot = null) =>
        Environment.GetEnvironmentVariable(DbEnvVar) is { Length: > 0 } env ? env
            : Path.Combine(workspaceRoot ?? Workspace.DefaultRoot, "MindAttic.Orchestrator", "data", "orchestrator.db");

    /// <summary>The connection string of the Orchestrator's SQL Server database, or null when its data is in its file. A
    /// database file named by <see cref="DbEnvVar"/> (a test's) is never on the server, as with the Orchestrator.</summary>
    public static string? ServerConnection()
    {
        if (Environment.GetEnvironmentVariable(ServerEnvVar) is { Length: > 0 } env)
            return string.Equals(env, "off", StringComparison.OrdinalIgnoreCase) ? null : env;
        if (Environment.GetEnvironmentVariable(DbEnvVar) is { Length: > 0 }) return null;
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigFile));
            return doc.RootElement.TryGetProperty("SqlServer", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } c ? c : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Every session cost the Orchestrator keeps, and where they were read from.</summary>
    public static (IReadOnlyList<PastCost> Costs, string Source) Load(string? workspaceRoot = null)
    {
        if (ServerConnection() is { } connection)
        {
            using var c = new SqlConnection(connection);
            c.Open();
            return (Read(c), new SqlConnectionStringBuilder(connection) is var b ? $"{b.DataSource}/{b.InitialCatalog}" : "SQL Server");
        }
        var path = Path.GetFullPath(FilePath(workspaceRoot));
        if (!File.Exists(path)) throw new FileNotFoundException($"No Orchestrator database at {path}.", path);
        using var file = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 15 }.ToString());
        file.Open();
        return (Read(file), path);
    }

    /// <summary>The <c>ticket_costs</c> rows on <paramref name="connection"/> (none when it has no such table).</summary>
    public static IReadOnlyList<PastCost> Read(DbConnection connection)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = connection is SqliteConnection
            ? "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ticket_costs'"
            : "SELECT COUNT(*) FROM sys.tables WHERE name = N'ticket_costs'";
        if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return [];
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ticket_id, type, repo, fresh FROM ticket_costs";
        using var r = cmd.ExecuteReader();
        var list = new List<PastCost>();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(2) || r.IsDBNull(3)) continue;
            list.Add(new PastCost(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture)));
        }
        return list;
    }
}
