using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// One version of a ticket (M.TIX-T137): how it read from <see cref="At"/> until the next version, numbered from 1 (as filed)
/// up to the current one. <see cref="Changed"/> names what differs from the version before (<see cref="TicketsDb.VersionFields"/>).
/// <see cref="Recorded"/>: read from SQL Server's system-versioned tables, every save as it was; false, worked back from the
/// ticket's history events (the SQLite file, or a server not yet versioned), its links shown as they are now.
/// <see cref="Assignee"/> and <see cref="Reserved"/> are its work (M.TIX-T134) then.
/// </summary>
public sealed record TicketVersion(int Number, DateTime At, string Repo, Ticket Ticket, string Approval, long Priority,
    IReadOnlyList<Link> Links, IReadOnlyList<string> Changed, bool Recorded, string? Assignee = null, long Reserved = 0);

public sealed partial class TicketsDb
{
    /// <summary>What a version can change, as <see cref="TicketVersion.Changed"/> names it.</summary>
    public static readonly IReadOnlyList<string> VersionFields = ["id", "repo", "type", "status", "summary", "ref", "approval", "priority", "assignee", "reserved", "links"];

    /// <summary>
    /// Every version of the ticket <paramref name="id"/> (or the one a former ID became), oldest first, the last the current
    /// one. On SQL Server once versioned, each is the ticket's row, approval, priority, assignee, reservation and links as
    /// they stood at one save (<c>FOR SYSTEM_TIME ALL</c>), following it across ID changes; otherwise each status, ref, type,
    /// repo, ID, approval, priority, assignee or reservation change in its history makes one. (Which session is working it
    /// is kept too, but a claim or release alone isn't a new version of the ticket.)
    /// </summary>
    public IReadOnlyList<TicketVersion> Versions(string id)
    {
        var (repo, t) = Ticket(id) ?? throw new KeyNotFoundException($"No ticket {id}.");
        var drafts = IsVersioned() ? RecordedVersions(t) : new List<Draft>();
        if (drafts.Count == 0) drafts = VersionsFromEvents(repo, t);
        var versions = new List<TicketVersion>();
        Draft? last = null;
        foreach (var d in drafts)
        {
            var changed = last is null ? new List<string>() : Changes(last, d);
            if (last is not null && changed.Count == 0) continue; // a save that changed none of it (row_md, updated)
            versions.Add(new TicketVersion(versions.Count + 1, d.At, d.Repo, d.Ticket, d.Approval, d.Priority, d.Links, changed, d.Recorded, d.Assignee, d.Reserved));
            last = d;
        }
        return versions;
    }

    /// <summary>Whether the store keeps every version (SQL Server, its tables system-versioned).</summary>
    public bool IsVersioned()
    {
        if (server is null) return false;
        if (!versioned) versioned = IsVersioned(db, ambient);
        return versioned;
    }

    private bool versioned;

    private sealed record Draft(DateTime At, string Repo, Ticket Ticket, string Approval, long Priority, IReadOnlyList<Link> Links, bool Recorded,
        string? Assignee, long Reserved);

    private static List<string> Changes(Draft a, Draft b)
    {
        var changed = new List<string>();
        if (a.Ticket.Id != b.Ticket.Id) changed.Add("id");
        if (a.Repo != b.Repo) changed.Add("repo");
        if (a.Ticket.Type != b.Ticket.Type) changed.Add("type");
        if (a.Ticket.Status != b.Ticket.Status) changed.Add("status");
        if (a.Ticket.Summary != b.Ticket.Summary) changed.Add("summary");
        if (a.Ticket.Ref != b.Ticket.Ref) changed.Add("ref");
        if (a.Approval != b.Approval) changed.Add("approval");
        if (a.Priority != b.Priority) changed.Add("priority");
        if (a.Assignee != b.Assignee) changed.Add("assignee");
        if (a.Reserved != b.Reserved) changed.Add("reserved");
        if (!a.Links.SequenceEqual(b.Links)) changed.Add("links");
        return changed;
    }

    private List<Draft> RecordedVersions(Ticket t)
    {
        // The ticket under every ID it has had: its versions before a reassignment are kept under the old one.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { t.Id };
        foreach (var c in IdHistory(t.Id)) { ids.Add(c.OldId); ids.Add(c.NewId); }
        var names = ids.Select((x, i) => ("$i" + i.ToString(CultureInfo.InvariantCulture), (object?)x)).ToArray();
        var list = "(" + string.Join(", ", names.Select(n => n.Item1)) + ")";
        var rows = Rows($"SELECT repo, id, date, type, status, summary, ref, valid_from, valid_to FROM tickets FOR SYSTEM_TIME ALL WHERE id COLLATE NOCASE IN {list}",
            r => (Repo: r.GetString(0), Ticket: ReadTicket(r, 1), From: Utc(r.GetDateTime(7)), To: Utc(r.GetDateTime(8))), names);
        var queue = Rows($"SELECT approval, priority, valid_from, valid_to FROM ticket_queue FOR SYSTEM_TIME ALL WHERE ticket_id IN {list}",
            r => (Approval: r.GetString(0), Priority: r.GetInt64(1), From: Utc(r.GetDateTime(2)), To: Utc(r.GetDateTime(3))), names);
        var work = Rows($"SELECT assignee, reserved, valid_from, valid_to FROM ticket_work FOR SYSTEM_TIME ALL WHERE ticket_id IN {list}",
            r => (Assignee: r.IsDBNull(0) ? null : r.GetString(0), Reserved: r.GetInt64(1), From: Utc(r.GetDateTime(2)), To: Utc(r.GetDateTime(3))), names);
        var links = Rows($"SELECT from_id, to_id, kind, note, valid_from, valid_to FROM links FOR SYSTEM_TIME ALL " +
                         $"WHERE from_id COLLATE NOCASE IN {list} OR to_id COLLATE NOCASE IN {list}",
            r => (Link: ReadLink(r), From: Utc(r.GetDateTime(4)), To: Utc(r.GetDateTime(5))), names);
        if (rows.Count == 0) return [];

        static bool Holds(DateTime from, DateTime to, DateTime at) => from <= at && at < to;
        var times = rows.SelectMany(x => new[] { x.From, x.To })
            .Concat(queue.SelectMany(q => new[] { q.From, q.To }))
            .Concat(work.SelectMany(w => new[] { w.From, w.To }))
            .Concat(links.SelectMany(l => new[] { l.From, l.To }))
            .Where(x => x.Year < 9999).Distinct().Order();
        var drafts = new List<Draft>();
        foreach (var at in times)
        {
            if (rows.Where(x => Holds(x.From, x.To, at)).Select(x => ((string Repo, Ticket Ticket)?)(x.Repo, x.Ticket)).FirstOrDefault() is not { } row) continue;
            var q = queue.Where(x => Holds(x.From, x.To, at)).Select(x => ((string, long)?)(x.Approval, x.Priority)).FirstOrDefault() ?? (Proposed, 0L);
            var held = links.Where(x => Holds(x.From, x.To, at)).Select(x => x.Link)
                .OrderBy(l => l.Kind, StringComparer.Ordinal).ThenBy(l => l.From, StringComparer.Ordinal).ThenBy(l => l.To, StringComparer.Ordinal).ToList();
            var w = work.Where(x => Holds(x.From, x.To, at)).Select(x => ((string?, long)?)(x.Assignee, x.Reserved)).FirstOrDefault() ?? (null, 0L);
            drafts.Add(new Draft(at, row.Repo, row.Ticket, q.Item1, q.Item2, held, Recorded: true, w.Item1, w.Item2));
        }
        return drafts;
    }

    private List<Draft> VersionsFromEvents(string repo, Ticket t)
    {
        var events = History(t.Id);
        var (approval, priority) = QueueState(t.Id);
        var links = Links(t.Id);
        var work = Work(t.Id);
        var current = new Shape(t.Id, repo, t.Type, t.Status, t.Ref, approval, priority, work.Assignee, work.Reserved, t.Summary);
        return [.. Timeline(current, Created(t.Date, events), events, AllShapeFields, DateTime.UtcNow)
            .Select(x => new Draft(x.At, x.State.Repo, t with { Id = x.State.Id, Type = x.State.Type, Status = x.State.Status, Ref = x.State.Ref, Summary = x.State.Summary ?? t.Summary },
                x.State.Approval, x.State.Priority, links, Recorded: false, x.State.Assignee, x.State.Reserved))];
    }

    // ── Working a ticket's past back from its history events ───────────────

    /// <summary>What a ticket's history events say it was: everything but its date and ordinal, which never change (its summary
    /// only by a recorded correction, M.TIX-T73; null where the caller doesn't follow it).</summary>
    internal sealed record Shape(string Id, string Repo, string Type, string Status, string Ref, string Approval, long Priority,
        string? Assignee = null, long Reserved = 0, string? Summary = null);

    private static readonly HashSet<string> AllShapeFields = ["status", "ref", "type", "summary", "repo", "id", "approval", "priority", "assignee", "reserved"];
    private static readonly HashSet<string> TicketShapeFields = ["status", "ref", "type", "summary", "repo", "id"];
    private static readonly HashSet<string> QueueShapeFields = ["approval", "priority"];
    private static readonly HashSet<string> WorkShapeFields = ["assignee", "reserved"];

    /// <summary>
    /// The states <paramref name="current"/> went through, oldest first: the first as filed (from <paramref name="created"/>),
    /// then one from each moment an event in <paramref name="fields"/> changed it (events at one moment are one save), each
    /// worked back from the one after by the events' old values. Times never pass <paramref name="ceiling"/> and always rise.
    /// </summary>
    internal static List<(DateTime At, Shape State)> Timeline(Shape current, DateTime created, IReadOnlyList<TicketEvent> events, IReadOnlySet<string> fields, DateTime ceiling)
    {
        var saves = events.Where(e => fields.Contains(e.Field)).GroupBy(e => e.At, StringComparer.Ordinal)
            .Select(g => (At: ParseAt(g.Key) ?? created, Events: g.ToList())).ToList();
        var states = new Shape[saves.Count + 1];
        states[saves.Count] = current;
        for (var i = saves.Count - 1; i >= 0; i--)
        {
            var s = states[i + 1];
            for (var j = saves[i].Events.Count - 1; j >= 0; j--) s = Before(s, saves[i].Events[j]);
            states[i] = s;
        }
        var times = new DateTime[saves.Count + 1];
        times[0] = saves.Select(x => x.At).Append(created).Min();
        for (var i = 0; i < saves.Count; i++) times[i + 1] = saves[i].At;
        for (var i = 0; i < times.Length; i++)
        {
            if (times[i] > ceiling) times[i] = ceiling;
            if (i > 0 && times[i] <= times[i - 1]) times[i] = times[i - 1].AddTicks(1);
        }
        return [.. states.Select((s, i) => (times[i], s))];
    }

    private static Shape Before(Shape s, TicketEvent e) => e.Field switch
    {
        "status" when e.Old is { } o => s with { Status = o },
        "ref" when e.Old is { } o => s with { Ref = o },
        "type" when e.Old is { } o => s with { Type = o },
        "summary" when e.Old is { } o => s with { Summary = o },
        "repo" when e.Old is { } o => s with { Repo = o },
        "id" when e.Old is { } o => s with { Id = o },
        "approval" when e.Old is { } o => s with { Approval = o },
        "priority" when long.TryParse(e.Old, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => s with { Priority = p },
        "assignee" => s with { Assignee = e.Old }, // none before is null
        "reserved" when long.TryParse(e.Old, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) => s with { Reserved = r },
        _ => s,
    };

    /// <summary>When a ticket was filed: its 'created' event, else the start of its date.</summary>
    private static DateTime Created(string date, IEnumerable<TicketEvent> events) =>
        events.Where(e => e.Field == "created").Select(e => ParseAt(e.At)).FirstOrDefault(x => x is not null) ?? ParseAt(date) ?? DateTime.UtcNow;

    /// <summary>An event's time (<c>yyyy-MM-ddTHH:mm:ssZ</c>) or a ticket's date, as UTC.</summary>
    internal static DateTime? ParseAt(string? at) =>
        DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);

    // ── The versioning step (schema 5) on SQL Server ────────────────────────

    /// <summary>Why the versioning step couldn't run at the last open (its backup failed), or null.</summary>
    public static string? VersioningError { get; private set; }

    /// <summary>The backup the versioning step made of the server database before it changed the schema, once it has run.</summary>
    public static string? VersioningBackup { get; private set; }

    private static bool IsVersioned(DbConnection connection, DbTransaction? tx) =>
        Convert.ToInt64(ScalarOn(connection, tx,
            "SELECT COUNT_BIG(*) FROM sys.tables WHERE temporal_type = 2 AND name IN (" +
            string.Join(", ", TicketsServer.Versioned.Select(v => $"N'{v.Table}'")) + ")"), CultureInfo.InvariantCulture) == TicketsServer.Versioned.Length;

    private static long CountOn(DbConnection connection, DbTransaction? tx, string table) =>
        Convert.ToInt64(ScalarOn(connection, tx, $"SELECT COUNT_BIG(*) FROM {table}"), CultureInfo.InvariantCulture);

    /// <summary>
    /// Schema 5 (M.TIX-T137): makes <see cref="TicketsServer.Versioned"/> SYSTEM_VERSIONED temporal tables, each with its
    /// history table, so every save keeps the row it replaced with its period, never pruned. A database holding tickets is
    /// backed up first (<c>BACKUP DATABASE</c>, copy-only, verified). The first versions are worked back from the ticket
    /// events: each ticket's states before its last status, ref, type, repo or ID change go in as history rows, its current
    /// row dated from that change, and its approval and priority likewise; a link is dated from when the later of its
    /// tickets was filed. Every table's rows are counted before and after, and the step rolls back if a count moved.
    /// Runs once, under the write lock; a second process opening meanwhile finds it done.
    /// </summary>
    private static void Versioning(SqlConnection c, string describe, bool backup)
    {
        if (IsVersioned(c, null)) return;
        string? backupPath = null;
        if (backup && CountOn(c, null, "tickets") > 0)
        {
            try { backupPath = BackupServerDatabase(c); }
            catch (SqlException ex)
            {
                // Without a backup the schema isn't touched: the tickets work as before, unversioned, and this says why.
                VersioningError = $"{describe} isn't versioned yet: its backup failed ({ex.Message})";
                return;
            }
        }

        using var tx = c.BeginTransaction();
        Raw(c, tx, "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'MindAttic.Tickets.write', @LockMode = N'Exclusive', " +
                   "@LockOwner = N'Transaction', @LockTimeout = 60000; IF @r < 0 THROW 50000, 'the ticket database stayed busy', 1;");
        if (IsVersioned(c, tx)) { tx.Commit(); return; }
        string[] counted = ["tickets", "ticket_events", "ticket_queue", "ticket_work", "links", "repos", "id_history"];
        var before = counted.ToDictionary(t => t, t => CountOn(c, tx, t));
        var now = Utc((DateTime)ScalarOn(c, tx, "SELECT SYSUTCDATETIME()")!);
        var todo = TicketsServer.Versioned.Where(v => Convert.ToInt32(ScalarOn(c, tx,
            $"SELECT COUNT(*) FROM sys.tables WHERE name = N'{v.Table}' AND temporal_type = 2"), CultureInfo.InvariantCulture) == 0).ToList();

        // A system-versioned table takes no INSTEAD OF trigger: the guard against deletes comes back as an AFTER trigger below.
        Raw(c, tx, "DISABLE TRIGGER ALL ON tickets");
        Raw(c, tx, "IF OBJECT_ID(N'tickets_no_delete', N'TR') IS NOT NULL DROP TRIGGER tickets_no_delete");
        foreach (var v in todo)
        {
            Raw(c, tx, $"ALTER TABLE {v.Table} ADD valid_from datetime2 NOT NULL CONSTRAINT {v.Table}_valid_from DEFAULT SYSUTCDATETIME(), " +
                       $"valid_to datetime2 NOT NULL CONSTRAINT {v.Table}_valid_to DEFAULT CONVERT(datetime2, '9999-12-31 23:59:59.9999999')");
            Raw(c, tx, $"UPDATE {v.Table} SET valid_from = @now", ("@now", now));
            Raw(c, tx, $"IF OBJECT_ID(N'{v.History}', N'U') IS NULL SELECT TOP 0 * INTO {v.History} FROM {v.Table}");
        }
        var written = Backfill(c, tx, now, todo.Select(v => v.Table).ToHashSet(StringComparer.Ordinal));
        foreach (var v in todo)
        {
            Raw(c, tx, $"CREATE CLUSTERED INDEX {v.History}_by_period ON {v.History} (valid_to, valid_from)");
            Raw(c, tx, $"CREATE INDEX {v.History}_by_{v.Key[0]} ON {v.History} ({v.Key[0]})");
            Raw(c, tx, $"ALTER TABLE {v.Table} ADD PERIOD FOR SYSTEM_TIME (valid_from, valid_to)");
            Raw(c, tx, $"ALTER TABLE {v.Table} ALTER COLUMN valid_from ADD HIDDEN");
            Raw(c, tx, $"ALTER TABLE {v.Table} ALTER COLUMN valid_to ADD HIDDEN");
            Raw(c, tx, $"ALTER TABLE {v.Table} SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.{v.History}, DATA_CONSISTENCY_CHECK = ON))");
        }
        if (todo.Any(v => v.Table == "links")) Raw(c, tx, "CREATE INDEX links_versions_by_to_id ON links_versions (to_id)");
        Raw(c, tx, "ENABLE TRIGGER ALL ON tickets");
        ServerTriggers(c, tx);

        foreach (var t in counted)
            if (CountOn(c, tx, t) is var after && after != before[t])
                throw new InvalidOperationException($"Versioning {describe}: {t} held {before[t]} rows before and {after} after; nothing was changed.");
        foreach (var v in todo)
            if (CountOn(c, tx, v.History) is var there && there != written.GetValueOrDefault(v.History))
                throw new InvalidOperationException($"Versioning {describe}: {written.GetValueOrDefault(v.History)} first versions worked out for {v.Table} but {there} written; nothing was changed.");
        tx.Commit();
        VersioningBackup = backupPath;
        VersioningError = null;
    }

    /// <summary>A copy-only backup of the server database in the instance's backup folder, verified; returns its path.</summary>
    private static string BackupServerDatabase(SqlConnection c)
    {
        var folder = ScalarOn(c, null, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))") as string;
        var name = c.Database;
        var file = $"{name}.{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.before-versioning.bak";
        var path = string.IsNullOrEmpty(folder) ? file : System.IO.Path.Combine(folder, file);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandTimeout = 600;
            cmd.CommandText = "BACKUP DATABASE @db TO DISK = @path WITH COPY_ONLY, INIT, CHECKSUM; RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;";
            cmd.Parameters.AddWithValue("@db", name);
            cmd.Parameters.AddWithValue("@path", path);
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    /// <summary>The first versions (see <see cref="Versioning"/>); returns the history rows written to each history table.</summary>
    private static Dictionary<string, long> Backfill(SqlConnection c, SqlTransaction tx, DateTime now, IReadOnlySet<string> tables)
    {
        var written = new Dictionary<string, long>(StringComparer.Ordinal);
        List<T> Read<T>(string sql, Func<DbDataReader, T> read)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(read(r));
            return list;
        }
        static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        var tickets = Read("SELECT id, repo, number, date, type, status, summary, ref, ordinal FROM tickets",
            r => (Id: r.GetString(0), Repo: r.GetString(1), Number: r.IsDBNull(2) ? (long?)null : r.GetInt64(2), Date: r.GetString(3), Type: r.GetString(4),
                  Status: r.GetString(5), Summary: r.GetString(6), Ref: r.GetString(7), Ordinal: r.GetInt64(8)));
        var events = Read("SELECT ticket_id, at, field, old, new FROM ticket_events ORDER BY seq",
                r => new TicketEvent(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), Str(r, 4)))
            .ToLookup(e => e.TicketId, StringComparer.OrdinalIgnoreCase);
        var created = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tickets)
        {
            var at = Created(t.Date, events[t.Id]);
            created[t.Id] = at > now ? now : at;
        }

        // Each table's first versions go in by bulk copy; the current rows' new start times by a join on a scratch table.
        void Bulk(string table, DataTable rows)
        {
            written[table] = rows.Rows.Count;
            if (rows.Rows.Count == 0) return;
            using var copy = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx) { DestinationTableName = table, BulkCopyTimeout = 600 };
            foreach (DataColumn col in rows.Columns) copy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            copy.WriteToServer(rows);
        }
        void Restamp(string table, string[] key, DataTable starts)
        {
            if (starts.Rows.Count == 0) return;
            var scratch = "#" + table + "_starts";
            Raw(c, tx, $"CREATE TABLE {scratch} ({string.Join(", ", key.Select(k => $"{k} nvarchar(450) COLLATE DATABASE_DEFAULT NOT NULL"))}, valid_from datetime2 NOT NULL)");
            using (var copy = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx) { DestinationTableName = scratch, BulkCopyTimeout = 600 })
            {
                foreach (DataColumn col in starts.Columns) copy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                copy.WriteToServer(starts);
            }
            Raw(c, tx, $"UPDATE t SET valid_from = s.valid_from FROM {table} t JOIN {scratch} s ON " + string.Join(" AND ", key.Select(k => $"t.{k} COLLATE DATABASE_DEFAULT = s.{k}")));
            Raw(c, tx, $"DROP TABLE {scratch}");
        }
        static DataTable Table(params (string Name, Type Type)[] columns)
        {
            var d = new DataTable();
            foreach (var (name, type) in columns) d.Columns.Add(name, type);
            return d;
        }

        if (tables.Contains("tickets"))
        {
            var history = Table(("id", typeof(string)), ("repo", typeof(string)), ("number", typeof(long)), ("date", typeof(string)), ("type", typeof(string)),
                ("status", typeof(string)), ("summary", typeof(string)), ("ref", typeof(string)), ("ordinal", typeof(long)), ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)));
            var starts = Table(("id", typeof(string)), ("valid_from", typeof(DateTime)));
            foreach (var t in tickets)
            {
                var line = Timeline(new Shape(t.Id, t.Repo, t.Type, t.Status, t.Ref, Proposed, 0, Summary: t.Summary), created[t.Id], [.. events[t.Id]], TicketShapeFields, now);
                for (var i = 0; i < line.Count - 1; i++)
                {
                    var s = line[i].State;
                    history.Rows.Add(s.Id, s.Repo, (object?)TicketRules.ParseNumber(s.Id) ?? (object?)t.Number ?? DBNull.Value, t.Date, s.Type, s.Status, s.Summary ?? t.Summary, s.Ref, t.Ordinal,
                        line[i].At, line[i + 1].At);
                }
                starts.Rows.Add(t.Id, line[^1].At);
            }
            Bulk("tickets_versions", history);
            Restamp("tickets", ["id"], starts);
        }

        if (tables.Contains("ticket_queue"))
        {
            var history = Table(("ticket_id", typeof(string)), ("approval", typeof(string)), ("priority", typeof(long)), ("updated", typeof(string)),
                ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)));
            var starts = Table(("ticket_id", typeof(string)), ("valid_from", typeof(DateTime)));
            foreach (var (ticketId, approval, priority, updated) in Read("SELECT ticket_id, approval, priority, updated FROM ticket_queue",
                         r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3))))
            {
                var from = created.TryGetValue(ticketId, out var filed) ? filed : ParseAt(updated) is { } u && u < now ? u : now;
                var line = Timeline(new Shape(ticketId, "", "", "", "", approval, priority), from, [.. events[ticketId]], QueueShapeFields, now);
                // The first state is the ticket before anything queued it: no row then (proposed at 0).
                for (var i = 1; i < line.Count - 1; i++)
                    history.Rows.Add(ticketId, line[i].State.Approval, line[i].State.Priority,
                        line[i].At.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), line[i].At, line[i + 1].At);
                var start = line.Count > 1 ? line[^1].At : ParseAt(updated) is { } at && at < now ? at : now;
                starts.Rows.Add(ticketId, start);
            }
            Bulk("ticket_queue_versions", history);
            Restamp("ticket_queue", ["ticket_id"], starts);
        }

        if (tables.Contains("ticket_work"))
        {
            // Assignee and reservation are worked back from their events; which session worked it before now isn't known.
            var history = Table(("ticket_id", typeof(string)), ("assignee", typeof(string)), ("reserved", typeof(long)), ("updated", typeof(string)),
                ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)));
            var starts = Table(("ticket_id", typeof(string)), ("valid_from", typeof(DateTime)));
            foreach (var (ticketId, assignee, reserved, updated) in Read("SELECT ticket_id, assignee, reserved, updated FROM ticket_work",
                         r => (r.GetString(0), Str(r, 1), r.GetInt64(2), r.GetString(3))))
            {
                var from = created.TryGetValue(ticketId, out var filed) ? filed : ParseAt(updated) is { } u && u < now ? u : now;
                var line = Timeline(new Shape(ticketId, "", "", "", "", Proposed, 0, assignee, reserved), from, [.. events[ticketId]], WorkShapeFields, now);
                for (var i = 1; i < line.Count - 1; i++)
                    history.Rows.Add(ticketId, (object?)line[i].State.Assignee ?? DBNull.Value, line[i].State.Reserved,
                        line[i].At.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), line[i].At, line[i + 1].At);
                starts.Rows.Add(ticketId, line.Count > 1 ? line[^1].At : ParseAt(updated) is { } at && at < now ? at : now);
            }
            Bulk("ticket_work_versions", history);
            Restamp("ticket_work", ["ticket_id"], starts);
        }

        if (tables.Contains("links"))
        {
            written["links_versions"] = 0;
            var starts = Table(("from_id", typeof(string)), ("to_id", typeof(string)), ("kind", typeof(string)), ("valid_from", typeof(DateTime)));
            foreach (var (from, to, kind) in Read("SELECT from_id, to_id, kind FROM links", r => (r.GetString(0), r.GetString(1), r.GetString(2))))
            {
                var ends = new[] { from, to }.Where(created.ContainsKey).Select(x => created[x]).ToList();
                if (ends.Count > 0) starts.Rows.Add(from, to, kind, ends.Max());
            }
            Restamp("links", ["from_id", "to_id", "kind"], starts);
        }
        return written;
    }

    // ── Checking the versions ───────────────────────────────────────────────

    /// <summary>
    /// Problems with the tickets' versions (optionally one repo's): on SQL Server, a table that isn't system-versioned, a
    /// history row whose period runs backwards, or one for a ticket that's gone; and in either store, a ticket whose last
    /// status, ref, approval or priority change in its history isn't what it says now (its versions are worked back from
    /// those). Per-ticket problems start with the repo's name.
    /// </summary>
    public IReadOnlyList<string> CheckVersions(string? repo = null)
    {
        var problems = new List<string>();
        if (server is not null)
        {
            if (VersioningError is { } error) problems.Add($"versions: {error}");
            foreach (var v in TicketsServer.Versioned)
            {
                var row = Rows($"SELECT t.temporal_type, h.name FROM sys.tables t LEFT JOIN sys.tables h ON h.object_id = t.history_table_id WHERE t.name = N'{v.Table}'",
                    r => (Type: Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture), History: r.IsDBNull(1) ? null : r.GetString(1))).FirstOrDefault();
                if (row.Type != 2) { problems.Add($"versions: {v.Table} isn't system-versioned, so its saves aren't kept"); continue; }
                if (!string.Equals(row.History, v.History, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"versions: {v.Table} keeps its history in {row.History ?? "nothing"}, not {v.History}");
                if (Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {v.History} WHERE valid_to < valid_from"), CultureInfo.InvariantCulture) is var backwards and > 0)
                    problems.Add($"versions: {backwards} row(s) of {v.History} end before they start");
            }
            if (IsVersioned())
            {
                var gone = Rows("SELECT DISTINCT h.id FROM tickets_versions h WHERE NOT EXISTS (SELECT 1 FROM tickets t WHERE t.id = h.id) " +
                                "AND NOT EXISTS (SELECT 1 FROM id_history i WHERE i.kind = 'ticket' AND i.old_id = h.id)", r => r.GetString(0));
                foreach (var id in gone) problems.Add($"versions: {id} has versions but no ticket (tickets are never deleted)");
            }
        }

        var tickets = Tickets(repo);
        var states = QueueStates();
        var last = Rows("SELECT ticket_id, field, new FROM ticket_events WHERE field IN ('status', 'ref', 'approval', 'priority') ORDER BY seq",
                r => (Id: r.GetString(0), Field: r.GetString(1), New: r.IsDBNull(2) ? null : r.GetString(2)))
            .GroupBy(e => (e.Id.ToUpperInvariant(), e.Field)).ToDictionary(g => g.Key, g => g.Last().New);
        foreach (var (name, t) in tickets)
        {
            var (approval, priority) = states.TryGetValue(t.Id, out var s) ? s : (Proposed, 0L);
            foreach (var (field, now) in new[] { ("status", t.Status), ("ref", t.Ref), ("approval", approval), ("priority", priority.ToString(CultureInfo.InvariantCulture)) })
                if (last.TryGetValue((t.Id.ToUpperInvariant(), field), out var said) && said != now)
                    problems.Add($"{name}: {t.Id}'s history last set its {field} to '{said}', but it's '{now}'");
        }
        return problems;
    }
}
