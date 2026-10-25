using System.IO;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>
/// Live refresh (M.TIX-T70): calls <c>changed</c> when the ticket database file changes, once it has been quiet for
/// <c>settle</c> (one change is several writes, and git replaces the whole file), and every <c>poll</c> regardless, in case
/// a change notification is missed. On SQL Server the file is still rewritten after every change, so the watch holds there
/// too. A watcher that fails (its buffer overflowed, the folder went away during a checkout) is started again, and the
/// change it may have missed is reported. <c>changed</c> runs on the thread pool; the window marshals it to the UI thread.
/// </summary>
public sealed class DatabaseWatch : IDisposable
{
    private readonly string path;
    private readonly TimeSpan settle;
    private readonly Action changed;
    private readonly Timer settleTimer, pollTimer;
    private readonly Lock gate = new();
    private FileSystemWatcher? watcher;
    private bool disposed;

    public DatabaseWatch(string path, TimeSpan settle, TimeSpan poll, Action changed)
    {
        this.path = Path.GetFullPath(path);
        this.settle = settle;
        this.changed = changed;
        settleTimer = new Timer(_ => Fire());
        pollTimer = new Timer(_ => { if (!Watching) Watch(); Fire(); }, null, poll, poll); // the folder may exist by now
        Watch();
    }

    /// <summary>Whether a file watcher is running (false while the database's folder doesn't exist; the poll still runs).</summary>
    public bool Watching
    {
        get { lock (gate) return watcher is not null; }
    }

    private void Watch()
    {
        lock (gate)
        {
            if (disposed) return;
            watcher?.Dispose();
            watcher = null;
            var dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return;
            var w = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            w.Changed += (_, _) => Touched();
            w.Created += (_, _) => Touched();
            w.Renamed += (_, _) => Touched();
            w.Error += (_, _) => { Watch(); Touched(); };
            w.EnableRaisingEvents = true;
            watcher = w;
        }
    }

    /// <summary>A change was seen: (re)start the settle delay, so a burst of writes refreshes once, after the last.</summary>
    private void Touched()
    {
        lock (gate)
            if (!disposed) settleTimer.Change(settle, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        lock (gate)
            if (disposed) return;
        changed();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            watcher?.Dispose();
            watcher = null;
        }
        settleTimer.Dispose();
        pollTimer.Dispose();
    }
}
