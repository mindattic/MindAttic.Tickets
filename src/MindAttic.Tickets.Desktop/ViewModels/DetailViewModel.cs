using System.Globalization;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

public sealed record HistoryLine(string When, string Text);

/// <summary>A link as read from this ticket's side: "Waits for M.TIX-T64".</summary>
public sealed record LinkLine(string Phrase, string Other, Link Link)
{
    public string SpokenName => $"{Phrase} {Other}";
}

/// <summary>The detail pane's content for one ticket.</summary>
public sealed class DetailViewModel(TicketDetail d) : ObservableObject
{
    private string error = "";

    public TicketDetail Source { get; } = d;
    public string Id => Source.Info.Ticket.Id;
    public string Repo => Source.Info.Repo;
    public string Type => Source.Info.Ticket.Type;
    public string Status => Source.Info.Ticket.Status;
    public string StatusText => Labels.Status(Status);
    public string Approval => Source.Info.Approval;
    public string ApprovalText => Labels.Approval(Approval);
    public long Priority => Source.Info.Priority;
    public string Date => Source.Info.Ticket.Date;
    public string Summary => Source.Info.Ticket.Summary;
    public string Ref => Source.Info.Ticket.Ref;
    public bool IsOpen => Status == TicketRules.Open;
    public bool IsApproved => Approval == TicketsDb.Approved;
    public bool IsBlocked => Source.OpenBlockers.Count > 0;

    public string WorkText =>
        !IsOpen ? "Done: not in the queue."
        : IsBlocked ? $"Blocked: waits for {string.Join(", ", Source.OpenBlockers)}."
        : Type == "fix" || IsApproved ? "Ready: autopilot may work it."
        : "Needs approval before autopilot works it.";

    public IReadOnlyList<string> Blockers => Source.OpenBlockers;

    public IReadOnlyList<LinkLine> Links { get; } = [.. d.Links.Select(l =>
        string.Equals(l.From, d.Info.Ticket.Id, StringComparison.OrdinalIgnoreCase)
            ? new LinkLine(Outgoing(l.Kind), l.To, l)
            : new LinkLine(Incoming(l.Kind), l.From, l))];

    public IReadOnlyList<HistoryLine> History { get; } = [.. d.History.Select(e => new HistoryLine(When(e.At), Describe(e)))];

    /// <summary>The last action's error, shown beside the actions and announced (3.3.1).</summary>
    public string Error { get => error; set => Set(ref error, value); }

    private static string Outgoing(string kind) => kind switch
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

    private static string Incoming(string kind) => kind switch
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
        _ when e.Old is null => $"{e.Field}: {e.New}",
        _ => $"{e.Field}: {e.Old} → {e.New}",
    };
}
