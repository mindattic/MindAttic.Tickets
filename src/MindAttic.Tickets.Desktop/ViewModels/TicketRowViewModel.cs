using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>One ticket in a list. Rows are kept across refreshes (keyed by ID) so focus and selection survive.</summary>
public sealed class TicketRowViewModel(string id) : ObservableObject
{
    private string repo = "", date = "", type = "", status = "", summary = "", reference = "", approval = TicketsDb.Proposed;
    private long priority;
    private long? estimate;
    private int rank;
    private bool isDropTarget;
    private IReadOnlyList<string> blockers = [], ticketLabels = [];
    private RepoHeaderViewModel? repoHeader;
    private Worker? worker;

    public string Id { get; } = id;
    public string Repo { get => repo; private set => Set(ref repo, value); }
    public string Date { get => date; private set => Set(ref date, value); }
    public string Type { get => type; private set => Set(ref type, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public string Ref { get => reference; private set => Set(ref reference, value); }
    public string Approval { get => approval; private set => Set(ref approval, value); }
    public long Priority { get => priority; private set => Set(ref priority, value); }

    /// <summary>Its effort estimate in tokens (M.TIX-T75), null while it has none.</summary>
    public long? Estimate { get => estimate; private set { if (Set(ref estimate, value)) Raise(nameof(EstimateText)); } }

    /// <summary>"~300k tokens", or empty with no estimate.</summary>
    public string EstimateText => Estimate is { } e ? $"~{TokenCount.Format(e)} tokens" : "";

    /// <summary>The open tickets this one waits on (its <c>requires</c> links, all the way down).</summary>
    public IReadOnlyList<string> Blockers { get => blockers; private set => Set(ref blockers, value); }

    /// <summary>Its labels, shown as chips (M.TIX-T74).</summary>
    public IReadOnlyList<string> TicketLabels { get => ticketLabels; private set { if (Set(ref ticketLabels, value)) Raise(nameof(LabelsText)); } }

    /// <summary>Its labels in one line ("ui, component:desktop"); empty when it has none.</summary>
    public string LabelsText => string.Join(", ", TicketLabels);

    public bool IsBlocked => IsOpen && Blockers.Count > 0;

    public string BlockedText => IsBlocked ? "⏸ blocked by " + string.Join(", ", Blockers) : "";

    /// <summary>1-based position in the queue as shown (0 outside the queue).</summary>
    public int Rank { get => rank; set { if (Set(ref rank, value)) Raise(nameof(SpokenName)); } }

    public bool IsDropTarget { get => isDropTarget; set => Set(ref isDropTarget, value); }

    /// <summary>Its repo's heading on the Repos tab (M.TIX-T135); the list groups rows by it.</summary>
    public RepoHeaderViewModel? RepoHeader { get => repoHeader; set => Set(ref repoHeader, value); }

    /// <summary>Who's working it now, from the Orchestrator's state (M.TIX-T135); null when nobody has claimed it.</summary>
    public Worker? Worker
    {
        get => worker;
        set
        {
            if (!Set(ref worker, value)) return;
            Raise(nameof(WorkerText));
            Raise(nameof(WorkerName));
            Raise(nameof(WorkerTip));
        }
    }

    public string WorkerText => Worker?.Text ?? "";

    /// <summary>Why it's orphaned (if it is), its worktree, session and since when (M.TIX-T76), and what the link does.</summary>
    public string WorkerTip => Worker is { } w
        ? string.Join("\n", new[] { w.Why is { Length: > 0 } why ? why + "." : null, w.Detail(DateTime.UtcNow) is { Length: > 0 } d ? d : null,
            "Opens the Orchestrator on this ticket's live work (O)." }.Where(s => s is not null))
        : "";

    public string WorkerName => Worker is { } w ? $"{Id} is being worked: {w.Text.TrimStart('▶', '⚠', ' ')}. Opens the Orchestrator on it." : "";

    public long Number => TicketRules.ParseNumber(Id) ?? 0;

    /// <summary>A fix may be worked without approval; anything else needs it (the orchestrator's rule).</summary>
    public bool MayWork => Status == TicketRules.Open && (Type == "fix" || Approval == TicketsDb.Approved);

    public bool IsOpen => Status == TicketRules.Open;

    public string ApprovalText => Labels.Approval(Approval);
    public string StatusText => Labels.Status(Status);

    /// <summary>What a screen reader says for the row: everything a sighted user reads off it, in one sentence.</summary>
    public string SpokenName =>
        (Rank > 0 ? $"Position {Rank}. " : "") + $"{Id}, {Type}, {Status}, {Approval}" +
        (Priority != 0 ? $", priority {Priority}" : "") + (Estimate is not null ? $", estimated {EstimateText.TrimStart('~')}" : "") + (IsBlocked ? $", {BlockedText.TrimStart('⏸', ' ')}" : "") +
        (TicketLabels.Count > 0 ? $", labels {LabelsText}" : "") + $", {Repo}: {Summary}";

    public void Update(TicketInfo t)
    {
        Repo = t.Repo;
        Date = t.Ticket.Date;
        Type = t.Ticket.Type;
        Status = t.Ticket.Status;
        Summary = t.Ticket.Summary;
        Ref = t.Ticket.Ref;
        Approval = t.Approval;
        Priority = t.Priority;
        Estimate = t.Estimate;
        if (!Blockers.SequenceEqual(t.Blockers, StringComparer.OrdinalIgnoreCase)) Blockers = t.Blockers;
        if (!TicketLabels.SequenceEqual(t.Labels, StringComparer.Ordinal)) TicketLabels = t.Labels;
        Raise(nameof(IsBlocked));
        Raise(nameof(BlockedText));
        Raise(nameof(MayWork));
        Raise(nameof(IsOpen));
        Raise(nameof(ApprovalText));
        Raise(nameof(StatusText));
        Raise(nameof(SpokenName));
    }

    public static TicketRowViewModel From(TicketInfo t)
    {
        var row = new TicketRowViewModel(t.Ticket.Id);
        row.Update(t);
        return row;
    }
}

/// <summary>Token counts as shown: 1.2M, 300k, 950.</summary>
public static class TokenCount
{
    public static string Format(long tokens) =>
        tokens >= 1_000_000_000 ? (tokens / 1e9).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "B"
        : tokens >= 1_000_000 ? (tokens / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M"
        : tokens >= 1_000 ? (tokens / 1e3).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The glyph + word shown for each state, so no state is told by colour alone (1.4.1).</summary>
public static class Labels
{
    public static string Approval(string approval) => approval switch
    {
        TicketsDb.Approved => "✓ approved",
        TicketsDb.Rejected => "✕ rejected",
        _ => "○ proposed",
    };

    public static string Status(string status) => status switch
    {
        TicketRules.Open => "● open",
        TicketRules.Closed => "✓ closed",
        _ => "– wont-do",
    };

    /// <summary>A field a version changed (<see cref="TicketsDb.VersionFields"/>), as the detail pane names it.</summary>
    public static string Field(string field) => field switch
    {
        "id" => "ID",
        "ref" => "Ref",
        _ => char.ToUpperInvariant(field[0]) + field[1..],
    };
}
