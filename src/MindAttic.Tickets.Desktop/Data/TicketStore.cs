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
    /// <summary>Its effort estimate in tokens (M.TIX-T75), or null while it has none.</summary>
    public long? Estimate { get; init; }

    public IReadOnlyList<string> Blockers { get; init; } = Blockers ?? [];

    /// <summary>A session is recorded as working it in the ticket database (<c>ticket_work</c>; M.TIX-T134).</summary>
    public bool Working { get; init; }

    /// <summary>Its labels, for a query's <c>label:</c> (M.TIX-T78).</summary>
    public IReadOnlyCollection<string> Labels { get; init; } = [];

    /// <summary>What a <see cref="TicketQuery"/> is matched against.</summary>
    public TicketFacts Facts => new(Repo, Ticket, Approval, Labels);

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

    /// <summary>The named queries kept in the database, for the filter bar (M.TIX-T78).</summary>
    public IReadOnlyList<SavedFilter> SavedFilters { get; init; } = [];

    /// <summary>Every repo's stories, epics and backlog items, for the command palette's jump (M.TIX-T81).</summary>
    public IReadOnlyList<StoredStory> Stories { get; init; } = [];
}

/// <summary>A story as the command palette opens it (M.TIX-T81): its rows (one per repo holding the ID) and its links.</summary>
public sealed record StoryDetail(IReadOnlyList<StoredStory> Rows, IReadOnlyList<Link> Links)
{
    public StoredStory Story => Rows[0];
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

    /// <summary>Its comments, oldest first (M.TIX-T72).</summary>
    public IReadOnlyList<TicketNote> Notes { get; init; } = [];

    /// <summary>Its requires chains both ways, as they are now (M.TIX-T83); empty when nothing waits either way.</summary>
    public DependencyGraph? Graph { get; init; }
    /// <summary>Its markdown description, kept apart from the one-line summary (M.TIX-T73); null when it has none.</summary>
    public string? Description { get; init; }

    /// <summary>The commits whose subject names it, in its repo, newest first, each linked to GitHub when it can be (M.TIX-T85).</summary>
    public IReadOnlyList<TicketCommit> Commits { get; init; } = [];
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
        var estimates = db.Estimates();
        var tickets = db.Tickets();
        var blockers = OpenBlockers(tickets.Select(x => x.Ticket), db.AllLinks());
        var work = db.WorkStates();
        var labels = db.TicketLabels();
        IReadOnlyList<string> BlockersOf(string id) => blockers.TryGetValue(id, out var b) ? b : [];
        IReadOnlyCollection<string> LabelsOf(string id) => labels.TryGetValue(id, out var l) ? l : [];
        bool WorkingOn(string id) => work.TryGetValue(id, out var w) && w.IsWorking;
        long? EstimateOf(string id) => estimates.TryGetValue(id, out var e) ? e : null;
        TicketInfo Info(string repo, Ticket t) =>
            (states.TryGetValue(t.Id, out var s) ? new TicketInfo(repo, t, s.Approval, s.Priority, BlockersOf(t.Id)) : new TicketInfo(repo, t, TicketsDb.Proposed, 0, BlockersOf(t.Id)))
            with { Working = WorkingOn(t.Id), Estimate = EstimateOf(t.Id), Labels = LabelsOf(t.Id) };
        return new Snapshot(
            [.. db.Repos().Select(r => r.Name)],
            [.. tickets.Select(x => Info(x.Repo, x.Ticket))],
            [.. db.Queue().Select(q => new TicketInfo(q.Repo, q.Ticket, q.Approval, q.Priority, BlockersOf(q.Ticket.Id)) { Working = WorkingOn(q.Ticket.Id), Estimate = q.Estimate, Labels = LabelsOf(q.Ticket.Id) })],
            DateTime.Now)
        {
            Hygiene = TicketHygiene.Check(tickets),
            SavedFilters = db.SavedFilters(),
            Stories = [.. db.Stories().Where(s => s.Id.Length > 0)],
        };
    });

    /// <summary>The story <paramref name="id"/> and its links (both directions), or null when there's no such story.</summary>
    public StoryDetail? Story(string id) => Read(db =>
        db.Story(id.Trim()) is { Count: > 0 } rows ? new StoryDetail(rows, db.Links(rows[0].Id)) : null);

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

    public TicketDetail? Detail(string id) =>
        // git runs after the database is closed, so a slow log never holds the file open (M.TIX-T85).
        DetailFromDb(id) is not { } d ? null : d with { Commits = Commits(d.Info.Repo, d.Info.Ticket.Id) };

    /// <summary>The commits whose subject names <paramref name="id"/> in <paramref name="repo"/>'s folder (M.TIX-T85); empty
    /// when the repo isn't in the workspace.</summary>
    public IReadOnlyList<TicketCommit> Commits(string repo, string id) =>
        new Workspace(Root).Find(repo) is { } r ? TicketCommits.Find(id, r.Path) : [];

    private TicketDetail? DetailFromDb(string id) => Read(db =>
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
        return new TicketDetail(new TicketInfo(found.Repo, found.Ticket, approval, priority, blockers) { Estimate = db.Estimate(found.Ticket.Id) }, db.History(found.Ticket.Id), links, blockers)
        {
            OpenDependents = dependents,
            Work = db.Work(found.Ticket.Id),
            Versions = db.Versions(found.Ticket.Id),
            Notes = db.Notes(found.Ticket.Id),
            // Only read every ticket and link when a requires link touches it: most tickets have none.
            Graph = links.Any(l => l.Kind == "requires")
                ? DependencyGraph.Build(found.Ticket.Id, db.Tickets().Select(x => x.Ticket), db.AllLinks())
                : DependencyGraph.Empty(found.Ticket.Id),
            Description = db.Description(found.Ticket.Id),
        };
    });

    /// <summary>Appends a note to the ticket, by <see cref="Environment.UserName"/> (or <c>$MINDATTIC_TICKETS_AUTHOR</c>).</summary>
    public void AddNote(string id, string markdown) => Write($"note on {id}", db => db.AddNote(id,
        Environment.GetEnvironmentVariable("MINDATTIC_TICKETS_AUTHOR") is { Length: > 0 } a ? a.Trim() : Environment.UserName, markdown), render: false);

    // Approve, unapprove, reject, move, close, link and unlink return what they changed, so the window can undo them (M.TIX-T80).
    public TicketChange Approve(string id) => Change($"approve {id}", [id], db => db.Approve(id), render: false);
    public void Assign(string id, string email) => Write($"assign {id} to {email}", db => db.Assign(id, email), render: false);
    public void Unassign(string id) => Write($"unassign {id}", db => db.Unassign(id), render: false);
    public void Reserve(string id, long tokens) => Write($"reserve {tokens} tokens for {id}", db => db.Reserve(id, tokens), render: false);
    public void SetEstimate(string id, long? tokens) =>
        Write(tokens is { } t ? $"estimate {id} at {t} tokens" : $"clear {id}'s estimate", db => db.SetEstimate(id, tokens), render: false);
    public TicketChange Unapprove(string id) => Change($"unapprove {id}", [id], db => db.Unapprove(id), render: false);
    public TicketChange Reject(string id, string reason) => Change($"reject {id}: {reason}", [id], db => db.Reject(id, reason));
    public TicketChange Move(string id, IReadOnlyList<string> newOrder) =>
        Change($"move {id} to position {newOrder.ToList().IndexOf(id) + 1}", [id, .. newOrder.Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase))],
            db => db.Move(id, newOrder), render: false);
    public TicketChange Close(string id, string status, string? reference) => Change($"{status} {id} ({reference})", [id], db => db.Close(id, status, reference));
    public void SetRef(string id, string reference) => Write($"ref {id} ({reference})", db => db.SetRef(id, reference));
    public void CorrectType(string id, string type, string reason) => Write($"correct {id} to {type}: {reason}", db => db.CorrectType(id, type, reason));
    public void CorrectSummary(string id, string summary, string reason) => Write($"reword {id} to '{summary}': {reason}", db => db.CorrectSummary(id, summary, reason));
    public void SetDescription(string id, string? body) =>
        Write($"{(string.IsNullOrWhiteSpace(body) ? "clear" : "set")} {id}'s description", db => db.SetDescription(id, body), render: false);
    public TicketChange AddLink(string from, string to, string kind) => Change($"link {from} {kind} {to}", [from], db => db.AddLink(from, to, kind), render: false);
    public TicketChange RemoveLink(string from, string to, string kind) => Change($"unlink {from} {kind} {to}", [from], db => db.RemoveLink(from, to, kind), render: false);
    public void SaveFilter(string name, string query) => Write($"save filter {name}: {query}", db => db.SaveFilter(name, query), render: false);
    public void DeleteFilter(string name) => Write($"delete filter {name}", db => db.DeleteFilter(name), render: false);

    /// <summary>
    /// Undoes <paramref name="change"/> with compensating changes, each recorded in the ticket's history, plus an <c>undo</c>
    /// event (M.TIX-T80); nothing is deleted. Throws <see cref="InvalidOperationException"/> when it has changed again since.
    /// </summary>
    public void Undo(TicketChange change) =>
        Write($"undo {change.What}", db => db.Undo(change), render: change.States.Any(s => s.Before.Status != s.After.Status || s.Before.Ref != s.After.Ref));

    /// <summary>Writes <paramref name="work"/> as <see cref="Write"/> does and returns what it changed to <paramref name="ids"/>.</summary>
    private TicketChange Change(string what, IReadOnlyList<string> ids, Action<TicketsDb> work, bool render = true)
    {
        TicketChange change = null!;
        Write(what, db => change = db.Record(what, ids, () => work(db)), render);
        return change;
    }

    /// <summary>
    /// Moves a card on the Board (M.TIX-T77) by changing what its column stands for: Proposed unapproves it, Approved approves
    /// it, In progress approves it and records it as worked by hand (<see cref="Board.HandSession"/>), Done closes it with
    /// <paramref name="reference"/> (optional). Leaving In progress clears the work record. A closed ticket stays closed:
    /// tickets are append-only, so a card can't leave Done (file a new ticket instead).
    /// </summary>
    public TicketChange MoveCard(string id, BoardColumn to, string? reference = null) =>
        Change($"board: {id} to {Board.Title(to)}", [id], db =>
        {
            var found = db.Ticket(id) ?? throw new KeyNotFoundException($"No ticket {id}.");
            if (found.Ticket.Status != TicketRules.Open)
            {
                if (to == BoardColumn.Done) return;
                throw new InvalidOperationException($"{found.Ticket.Id} is {found.Ticket.Status}; a closed ticket can't be reopened (tickets are append-only). File a new ticket instead.");
            }
            var t = found.Ticket.Id;
            if (to != BoardColumn.InProgress) db.ClearWorking(t);
            switch (to)
            {
                case BoardColumn.Proposed: db.Unapprove(t); break;
                case BoardColumn.Approved: db.Approve(t); break;
                case BoardColumn.InProgress:
                    if (db.QueueState(t).Approval != TicketsDb.Approved) db.Approve(t);
                    if (!db.Work(t).IsWorking) db.SetWorking(t, null, Board.HandSession);
                    break;
                case BoardColumn.Done: db.Close(t, TicketRules.Closed, reference); break;
            }
        }, render: to == BoardColumn.Done);

    // ── Bulk actions (M.TIX-T79): each ticket is changed, and recorded in its own history, on its own ──

    /// <summary>Approves each open ticket in <paramref name="ids"/>.</summary>
    public BulkResult ApproveMany(IReadOnlyList<string> ids) => Bulk("approve", ids, (db, id) => db.Approve(id), render: false);

    /// <summary>Puts each ticket in <paramref name="ids"/> back to proposed.</summary>
    public BulkResult UnapproveMany(IReadOnlyList<string> ids) => Bulk("unapprove", ids, (db, id) => db.Unapprove(id), render: false);

    /// <summary>Rejects each open ticket in <paramref name="ids"/> (closed as wont-do), all with the one <paramref name="reason"/>.</summary>
    public BulkResult RejectMany(IReadOnlyList<string> ids, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection needs a reason.", nameof(reason));
        return Bulk($"reject ({reason.Trim()})", ids, (db, id) => db.Reject(id, reason));
    }

    /// <summary>Closes each open ticket in <paramref name="ids"/> as <paramref name="status"/>, setting <paramref name="reference"/>
    /// as its ref when one is given; a ticket that's already closed is left as it is and reported.</summary>
    public BulkResult CloseMany(IReadOnlyList<string> ids, string status, string? reference)
    {
        if (status == TicketRules.WontDo && string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("Won't do needs a reason.", nameof(reference));
        return Bulk($"{status} ({reference})", ids, (db, id) =>
        {
            var t = db.Ticket(id) ?? throw new KeyNotFoundException($"No ticket {id}.");
            if (t.Ticket.Status != TicketRules.Open) throw new InvalidOperationException($"{t.Ticket.Id} is already {t.Ticket.Status}.");
            db.Close(t.Ticket.Id, status, reference);
        });
    }

    /// <summary>Sets the type of each ticket in <paramref name="ids"/> (its label until tickets have labels of their own), each
    /// correction recorded in its history with <paramref name="reason"/>; one already of that type is left as it is.</summary>
    public BulkResult SetTypeMany(IReadOnlyList<string> ids, string type, string reason)
    {
        if (!TicketRules.Types.Contains(type)) throw new ArgumentException($"Unknown type '{type}' (expected {string.Join("/", TicketRules.Types)}).", nameof(type));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A type change needs a reason.", nameof(reason));
        // Not recorded for undo: a type correction runs its own transaction (it lifts the append-only trigger) and isn't undoable.
        return Bulk($"type {type} ({reason.Trim()})", ids, (db, id) => db.CorrectType(id, type, reason), record: false);
    }

    /// <summary>
    /// Moves the open tickets in <paramref name="ids"/> to the top of the queue (or the bottom), keeping their order: each gets a
    /// priority just above every other open ticket's (or just below), first-named highest. Only priorities that change are
    /// written, each recorded in its ticket's history; the rest of the queue keeps theirs.
    /// </summary>
    public BulkResult MoveMany(IReadOnlyList<string> ids, bool toTop)
    {
        var moving = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        long? edge = null;
        var next = 0;
        return Bulk(toTop ? "move to the top" : "move to the bottom", ids, (db, id) =>
        {
            var t = db.Ticket(id) ?? throw new KeyNotFoundException($"No ticket {id}.");
            if (t.Ticket.Status != TicketRules.Open) throw new InvalidOperationException($"{t.Ticket.Id} is {t.Ticket.Status}; only open tickets are in the queue.");
            if (edge is null)
            {
                var others = db.Queue().Where(q => !moving.Contains(q.Ticket.Id)).Select(q => q.Priority).ToList();
                var openMoving = moving.Count(x => db.Ticket(x) is { } m && m.Ticket.Status == TicketRules.Open);
                // The first one moved gets the edge, the next one below it, and so on.
                edge = others.Count == 0 ? (toTop ? openMoving : 0) : toTop ? others.Max() + openMoving : others.Min() - 1;
            }
            db.SetPriority(t.Ticket.Id, edge.Value - next);
            next++;
        }, render: false);
    }

    /// <summary>
    /// Runs <paramref name="each"/> for every ticket in <paramref name="ids"/> in one database session, each its own change (and
    /// its own history), so one ticket that can't take it (closed, gone) doesn't stop the rest: it's reported in the result.
    /// </summary>
    private BulkResult Bulk(string what, IReadOnlyList<string> ids, Action<TicketsDb, string> each, bool render = true, bool record = true)
    {
        var done = new List<string>();
        var failed = new List<BulkFailure>();
        TicketChange? change = null;
        var label = $"bulk {what} of {ids.Count} ticket(s)";
        Write(label, db =>
        {
            var unique = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Record what it changed to every ticket that exists, so the whole batch can be undone in one go (M.TIX-T80).
            var known = record ? unique.Where(id => db.Ticket(id) is not null).ToList() : [];
            if (known.Count > 0) change = db.Record(label, known, Run);
            else Run();

            void Run()
            {
                foreach (var id in unique)
                {
                    try
                    {
                        each(db, id);
                        done.Add(id);
                        Log.Information("{What:l} {Id} (bulk)", what, id);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or System.Data.Common.DbException)
                    {
                        failed.Add(new BulkFailure(id, ex.Message));
                        Log.Warning("{What:l} {Id} (bulk) failed: {Message:l}", what, id, ex.Message);
                    }
                }
            }
        }, render);
        return new BulkResult(done, failed) { Change = change };
    }

    public Ticket Add(string repo, string type, string summary, string reference, string? description = null)
    {
        Ticket t = null!;
        Write($"new {type} in {repo}: {summary}", db => t = db.Add(repo, type, TicketRules.Open, summary, reference, description: description));
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
