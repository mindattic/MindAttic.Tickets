using System.Globalization;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>Milestone states, check results, growth and history in words (M.TIX-T336), the words the CLI's <c>tickets milestone</c> uses.</summary>
public static class MilestoneWords
{
    /// <summary>A state as a glyph and its word.</summary>
    public static string State(string state) => state switch
    {
        MilestoneStates.Planned => "○ planned",
        MilestoneStates.Active => "● active",
        MilestoneStates.Accepting => "◐ accepting",
        MilestoneStates.Reached => "✓ reached",
        MilestoneStates.Dropped => "– dropped",
        _ => state,
    };

    /// <summary>A check's result as a glyph and its word.</summary>
    public static string Result(string result) => result switch
    {
        MilestoneCheckResults.Pass => "✓ pass",
        MilestoneCheckResults.Fail => "✕ fail",
        _ => "? unknown",
    };

    /// <summary>"3 tickets", "1 ticket".</summary>
    public static string Count(int n, string one, string? many = null) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");

    /// <summary>How much a milestone has grown since it was activated: "Grown by 3 tickets since activation, 2 waiting for
    /// approval"; empty before activation and while it hasn't grown and nothing waits.</summary>
    public static string Growth(MilestoneGrowth? g)
    {
        if (g is null) return "";
        var waiting = g.Pending > 0 ? $"{g.Pending.ToString(CultureInfo.InvariantCulture)} waiting for approval" : "";
        if (g.Added <= 0) return waiting.Length == 0 ? "" : $"Not grown since activation, {waiting}";
        return $"Grown by {Count(g.Added, "ticket")} since activation" + (waiting.Length == 0 ? "" : ", " + waiting);
    }

    /// <summary>One change in a milestone's history, as a sentence.</summary>
    public static string History(MilestoneEvent e) => e.Field switch
    {
        "created" => "Created.",
        "state" => $"State: {e.Old} → {e.New}",
        "member" => e.New is { } joined ? $"{joined} joined." : $"{e.Old} was taken off.",
        "proposed" => $"Proposed: {e.New}",
        "approved" => $"Approved: {e.New}",
        "declined" => $"Declined: {e.New}",
        "accepted" => $"Accepted as reached by {e.New}",
        "dropped" => $"Dropped: {e.New}",
        "check" => e.New is { } added ? $"Check added: {added}" : $"Check removed: {e.Old}",
        "check-result" => $"Check result: {e.Old} → {e.New}",
        "repo" => e.New is { } repo ? $"Repo added: {repo}" : $"Repo removed: {e.Old}",
        _ => $"{Labels.Field(e.Field)}: {e.Old ?? "none"} → {e.New ?? "none"}",
    };
}

/// <summary>One acceptance check of a milestone, as its detail lists it: the result as a word, never colour alone.</summary>
public sealed record MilestoneCheckLine(MilestoneCheck Check)
{
    public string Title => $"#{Check.Number.ToString(CultureInfo.InvariantCulture)} {Check.Kind}";
    public string Description => Check.Description;
    public string Result => Check.Result;
    public string ResultText => MilestoneWords.Result(Check.Result);

    /// <summary>What it looks at: its repos and its target; empty for a sign-off.</summary>
    public string LooksAt => string.Join(" ", new[]
    {
        Check.Repos.Count > 0 ? "In " + string.Join(", ", Check.Repos) : "",
        Check.Target is { } target ? "→ " + target : "",
    }.Where(x => x.Length > 0));

    public string CheckedText => Check.Checked is { } at
        ? $"Checked {DetailViewModel.When(at)}" + (Check.Evidence is { Length: > 0 } evidence ? $": {evidence}" : "")
        : "Not checked yet.";

    public string SpokenName => $"Check {Title}, {Check.Result}: {Description}. {CheckedText}";
}

/// <summary>A ticket (or other item) on a milestone, with its status.</summary>
public sealed record MilestoneMemberLine(string Id, string Summary, string Status, bool IsTicket)
{
    public string StatusText => IsTicket ? Labels.Status(Status) : "not a ticket";
    public string SpokenName => $"{Id}, {(IsTicket ? Status : "not a ticket")}: {Summary}";

    public static MilestoneMemberLine From(GraphNode node) =>
        new(node.Id, node.Name is { Length: > 0 } name ? $"{name}. {node.Summary}" : node.Summary ?? "", node.Status ?? "", node.IsTicket);
}

/// <summary>The members of a milestone under one heading: an epic's (or parent ticket's), or those linked to the milestone itself.</summary>
public sealed record MilestoneGroupLine(string Heading, string ProgressText, IReadOnlyList<MilestoneMemberLine> Members);

/// <summary>An addition waiting for the milestone's owner: approved or declined from its row.</summary>
public sealed record MilestonePendingLine(string MilestoneId, MilestoneMemberLine Item, string ProposedText)
{
    public string ApproveName => $"Approve {Item.Id} for {MilestoneId}";
    public string DeclineName => $"Decline {Item.Id} for {MilestoneId}";
}

/// <summary>
/// One milestone in the Milestones view (M.TIX-T336): its row in the list and, when selected, its detail. Kept and updated in
/// place across refreshes, so the selection survives them.
/// </summary>
public sealed class MilestoneRowViewModel : ObservableObject
{
    private string error = "";

    public MilestoneRowViewModel(MilestoneInfo info)
    {
        Id = info.Milestone.Id;
        Source = info;
        Update(info);
    }

    public string Id { get; }
    public MilestoneInfo Source { get; private set; }

    public string Name => Source.Milestone.Name;
    public string State => Source.Milestone.State;
    public string StateText => MilestoneWords.State(State);
    public string ReposText => string.Join(", ", Source.Milestone.Repos);
    public string Goal => Source.Milestone.Goal;
    public bool HasGoal => Goal.Length > 0;

    public bool IsReached => State == MilestoneStates.Reached;
    public bool CanActivate => State == MilestoneStates.Planned;
    public bool CanSendToAcceptance => State == MilestoneStates.Active;

    public int Done { get; private set; }
    public int Total { get; private set; }
    public double DoneFraction => Total == 0 ? 0 : (double)Done / Total;
    public string ProgressText => Total == 0 ? "No tickets yet" : $"{Done} of {MilestoneWords.Count(Total, "ticket")} done";

    public int ChecksPassed => Source.Checks.Count(c => c.Passed);
    public string ChecksText => Source.Checks.Count == 0 ? "No acceptance checks" : $"Checks: {ChecksPassed} of {Source.Checks.Count} pass";

    /// <summary>Its growth since activation in words; empty when it hasn't grown (or was never activated).</summary>
    public string GrowthText => MilestoneWords.Growth(Source.Growth);

    /// <summary>When it was activated and reached, and by whom.</summary>
    public string DatesText => string.Join(" · ", new[]
    {
        Source.Milestone.Activated is { } on ? $"Activated {DetailViewModel.When(on)}" : "Not activated yet",
        Source.Milestone.Reached is { } at ? $"Reached {DetailViewModel.When(at)} by {Source.Milestone.ReachedBy ?? "unknown"}" : "",
    }.Where(x => x.Length > 0));

    /// <summary>What the view doesn't do, and the command that does.</summary>
    public string CliHint => IsReached ? ""
        : $"Accepting it as reached, or dropping it, is done from the command line: tickets milestone accept {Id} --reached, tickets milestone drop {Id}.";

    public string NoChecksText => $"It has no acceptance checks. Add one with: tickets milestone check add {Id} --kind ... --description ...";
    public string NoMembersText => $"Nothing is on it yet. Add tickets or epics with: tickets milestone add {Id} TICKET ...";

    public IReadOnlyList<MilestoneCheckLine> Checks { get; private set; } = [];
    public IReadOnlyList<MilestoneGroupLine> Groups { get; private set; } = [];
    public IReadOnlyList<MilestonePendingLine> Pending { get; private set; } = [];

    /// <summary>Its history, most recent first.</summary>
    public IReadOnlyList<HistoryLine> History { get; private set; } = [];

    public string ChecksHeader => $"ACCEPTANCE CHECKS ({ChecksPassed} OF {Source.Checks.Count} PASS)";
    public string MembersHeader => $"EPICS AND TICKETS ({Done} OF {Total} DONE)";
    public string PendingHeader => $"PENDING ADDITIONS ({Pending.Count} WAITING FOR THE OWNER'S APPROVAL)";
    public string HistoryHeader => $"History ({MilestoneWords.Count(History.Count, "change")}, most recent first)";

    /// <summary>Why the last action on it failed; empty when it didn't.</summary>
    public string Error { get => error; set => Set(ref error, value); }

    public string SpokenName => $"{Id}, {State}, {ReposText}: {Name}. {ProgressText}. {ChecksText}." + (GrowthText.Length > 0 ? $" {GrowthText}." : "");

    public void Update(MilestoneInfo info)
    {
        Source = info;
        (Done, Total) = TicketsDb.MilestoneProgress(info.Members);
        Checks = [.. info.Checks.Select(c => new MilestoneCheckLine(c))];
        Groups = [.. info.Groups.Select(g =>
        {
            var (done, total) = TicketsDb.MilestoneProgress(g.Members);
            return new MilestoneGroupLine(g.Heading, total == 0 ? "" : $"{done} of {total} done", [.. g.Members.Select(m => MilestoneMemberLine.From(m.Node))]);
        })];
        Pending = [.. info.Pending.Select(p => new MilestonePendingLine(Id, MilestoneMemberLine.From(p.Node),
            $"Proposed {DetailViewModel.When(p.Addition.At)}" + (p.Addition.By is { } who ? $" by {who}" : "")))];
        History = [.. info.History.Reverse().Select(e => new HistoryLine(DetailViewModel.When(e.At), MilestoneWords.History(e)))];
        Raise(string.Empty); // every property reads from the new info
    }
}
