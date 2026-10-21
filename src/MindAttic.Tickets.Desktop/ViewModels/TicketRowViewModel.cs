using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>One ticket in a list. Rows are kept across refreshes (keyed by ID) so focus and selection survive.</summary>
public sealed class TicketRowViewModel(string id) : ObservableObject
{
    private string repo = "", date = "", type = "", status = "", summary = "", reference = "", approval = TicketsDb.Proposed;
    private long priority;
    private int rank;
    private bool isDropTarget;

    public string Id { get; } = id;
    public string Repo { get => repo; private set => Set(ref repo, value); }
    public string Date { get => date; private set => Set(ref date, value); }
    public string Type { get => type; private set => Set(ref type, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public string Ref { get => reference; private set => Set(ref reference, value); }
    public string Approval { get => approval; private set => Set(ref approval, value); }
    public long Priority { get => priority; private set => Set(ref priority, value); }

    /// <summary>1-based position in the queue as shown (0 outside the queue).</summary>
    public int Rank { get => rank; set { if (Set(ref rank, value)) Raise(nameof(SpokenName)); } }

    public bool IsDropTarget { get => isDropTarget; set => Set(ref isDropTarget, value); }

    public long Number => TicketRules.ParseNumber(Id) ?? 0;

    /// <summary>A fix may be worked without approval; anything else needs it (the orchestrator's rule).</summary>
    public bool MayWork => Status == TicketRules.Open && (Type == "fix" || Approval == TicketsDb.Approved);

    public bool IsOpen => Status == TicketRules.Open;

    public string ApprovalText => Labels.Approval(Approval);
    public string StatusText => Labels.Status(Status);

    /// <summary>What a screen reader says for the row: everything a sighted user reads off it, in one sentence.</summary>
    public string SpokenName =>
        (Rank > 0 ? $"Position {Rank}. " : "") + $"{Id}, {Type}, {Status}, {Approval}" +
        (Priority != 0 ? $", priority {Priority}" : "") + $", {Repo}: {Summary}";

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
}
