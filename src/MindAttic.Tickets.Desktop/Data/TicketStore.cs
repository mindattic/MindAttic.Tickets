using System.Diagnostics;
using System.IO;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using MindAttic.Tickets.Repos;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>
/// One ticket with its queue state, as the views show it. <paramref name="Blockers"/>: the open tickets it waits on
/// (its <c>requires</c> links, all the way down), empty when nothing holds it up.
/// </summary>
public sealed record TicketInfo(string Repo, Ticket Ticket, string Approval, long Priority, IReadOnlyList<string>? Blockers = null)
{
    public IReadOnlyList<string> Blockers { get; init; } = Blockers ?? [];

    public bool IsOpen => Ticket.Status == TicketRules.Open;

    /// <summary>Open and waiting on another open ticket.</summary>
    public bool Blocked => IsOpen && Blockers.Count > 0;

    /// <summary>Open, not a fix and not approved: /allocate won't take it until it's approved (the orchestrator's rule).</summary>
    public bool NeedsApproval => IsOpen && Ticket.Type != "fix" && Approval != TicketsDb.Approved;

    /// <summary>Open, approved or a fix, and not blocked: what /allocate takes next, in priority order.</summary>
    public bool Queued => IsOpen && !NeedsApproval && !Blocked;
}

/// <summary>One thing GitHub intake's dry run would file, in <paramref name="Repo"/>.</summary>
public sealed record IntakePreview(string Repo, IntakeItem Item)
{
    /// <summary>The intake item a dry-run ticket stands for: its marker (GH#12, CI#987) leads its summary, its link is its Ref.</summary>
    internal static IntakeItem ItemOf(Ticket t)
    {
        var colon = t.Summary.IndexOf(':');
        return new IntakeItem(colon > 0 ? t.Summary[..colon] : t.Summary, t.Type, t.Summary, t.Ref);
    }
}

/// <summary>Everything the window shows, read in one go.</summary>
public sealed record Snapshot(IReadOnlyList<string> Repos, IReadOnlyList<TicketInfo> Tickets, IReadOnlyList<TicketInfo> Queue, DateTime Read)
{
    /// <summary>Closed tickets that may not really be done (<c>tickets ticket hygiene</c>), worked out with the read (M.TIX-T138).</summary>
    public IReadOnlyList<HygieneFinding> Hygiene { get; init; } = [];
}

/// <summary>A ticket's detail: history and links (both directions) and what it's waiting for.</summary>
public sealed record TicketDetail(TicketInfo Info, IReadOnlyList<TicketEvent> History, IReadOnlyList<Link> Links, IReadOnlyList<string> OpenBlockers)
{
    /// <summary>Who it's assigned to, the tokens reserved for it and the session working it now (M.TIX-T134).</summary>
    public TicketWork Work { get; init; } = TicketWork.None;

    /// <summary>Every version of it, oldest first, the last the current one (M.TIX-T137).</summary>
    public IReadOnlyList<TicketVersion> Versions { get; init; } = [];

    /// <summary>The open tickets that require this one directly: while it's open, it blocks them (M.TIX-T68).</summary>
    public IReadOnlyList<string> OpenDependents { get; init; } = [];
}

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
        var tickets = db.Tickets();
        var blockers = OpenBlockers(tickets.Select(x => x.Ticket), db.AllLinks());
        IReadOnlyList<string> BlockersOf(string id) => blockers.TryGetValue(id, out var b) ? b : [];
        TicketInfo Info(string repo, Ticket t) =>
            states.TryGetValue(t.Id, out var s) ? new(repo, t, s.Approval, s.Priority, BlockersOf(t.Id)) : new(repo, t, TicketsDb.Proposed, 0, BlockersOf(t.Id));
        return new Snapshot(
            [.. db.Repos().Select(r => r.Name)],
            [.. tickets.Select(x => Info(x.Repo, x.Ticket))],
            [.. db.Queue().Select(q => new TicketInfo(q.Repo, q.Ticket, q.Approval, q.Priority, BlockersOf(q.Ticket.Id)))],
            DateTime.Now)
        {
            Hygiene = TicketHygiene.Check(tickets),
        };
    });

    /// <summary>
    /// Each open ticket's open blockers: the open tickets its <c>requires</c> links reach, directly or through others
    /// (the detail pane's "waiting for", worked out for every ticket from one read of the links). IDs match without case.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> OpenBlockers(IEnumerable<Ticket> tickets, IEnumerable<Link> links)
    {
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<Ticket>();
        foreach (var t in tickets) { all.Add(t); if (t.Status == TicketRules.Open) open.Add(t.Id); }
        var edges = links.Where(l => l.Kind == "requires").ToLookup(l => l.From, l => l.To, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in all.Where(t => open.Contains(t.Id) && edges.Contains(t.Id)))
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { t.Id };
            var next = new Queue<string>(edges[t.Id]);
            while (next.TryDequeue(out var x))
            {
                if (!seen.Add(x)) continue;
                if (open.Contains(x)) found.Add(x);
                foreach (var y in edges[x]) next.Enqueue(y);
            }
            if (found.Count > 0) result[t.Id] = found;
        }
        return result;
    }

    public TicketDetail? Detail(string id) => Read(db =>
    {
        if (db.Ticket(id) is not { } found) return null;
        var (approval, priority) = db.QueueState(found.Ticket.Id);
        var blockers = db.Requires(found.Ticket.Id, transitive: true)
            .Where(b => db.Ticket(b) is { } t && t.Ticket.Status == TicketRules.Open).ToList();
        var links = db.Links(found.Ticket.Id);
        var dependents = links
            .Where(l => l.Kind == "requires" && string.Equals(l.To, found.Ticket.Id, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.From)
            .Where(d => db.Ticket(d) is { } t && t.Ticket.Status == TicketRules.Open).ToList();
        return new TicketDetail(new TicketInfo(found.Repo, found.Ticket, approval, priority, blockers), db.History(found.Ticket.Id), links, blockers)
        {
            OpenDependents = dependents,
            Work = db.Work(found.Ticket.Id),
            Versions = db.Versions(found.Ticket.Id),
        };
    });

    public void Approve(string id) => Write($"approve {id}", db => db.Approve(id), render: false);
    public void Assign(string id, string email) => Write($"assign {id} to {email}", db => db.Assign(id, email), render: false);
    public void Unassign(string id) => Write($"unassign {id}", db => db.Unassign(id), render: false);
    public void Reserve(string id, long tokens) => Write($"reserve {tokens} tokens for {id}", db => db.Reserve(id, tokens), render: false);
    public void Unapprove(string id) => Write($"unapprove {id}", db => db.Unapprove(id), render: false);
    public void Reject(string id, string reason) => Write($"reject {id}: {reason}", db => db.Reject(id, reason));
    public void Move(string id, IReadOnlyList<string> newOrder) =>
        Write($"move {id} to position {newOrder.ToList().IndexOf(id) + 1}", db => db.Move(id, newOrder), render: false);
    public void Close(string id, string status, string? reference) => Write($"{status} {id} ({reference})", db => db.Close(id, status, reference));
    public void SetRef(string id, string reference) => Write($"ref {id} ({reference})", db => db.SetRef(id, reference));
    public void CorrectType(string id, string type, string reason) => Write($"correct {id} to {type}: {reason}", db => db.CorrectType(id, type, reason));
    public void AddLink(string from, string to, string kind) => Write($"link {from} {kind} {to}", db => db.AddLink(from, to, kind), render: false);
    public void RemoveLink(string from, string to, string kind) => Write($"unlink {from} {kind} {to}", db => db.RemoveLink(from, to, kind), render: false);

    public Ticket Add(string repo, string type, string summary, string reference)
    {
        Ticket t = null!;
        Write($"new {type} in {repo}: {summary}", db => t = db.Add(repo, type, TicketRules.Open, summary, reference));
        return t;
    }

    /// <summary>Runs <c>gh</c> for GitHub intake; tests replace it.</summary>
    public Func<string[], (int Code, string Output)> Gh { get; init; } = GitHubIntake.Gh;

    /// <summary>A repo's <c>origin</c> URL for GitHub intake (null when it has none); tests replace it.</summary>
    public Func<Repo, string?>? Remote { get; init; }

    /// <summary>What the last intake preview's <c>gh</c> calls said when one failed (not installed, not signed in), else null.</summary>
    public string? IntakeError { get; private set; }

    /// <summary>
    /// GitHub intake's dry run (<c>tickets ticket intake --dry-run</c>): what each workspace repo with a GitHub origin would
    /// get filed, nothing written.
    /// </summary>
    public IReadOnlyList<IntakePreview> PreviewIntake()
    {
        IntakeError = null;
        (int Code, string Output) Ask(string[] args)
        {
            var result = Gh(args);
            if (result.Code != 0 && !string.IsNullOrWhiteSpace(result.Output)) IntakeError ??= result.Output.Trim();
            return result;
        }
        return Read(db => GitHubIntake.Intake(new Workspace(Root), db, dryRun: true, gh: Ask, remote: Remote)
            .Select(x => new IntakePreview(x.Repo, IntakePreview.ItemOf(x.Ticket))).ToList());
    }

    /// <summary>
    /// Files exactly the previewed items (not a fresh read of GitHub), each once: into the ticket database when it holds the
    /// repo, else into the repo's own USER_STORIES.md. Returns what was filed; an item filed since the preview is skipped.
    /// </summary>
    public IReadOnlyList<(string Repo, Ticket Ticket)> FileIntake(IReadOnlyList<IntakePreview> preview)
    {
        var filed = new List<(string Repo, Ticket Ticket)>();
        Write($"GitHub intake of {preview.Count} item(s)", db =>
        {
            var ws = new Workspace(Root);
            var inDb = db.Repos().Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in preview.GroupBy(p => p.Repo, StringComparer.OrdinalIgnoreCase))
            {
                var items = group.Select(p => p.Item).ToList();
                if (inDb.Contains(group.Key))
                {
                    filed.AddRange(GitHubIntake.FileNew(db, group.Key, items).Select(t => (group.Key, t)));
                    continue;
                }
                if (ws.Repos().FirstOrDefault(r => r.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase)) is not { } repo || !File.Exists(repo.StoriesPath)) continue;
                var stories = StoryFile.Load(repo.StoriesPath);
                var added = GitHubIntake.FileNew(stories, items);
                if (added.Count > 0) stories.Save();
                filed.AddRange(added.Select(t => (repo.Name, t)));
            }
        });
        foreach (var (repo, t) in filed) Log.Information("GitHub intake filed {Id} in {Repo}: {Summary:l}", t.Id, repo, t.Summary);
        return filed;
    }

    /// <summary>Runs <c>tickets db sync</c> (render, dump, commit and push the database); returns its exit code and output.</summary>
    public Task<(int Code, string Output)> SyncAsync() => Sync(this);

    /// <summary>What <see cref="SyncAsync"/> runs: the <c>tickets</c> CLI; tests replace it.</summary>
    public Func<TicketStore, Task<(int Code, string Output)>> Sync { get; init; } = RunSyncAsync;

    private static async Task<(int Code, string Output)> RunSyncAsync(TicketStore store)
    {
        // --root: the window's workspace, which needn't be the CLI's default one.
        var root = store.Root;
        var psi = new ProcessStartInfo("tickets")
        {
            ArgumentList = { "db", "sync", "--root", root },
            WorkingDirectory = Path.Combine(root, MindAttic.Tickets.Data.TicketsDb.HomeFolder(root)),
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
