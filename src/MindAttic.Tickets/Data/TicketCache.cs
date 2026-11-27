using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MindAttic.Tickets.Data;

/// <summary>
/// One state of a <see cref="TicketCache"/>: the store version it's complete up to (0: never loaded), every ticket's minimal
/// row by ID in any case, and each row's version, the store version it is known current at.
/// </summary>
public sealed record TicketCacheSnapshot(long Version, ImmutableDictionary<string, TicketRow> Rows, ImmutableDictionary<string, long> RowVersions)
{
    public static readonly TicketCacheSnapshot Empty =
        new(0, ImmutableDictionary.Create<string, TicketRow>(StringComparer.OrdinalIgnoreCase), ImmutableDictionary.Create<string, long>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Every ticket's <see cref="TicketRow"/>, kept current by asking only what changed (M.TIX-T415): a <see cref="Refresh()"/>
/// with nothing new is one <see cref="TicketsDb.CurrentVersion"/> scalar; otherwise <see cref="TicketsDb.ChangedSince"/> and
/// <see cref="TicketsDb.Rows(IEnumerable{string})"/> for just those tickets, or every row again when the store says to reload.
/// <see cref="Apply(IEnumerable{string})"/> re-reads named tickets (a pushed ticket event). One instance per workspace root
/// (<see cref="For"/>), safe across threads: updates run one at a time, and readers take an immutable
/// <see cref="Snapshot"/> without waiting.
/// </summary>
public sealed class TicketCache
{
    private static readonly ConcurrentDictionary<string, TicketCache> instances = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<TicketsDb?> open;
    private readonly Lock gate = new();
    private TicketCacheSnapshot snapshot = TicketCacheSnapshot.Empty;

    /// <summary>A cache over the store <paramref name="open"/> opens for each update (null: not migrated yet, nothing read).</summary>
    public TicketCache(Func<TicketsDb?> open) => this.open = open;

    /// <summary>The process's cache for the workspace at <paramref name="workspaceRoot"/> (default: this machine's).</summary>
    public static TicketCache For(string? workspaceRoot = null)
    {
        var root = Path.GetFullPath(workspaceRoot ?? Workspace.DefaultRoot);
        return instances.GetOrAdd(root, r => new TicketCache(() => TicketsDb.OpenIfMigrated(r)));
    }

    public TicketCacheSnapshot Snapshot => Volatile.Read(ref snapshot);

    public TicketRow? Get(string id) => Snapshot.Rows.GetValueOrDefault(id);

    /// <summary>Brings the cache up to the store; whether anything was read beyond the version.</summary>
    public bool Refresh()
    {
        using var db = open();
        return db is not null && Refresh(db);
    }

    /// <inheritdoc cref="Refresh()"/>
    public bool Refresh(TicketsDb db)
    {
        lock (gate)
        {
            var s = snapshot;
            if (s.Version == 0) return Reload(db, db.CurrentVersion());
            if (db.CurrentVersion() == s.Version) return false;
            var changes = db.ChangedSince(s.Version);
            if (changes.FullReload) return Reload(db, changes.Version);
            var rows = db.Rows(changes.Changed.Keys);
            Publish(s with { Version = changes.Version }, changes.Changed.Keys, rows, id => changes.Changed[id]);
            return true;
        }
    }

    /// <summary>Re-reads <paramref name="ids"/> (a ticket gone from the store leaves the cache); the cache's version stays.</summary>
    public void Apply(IEnumerable<string> ids)
    {
        using var db = open();
        if (db is not null) Apply(db, ids);
    }

    /// <inheritdoc cref="Apply(IEnumerable{string})"/>
    public void Apply(TicketsDb db, IEnumerable<string> ids)
    {
        var list = ids.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        lock (gate)
        {
            var version = db.CurrentVersion();
            var rows = db.Rows(list);
            var s = snapshot;
            Publish(s, list, rows, id => Math.Max(version, s.RowVersions.GetValueOrDefault(id)));
        }
    }

    /// <summary>Every row read again; the version is read before the rows, so a change made meanwhile is read again next time.</summary>
    private bool Reload(TicketsDb db, long version)
    {
        var rows = db.AllRows();
        Volatile.Write(ref snapshot, new TicketCacheSnapshot(version,
            rows.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            rows.Keys.ToImmutableDictionary(k => k, _ => version, StringComparer.OrdinalIgnoreCase)));
        return true;
    }

    private void Publish(TicketCacheSnapshot s, IEnumerable<string> ids, IReadOnlyDictionary<string, TicketRow> rows, Func<string, long> versionOf)
    {
        var r = s.Rows.ToBuilder();
        var v = s.RowVersions.ToBuilder();
        foreach (var id in ids)
        {
            if (rows.TryGetValue(id, out var row))
            {
                r[row.Id] = row;
                v[row.Id] = versionOf(id);
            }
            else
            {
                r.Remove(id);
                v.Remove(id);
            }
        }
        Volatile.Write(ref snapshot, s with { Rows = r.ToImmutable(), RowVersions = v.ToImmutable() });
    }
}
