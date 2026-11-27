using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MindAttic.Tickets.Data;

/// <summary>
/// The fields a ticket list shows, read for many tickets at once, with a checksum over them (M.TIX-T414): a client holding a
/// row checks it against <see cref="TicketsDb.Checksum"/> of a fresh read, and refreshes it when they differ.
/// </summary>
public sealed record TicketRow(
    string Id, string Repo, string Status, string Summary, string? Name, string? Level, string? Parent,
    long Priority, string Approval, string? Assignee, string? Login, string? Description, string Checksum);

/// <summary>
/// What changed since a version: each changed ticket's ID with the version it last changed at (a ticket whose rows are gone
/// is listed too, and <see cref="TicketsDb.Rows(IEnumerable{string})"/> then has no row for it). <see cref="FullReload"/>
/// means the store can't answer from that version (SQLite, change tracking off, older than the server keeps changes, or
/// another database's number): read every ticket again. <see cref="Version"/> is the store's version this answer covers to.
/// </summary>
public sealed record TicketChanges(long Version, bool FullReload, IReadOnlyDictionary<string, long> Changed);

/// <summary>
/// Per-ticket versions from SQL Server's change tracking (on for every table since schema 9), and the minimal row with its
/// checksum (M.TIX-T414). Nothing is written to answer either: a ticket's version is the newest change-tracking version of
/// any of its rows in the tables below, read in one query, so no write path has to remember to stamp one.
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>The tables whose rows belong to one ticket, by the column naming it. ticket_notes is keyed by seq and links
    /// by both ends; they're read separately.</summary>
    private static readonly (string Table, string Column)[] TicketKeyed =
        [("tickets", "id"), ("ticket_names", "ticket_id"), ("ticket_descriptions", "ticket_id"), ("ticket_labels", "ticket_id"),
         ("ticket_queue", "ticket_id"), ("ticket_work", "ticket_id")];

    private static readonly string[] VersionedTables = [.. TicketKeyed.Select(t => t.Table), "ticket_notes", "links"];

    /// <summary>The store's version now: SQL Server's change-tracking version (0 while it tracks none); on SQLite, when the
    /// file was last written, which moves at every write but answers no <see cref="ChangedSince"/> but a full reload.</summary>
    public long CurrentVersion()
    {
        if (server is null) return File.GetLastWriteTimeUtc(Path).Ticks;
        return Scalar("SELECT CHANGE_TRACKING_CURRENT_VERSION()") is { } v and not DBNull ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// The tickets changed after <paramref name="version"/>, each with the version it last changed at, from one query over the
    /// change tables. A full reload when the store can't answer from <paramref name="version"/> (see <see cref="TicketChanges"/>).
    /// </summary>
    public TicketChanges ChangedSince(long version)
    {
        if (server is null || !tracked) return new(CurrentVersion(), true, new Dictionary<string, long>());
        var sql = new StringBuilder();
        sql.Append("SELECT CHANGE_TRACKING_CURRENT_VERSION(), (SELECT MAX(m.v) FROM (VALUES ")
           .AppendJoin(", ", VersionedTables.Select(t => $"(CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'{t}')))")).Append(") AS m(v));\n");
        var branches = TicketKeyed.Select(t => $"SELECT UPPER(c.{t.Column}) COLLATE NOCASE AS id, c.SYS_CHANGE_VERSION AS v FROM CHANGETABLE(CHANGES {t.Table}, $v) AS c")
            .Append("SELECT UPPER(n.ticket_id) COLLATE NOCASE, c.SYS_CHANGE_VERSION FROM CHANGETABLE(CHANGES ticket_notes, $v) AS c JOIN ticket_notes n ON n.seq = c.seq")
            .Append("SELECT UPPER(c.from_id) COLLATE NOCASE, c.SYS_CHANGE_VERSION FROM CHANGETABLE(CHANGES links, $v) AS c")
            .Append("SELECT UPPER(c.to_id) COLLATE NOCASE, c.SYS_CHANGE_VERSION FROM CHANGETABLE(CHANGES links, $v) AS c");
        sql.Append("SELECT x.id, MAX(x.v) FROM (").AppendJoin(" UNION ALL ", branches).Append(") AS x GROUP BY x.id");

        using var cmd = Command(sql.ToString(), null, [("$v", version)]);
        using var r = cmd.ExecuteReader();
        r.Read();
        long current = r.IsDBNull(0) ? 0 : r.GetInt64(0), minValid = r.IsDBNull(1) ? 0 : r.GetInt64(1);
        if (current == 0 || version < minValid || version > current) return new(current, true, new Dictionary<string, long>());
        r.NextResult();
        var changed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) changed[r.GetString(0)] = r.GetInt64(1);
        return new(current, false, changed);
    }

    /// <summary>The minimal row of each of these tickets (as stored, without following reassignments), by ID in any case, a few
    /// hundred to a query; an ID with no ticket isn't there.</summary>
    public IReadOnlyDictionary<string, TicketRow> Rows(IEnumerable<string> ids) =>
        RowsIn(TicketRowSql + "WHERE t.id IN {0}", ids, ReadTicketRow).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every ticket's minimal row, by ID in any case.</summary>
    public IReadOnlyDictionary<string, TicketRow> AllRows() =>
        Rows(TicketRowSql, ReadTicketRow).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

    private const string TicketRowSql =
        "SELECT t.id, t.repo, t.status, t.summary, n.name, " +
        "(SELECT MIN(l.label) FROM ticket_labels l WHERE l.ticket_id = t.id COLLATE NOCASE AND l.label LIKE 'level:%'), " +
        "(SELECT MIN(k.to_id) FROM links k WHERE k.from_id = t.id AND k.kind = 'part-of'), " +
        "COALESCE(q.priority, 0), COALESCE(q.approval, 'proposed'), w.assignee, w.login, d.body " +
        "FROM tickets t LEFT JOIN ticket_names n ON n.ticket_id = t.id COLLATE NOCASE " +
        "LEFT JOIN ticket_queue q ON q.ticket_id = t.id COLLATE NOCASE LEFT JOIN ticket_work w ON w.ticket_id = t.id COLLATE NOCASE " +
        "LEFT JOIN ticket_descriptions d ON d.ticket_id = t.id COLLATE NOCASE ";

    private static TicketRow ReadTicketRow(System.Data.Common.DbDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var row = new TicketRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(4), NormalizeLevel(S(5)), S(6),
            r.GetInt64(7), r.GetString(8), S(9), S(10), S(11), "");
        return row with { Checksum = Checksum(row) };
    }

    /// <summary>
    /// A row's checksum: the first 16 hex digits of SHA-256 over its fields (not its own checksum), each with a mark for
    /// null, so the same fields give the same checksum in any process, and any field changed gives another.
    /// </summary>
    public static string Checksum(TicketRow row)
    {
        var sb = new StringBuilder();
        foreach (var f in (string?[])[row.Id.ToUpperInvariant(), row.Repo, row.Status, row.Summary, row.Name, row.Level, row.Parent?.ToUpperInvariant(),
                     row.Priority.ToString(CultureInfo.InvariantCulture), row.Approval, row.Assignee, row.Login, row.Description])
            sb.Append(f is null ? "\u0000" : "\u0001" + f).Append('\u001f');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
