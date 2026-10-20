using System.Diagnostics;
using System.IO;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>One ticket with its queue state, as the views show it.</summary>
public sealed record TicketInfo(string Repo, Ticket Ticket, string Approval, long Priority);

/// <summary>Everything the window shows, read in one go.</summary>
public sealed record Snapshot(IReadOnlyList<string> Repos, IReadOnlyList<TicketInfo> Tickets, IReadOnlyList<TicketInfo> Queue, DateTime Read);

/// <summary>A ticket's detail: history and links (both directions) and what it's waiting for.</summary>
public sealed record TicketDetail(TicketInfo Info, IReadOnlyList<TicketEvent> History, IReadOnlyList<Link> Links, IReadOnlyList<string> OpenBlockers);

/// <summary>
/// The window's only way to the ticket database. Every call opens the database, does its work and closes it, so
/// git (which commits the file) and agents writing tickets are never held up by an open window. Every write then
/// re-renders the repos' generated USER_STORIES.md views, as the CLI does.
/// </summary>
public sealed class TicketStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string DbPath => TicketsDb.DefaultPath(Root);

    public bool Exists => File.Exists(DbPath);

    public Snapshot Load() => Read(db =>
    {
        var states = db.QueueStates();
        TicketInfo Info(string repo, Ticket t) =>
            states.TryGetValue(t.Id, out var s) ? new(repo, t, s.Approval, s.Priority) : new(repo, t, TicketsDb.Proposed, 0);
        return new Snapshot(
            [.. db.Repos().Select(r => r.Name)],
            [.. db.Tickets().Select(x => Info(x.Repo, x.Ticket))],
            [.. db.Queue().Select(q => new TicketInfo(q.Repo, q.Ticket, q.Approval, q.Priority))],
            DateTime.Now);
    });

    public TicketDetail? Detail(string id) => Read(db =>
    {
        if (db.Ticket(id) is not { } found) return null;
        var (approval, priority) = db.QueueState(found.Ticket.Id);
        var blockers = db.Requires(found.Ticket.Id, transitive: true)
            .Where(b => db.Ticket(b) is { } t && t.Ticket.Status == TicketRules.Open).ToList();
        return new TicketDetail(new TicketInfo(found.Repo, found.Ticket, approval, priority), db.History(found.Ticket.Id), db.Links(found.Ticket.Id), blockers);
    });

    public void Approve(string id) => Write($"approve {id}", db => db.Approve(id), render: false);
    public void Unapprove(string id) => Write($"unapprove {id}", db => db.Unapprove(id), render: false);
    public void Reject(string id, string reason) => Write($"reject {id}: {reason}", db => db.Reject(id, reason));
    public void Move(string id, IReadOnlyList<string> newOrder) =>
        Write($"move {id} to position {newOrder.ToList().IndexOf(id) + 1}", db => db.Move(id, newOrder), render: false);
    public void Close(string id, string status, string? reference) => Write($"{status} {id} ({reference})", db => db.Close(id, status, reference));
    public void CorrectType(string id, string type, string reason) => Write($"correct {id} to {type}: {reason}", db => db.CorrectType(id, type, reason));
    public void AddLink(string from, string to, string kind) => Write($"link {from} {kind} {to}", db => db.AddLink(from, to, kind), render: false);
    public void RemoveLink(string from, string to, string kind) => Write($"unlink {from} {kind} {to}", db => db.RemoveLink(from, to, kind), render: false);

    public Ticket Add(string repo, string type, string summary, string reference)
    {
        Ticket t = null!;
        Write($"new {type} in {repo}: {summary}", db => t = db.Add(repo, type, TicketRules.Open, summary, reference));
        return t;
    }

    /// <summary>Runs <c>tickets db sync</c> (render, dump, commit and push the database); returns its exit code and output.</summary>
    public async Task<(int Code, string Output)> SyncAsync()
    {
        // --root: the window's workspace, which needn't be the CLI's default one.
        var psi = new ProcessStartInfo("tickets")
        {
            ArgumentList = { "db", "sync", "--root", Root },
            WorkingDirectory = Path.Combine(Root, MindAttic.Tickets.Data.TicketsDb.HomeFolder(Root)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["NO_COLOR"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start 'tickets'.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var output = ((await stdout) + (await stderr)).Trim();
        if (p.ExitCode == 0) Log.Information("tickets db sync: {Output:l}", output);
        else Log.Warning("tickets db sync exited {Code}: {Output:l}", p.ExitCode, output);
        return (p.ExitCode, output);
    }

    private T Read<T>(Func<TicketsDb, T> work)
    {
        if (!Exists) throw new FileNotFoundException($"No ticket database at {DbPath}.");
        using var db = TicketsDb.Open(DbPath);
        return work(db);
    }

    private void Write(string what, Action<TicketsDb> work, bool render = true)
    {
        if (!Exists) throw new FileNotFoundException($"No ticket database at {DbPath}.");
        using var db = TicketsDb.Open(DbPath);
        work(db);
        if (render) db.Render(new Workspace(Root));
        Log.Information("{Change:l}", what);
    }

    private static readonly Serilog.ILogger Log = TicketsLog.For("Store");
}
