using System.Globalization;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

public sealed record HistoryLine(string When, string Text);

/// <summary>
/// A link as read from this ticket's side: "Waits for M.TIX-T64". <see cref="Blocking"/> marks a requires link that holds
/// work up now: this ticket waits for an open one, or an open ticket waits for this open one; <see cref="State"/> says which.
/// </summary>
public sealed record LinkLine(string Phrase, string Other, Link Link, bool Blocking = false, string State = "")
{
    public string SpokenName => State.Length == 0 ? $"{Phrase} {Other}" : $"{Phrase} {Other}, {State}";
}

/// <summary>
/// One ticket drawn in the dependency graph (M.TIX-T83): <see cref="Blocking"/> marks one that holds work up now, drawn in
/// the error colour and said in <see cref="State"/>; <see cref="IsSelf"/> the ticket the graph is around.
/// </summary>
public sealed record GraphChip(string Id, string State, bool Blocking, bool IsOpen, bool IsSelf, string Summary)
{
    public string Text => $"{Id} · {State}";
    public string SpokenName => (IsSelf ? "This ticket, " : "") + $"{Id}, {State}" + (Summary.Length > 0 ? $": {Summary}" : "") + (IsSelf ? "" : ": go to it");
}

/// <summary>
/// One row of the drawn dependency graph: what it waits for, farthest first, above the ticket, and what waits for it,
/// nearest first, below it, so the requires arrows all point up.
/// </summary>
public sealed record GraphRow(string Label, IReadOnlyList<GraphChip> Chips)
{
    public bool IsSelf => Chips.Count == 1 && Chips[0].IsSelf;
}

/// <summary>One of the ticket's commits (M.TIX-T85), as the detail pane lists it; <see cref="Url"/> is its GitHub page, or null.</summary>
public sealed record CommitLine(TicketCommit Commit)
{
    public string Hash => Commit.ShortHash;
    public string When => $"{Commit.Date} · {Commit.Author}";
    public string Subject => Commit.Subject;
    public string? Url => Commit.Url;
    public bool HasUrl => Url is not null;
    public string SpokenName => $"Commit {Hash}, {When}: {Subject}" + (HasUrl ? ": open it on GitHub" : "");
}

/// <summary>A link that opens the Orchestrator on <see cref="Target"/> (a login's email, a session id or a ticket id).</summary>
public sealed record OrchestratorTarget(string Text, string Target)
{
    public string SpokenName => $"{Text}: open in the Orchestrator";
}

/// <summary>
/// The detail pane's content for one ticket. Its fields show one version of the ticket (M.TIX-T137): the current one, or an
/// older one stepped to with the arrows (<see cref="Step"/>), with what that version changed marked. Actions always act on
/// the ticket as it is now.
/// </summary>
public sealed class DetailViewModel : ObservableObject
{
    private string error = "";
    private int index;
    private IReadOnlyList<LinkLine> links;

    /// <param name="version">The version to show (1 is as filed); null for the current one.</param>
    /// <param name="worker">Who the Orchestrator's assignments say is working it now (M.TIX-T76); null when nobody is.</param>
    public DetailViewModel(TicketDetail d, int? version = null, Worker? worker = null)
    {
        Source = d;
        Worker = worker;
        index = d.Versions.Count == 0 ? -1 : version is { } v ? Math.Clamp(v - 1, 0, d.Versions.Count - 1) : d.Versions.Count - 1;
        links = LinkLines(Shown?.Links ?? d.Links);
        History = [.. d.History.Select(e => new HistoryLine(When(e.At), Describe(e)))];
        Notes = [.. d.Notes.Select(n => new HistoryLine($"{When(n.At)} · {n.Author}", n.Markdown))];
        Commits = [.. d.Commits.Select(c => new CommitLine(c))];
    }

    /// <summary>The commits whose subject names it, newest first (M.TIX-T85); each opens on GitHub when its repo is there.</summary>
    public IReadOnlyList<CommitLine> Commits { get; }

    /// <summary>The ticket's comments, oldest first (M.TIX-T72): when and who, then the markdown as written.</summary>
    public IReadOnlyList<HistoryLine> Notes { get; }

    public TicketDetail Source { get; }

    /// <summary>The version shown, or null when the store gave none.</summary>
    public TicketVersion? Shown => index >= 0 ? Source.Versions[index] : null;

    private Ticket Ticket => Shown?.Ticket ?? Source.Info.Ticket;

    /// <summary>The ticket's ID now (what actions and links act on); <see cref="ShownId"/> is the shown version's.</summary>
    public string Id => Source.Info.Ticket.Id;
    public string ShownId => Ticket.Id;
    public string Repo => Shown?.Repo ?? Source.Info.Repo;
    public string Type => Ticket.Type;
    public string Status => Ticket.Status;
    public string StatusText => Labels.Status(Status);
    public string Approval => Shown?.Approval ?? Source.Info.Approval;
    public string ApprovalText => Labels.Approval(Approval);
    public long Priority => Shown?.Priority ?? Source.Info.Priority;
    public string Date => Ticket.Date;
    public string Summary => Ticket.Summary;
    public string Ref => Ticket.Ref;

    /// <summary>Its markdown description (M.TIX-T73), as it is now (descriptions aren't versioned with the row).</summary>
    public string Description => Source.Description ?? "";
    public bool HasDescription => Source.Description is not null;
    public bool IsOpen => Source.Info.Ticket.Status == TicketRules.Open;
    public bool IsApproved => Source.Info.Approval == TicketsDb.Approved;
    public bool IsBlocked => Source.OpenBlockers.Count > 0;

    // ── Versions: the arrows step through them, the rightmost the current one ──

    public int VersionCount => Source.Versions.Count;
    public int VersionNumber => index + 1;
    public bool HasVersions => VersionCount > 1;
    public bool IsCurrentVersion => index < 0 || index == VersionCount - 1;
    public bool CanGoOlder => index > 0;
    public bool CanGoNewer => index >= 0 && index < VersionCount - 1;

    public string VersionText => index < 0 ? "" : $"Version {VersionNumber} of {VersionCount}" + (IsCurrentVersion ? " (current)" : "");

    public string VersionWhen => Shown is { } v ? v.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";

    /// <summary>What the shown version changed, in words (the marks on the fields say it in colour too).</summary>
    public string ChangesText =>
        Shown is not { } v ? ""
        : (v.Number == 1 ? "As filed." : $"Changed: {string.Join(", ", v.Changed.Select(Labels.Field))}.")
          + (ShownId != Id ? $" Then {ShownId}." : "")
          + (v.Recorded ? "" : " Worked back from its history; links as they are now.");

    public bool IdChanged => Changed("id");
    public bool RepoChanged => Changed("repo");
    public bool TypeChanged => Changed("type");
    public bool StatusChanged => Changed("status");
    public bool SummaryChanged => Changed("summary");
    public bool RefChanged => Changed("ref");
    public bool ApprovalChanged => Changed("approval");
    public bool PriorityChanged => Changed("priority");
    public bool LinksChanged => Changed("links");
    public bool AssigneeChanged => Changed("assignee");
    public bool ReservedChanged => Changed("reserved");

    private bool Changed(string field) => Shown is { Number: > 1 } v && v.Changed.Contains(field);

    /// <summary>Shows the version <paramref name="delta"/> away (−1 older, +1 newer); false at either end.</summary>
    public bool Step(int delta)
    {
        var to = index + delta;
        if (index < 0 || to < 0 || to >= VersionCount) return false;
        index = to;
        links = LinkLines(Shown?.Links ?? Source.Links);
        Raise(string.Empty); // every field reads from the version now shown
        CommandManager.InvalidateRequerySuggested();
        return true;
    }

    /// <summary>What a screen reader says after a step.</summary>
    public string VersionAnnouncement => $"{VersionText}, {VersionWhen}. {ChangesText}";

    public string WorkText =>
        !IsOpen ? "Done: not in the queue."
        : IsBlocked ? $"Blocked: waits for {string.Join(", ", Source.OpenBlockers)}."
        : Type == "fix" || IsApproved ? "Ready: /allocate may work it."
        : "Needs approval before /allocate works it.";

    public IReadOnlyList<string> Blockers => Source.OpenBlockers;

    /// <summary>Its labels as they are now (M.TIX-T74), shown as chips; labels aren't versioned.</summary>
    public IReadOnlyList<string> TicketLabels => Source.Info.Labels;

    // ── Work (M.TIX-T134): assignee, reservation, and the session working it, which links back to the Orchestrator ──

    public TicketWork Work => Source.Work;

    /// <summary>The shown version's assignee and reservation (M.TIX-T137); the session working it is always now's.</summary>
    private string? ShownAssignee => Shown is { } v ? v.Assignee : Work.Assignee;
    private long ShownReserved => Shown?.Reserved ?? Work.Reserved;
    public string AssigneeText => ShownAssignee ?? "Nobody";
    public bool IsAssigned => Work.Assignee is not null;
    public string ReservedText => ShownReserved > 0 ? $"{Tokens(ShownReserved)} tokens" : "None";
    public bool IsWorking => Work.IsWorking;

    /// <summary>"Being worked on ryan@… since 14:02".</summary>
    public string WorkingText => !IsWorking ? "" : $"Being worked{(Work.Login is { } l ? $" on {l}" : "")}{(Work.Since is { } s ? $" since {When(s)}" : "")}.";

    /// <summary>The links that open the Orchestrator on what's working the ticket: its login, its session, the ticket itself.</summary>
    public IReadOnlyList<OrchestratorTarget> OrchestratorTargets => !IsWorking ? [] :
        [
            .. Work.Login is { } login ? [new OrchestratorTarget($"Login {login}", login)] : Array.Empty<OrchestratorTarget>(),
            .. Work.Session is { } session ? [new OrchestratorTarget($"Session {session}", session)] : Array.Empty<OrchestratorTarget>(),
            new OrchestratorTarget($"{Id} in the Orchestrator", Id),
        ];

    // ── Claim (M.TIX-T76): the Orchestrator's assignment for it — the agent, its worktree, and since when ──

    /// <summary>The Orchestrator's worker on this ticket, as last read; null when it has none.</summary>
    public Worker? Worker { get; }

    public bool HasWorker => Worker is not null;

    /// <summary>Whether the work panel carries the Show who's working this action (M.TIX-T158): a session works it but the
    /// Orchestrator's state has no worker on it (when it has one, the claim panel carries the action).</summary>
    public bool ShowsWorkerInWorkPanel => IsWorking && !HasWorker;

    /// <summary>"Claimed by Ryan (ryan@…) on opus", or that its worker died and left it orphaned.</summary>
    public string ClaimText => Worker is not { } w ? ""
        : w.Orphaned ? $"Orphaned: {((w.Person ?? w.Login) is { } who ? who + "'s" : "its")} worker is gone{(w.Why is { Length: > 0 } why ? $" ({why})" : "")}."
        : $"Claimed by {w.Agent}.";

    /// <summary>The worktree it's being worked in (the full path), or empty.</summary>
    public string ClaimWorktree => Worker?.Directory ?? "";

    /// <summary>When the Orchestrator dispatched it, local, with how long ago; empty when it didn't record it.</summary>
    public string ClaimSince => Worker?.SinceText(DateTime.UtcNow) ?? "";

    public string ClaimSession => Worker?.Session ?? "";

    /// <summary>All of the claim in one sentence, for a screen reader.</summary>
    public string ClaimSpoken => Worker is null ? "" : string.Join(" ", new[]
    {
        ClaimText,
        ClaimWorktree.Length > 0 ? $"Worktree {ClaimWorktree}." : null,
        ClaimSince.Length > 0 ? $"Since {ClaimSince}." : null,
    }.Where(s => s is not null));

    private static string Tokens(long n) => TokenCount.Format(n);

    /// <summary>Its effort estimate now (M.TIX-T75): "~300k tokens", or "None".</summary>
    public string EstimateText => Source.Info.Estimate is { } e ? $"~{Tokens(e)} tokens" : "None";

    public IReadOnlyList<LinkLine> Links => links;

    private IReadOnlyList<LinkLine> LinkLines(IEnumerable<Link> all) => [.. all.Select(l =>
    {
        var outgoing = string.Equals(l.From, Id, StringComparison.OrdinalIgnoreCase) || string.Equals(l.From, ShownId, StringComparison.OrdinalIgnoreCase);
        if (l.Kind != "requires")
            return outgoing ? new LinkLine(Outgoing(l.Kind), l.To, l) : new LinkLine(Incoming(l.Kind), l.From, l);
        // Blocked state, both ways: what this one waits for that's still open, and who waits for this one while it's open.
        if (outgoing)
            return IsOpen && Source.OpenBlockers.Contains(l.To, StringComparer.OrdinalIgnoreCase)
                ? new LinkLine(Outgoing(l.Kind), l.To, l, Blocking: true, State: "open: blocks this")
                : new LinkLine(Outgoing(l.Kind), l.To, l, State: IsOpen ? "not blocking" : "");
        return IsOpen && Source.OpenDependents.Contains(l.From, StringComparer.OrdinalIgnoreCase)
            ? new LinkLine(Incoming(l.Kind), l.From, l, Blocking: true, State: "blocked by this")
            : new LinkLine(Incoming(l.Kind), l.From, l);
    })];

    // ── Dependency graph (M.TIX-T83): the requires chains both ways, as they are now, drawn and as a text list ──

    public DependencyGraph Graph => Source.Graph ?? DependencyGraph.Empty(Id);

    public bool HasDependencies => !Graph.IsEmpty;

    /// <summary>The drawn graph's rows, top to bottom: farthest prerequisites, …, this ticket, …, farthest dependents.</summary>
    public IReadOnlyList<GraphRow> GraphRows
    {
        get
        {
            if (Graph.IsEmpty) return [];
            static string Steps(int n) => n == 1 ? "" : $", {n} steps";
            GraphChip Chip(DependencyNode n, bool upstream) =>
                new(n.Id, n.StateText(upstream), n.Blocking, n.IsOpen, IsSelf: false, n.Summary ?? "");
            var rows = new List<GraphRow>();
            rows.AddRange(DependencyGraph.Layers(Graph.Upstream).Reverse()
                .Select(layer => new GraphRow("Waits for" + Steps(layer[0].Depth), [.. layer.Select(n => Chip(n, upstream: true))])));
            rows.Add(new GraphRow("This ticket", [new GraphChip(Id, Source.Info.Ticket.Status + (IsBlocked ? ", blocked" : ""), false, IsOpen, IsSelf: true, Source.Info.Ticket.Summary)]));
            rows.AddRange(DependencyGraph.Layers(Graph.Downstream)
                .Select(layer => new GraphRow("Needed by" + Steps(layer[0].Depth), [.. layer.Select(n => Chip(n, upstream: false))])));
            return rows;
        }
    }

    /// <summary>The graph as a text list, one ticket a line, indented by steps: the drawn graph's text alternative.</summary>
    public string DependencyText => string.Join(Environment.NewLine, Graph.TextLines());

    /// <summary>The graph in one sentence: how many it waits for and blocks.</summary>
    public string DependencySummary =>
        $"Waits for {Count(Graph.Upstream)}, {Graph.Blockers.Count} of them open and blocking it. " +
        $"Needed by {Count(Graph.Downstream)}, {Graph.Blocked.Count} of them blocked by it.";

    private static int Count(IEnumerable<DependencyNode> side) => side.Where(n => !n.Repeat && !n.Cycle).Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public IReadOnlyList<HistoryLine> History { get; }

    /// <summary>The last action's error, shown beside the actions and announced (3.3.1).</summary>
    public string Error { get => error; set => Set(ref error, value); }

    internal static string Outgoing(string kind) => kind switch
    {
        "requires" => "Waits for",
        "related" => "Related to",
        "follow-up-of" => "Follow-up of",
        "supersedes" => "Supersedes",
        "duplicates" => "Duplicates",
        "implements" => "Implements",
        "part-of" => "Part of",
        _ => kind,
    };

    internal static string Incoming(string kind) => kind switch
    {
        "requires" => "Needed by",
        "related" => "Related to",
        "follow-up-of" => "Followed up by",
        "supersedes" => "Superseded by",
        "duplicates" => "Duplicated by",
        "implements" => "Implemented by",
        "part-of" => "Includes",
        _ => kind,
    };

    private static string When(string at) =>
        DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : at;

    private static string Describe(TicketEvent e) => e.Field switch
    {
        "created" => $"Filed ({e.New})",
        // A description's history holds whole bodies; the line only says what happened.
        "description" => e.Old is null ? "Description added" : e.New is null ? "Description removed" : "Description edited",
        _ when e.Old is null => $"{e.Field}: {e.New}",
        _ => $"{e.Field}: {e.Old} → {e.New}",
    };
}
