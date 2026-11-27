using System.Globalization;

namespace MindAttic.Tickets.Data;

/// <summary>One index the nightly maintenance looked at: how fragmented it is and what was (or, on a dry run, would be) done to it.</summary>
public sealed record IndexWork(string Table, string Index, double Fragmentation, long Pages, string Action);

/// <summary>Tickets whose checksum differs between the server and the copy (or that only one of them holds).</summary>
public sealed record CopyMismatches(long Compared, IReadOnlyList<string> Ids);

public sealed partial class TicketsDb
{
    /// <summary>Opens the SQLite file at <paramref name="path"/> as the store, whatever server this machine has configured.</summary>
    public static TicketsDb OpenFile(string path) => new(path, null, false);

    /// <summary>Compares every ticket's checksum on this store with the same ticket in <paramref name="copy"/>.</summary>
    public CopyMismatches CompareTo(TicketsDb copy)
    {
        var mine = AllRows();
        var theirs = copy.AllRows();
        var bad = mine.Where(m => !theirs.TryGetValue(m.Key, out var t) || t.Checksum != m.Value.Checksum).Select(m => m.Key)
            .Concat(theirs.Keys.Where(k => !mine.ContainsKey(k))).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new(mine.Count, bad);
    }

    /// <summary>
    /// Index maintenance on the server: an index over <paramref name="minPages"/> pages that's fragmented more than
    /// <paramref name="rebuildAbove"/> percent is rebuilt, one over <paramref name="reorganizeAbove"/> percent is reorganized,
    /// then the statistics of every table are updated. With <paramref name="dryRun"/> nothing is changed.
    /// </summary>
    public IReadOnlyList<IndexWork> MaintainIndexes(double rebuildAbove, double reorganizeAbove, long minPages, bool dryRun, out int statisticsUpdated)
    {
        statisticsUpdated = 0;
        if (server is null) throw new InvalidOperationException("Index maintenance needs the SQL Server store.");
        var found = new List<(string Schema, string Table, string Index, double Frag, long Pages)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = ambient;
            cmd.CommandText = "SELECT SCHEMA_NAME(o.schema_id), o.name, i.name, s.avg_fragmentation_in_percent, s.page_count " +
                "FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') s " +
                "JOIN sys.indexes i ON i.object_id = s.object_id AND i.index_id = s.index_id JOIN sys.objects o ON o.object_id = s.object_id " +
                "WHERE i.index_id > 0 AND i.name IS NOT NULL AND o.type = 'U' ORDER BY o.name, i.name";
            cmd.CommandTimeout = 0;
            using var r = cmd.ExecuteReader();
            while (r.Read()) found.Add((r.GetString(0), r.GetString(1), r.GetString(2), Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture), Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture)));
        }

        var work = new List<IndexWork>();
        foreach (var (schema, table, index, frag, pages) in found)
        {
            if (pages <= minPages || frag <= reorganizeAbove) continue;
            var rebuild = frag > rebuildAbove;
            work.Add(new(table, index, frag, pages, rebuild ? "REBUILD" : "REORGANIZE"));
            if (!dryRun) Run($"ALTER INDEX [{index}] ON [{schema}].[{table}] {(rebuild ? "REBUILD WITH (ONLINE = OFF)" : "REORGANIZE")}");
        }

        foreach (var t in TableOrder)
        {
            if (Scalar("SELECT 1 FROM sys.tables WHERE name = $t", null, ("$t", t)) is null) continue;
            statisticsUpdated++;
            if (!dryRun) Run($"UPDATE STATISTICS [{t}]");
        }
        return work;
    }

    private void Run(string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = ambient;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        cmd.ExecuteNonQuery();
    }
}
