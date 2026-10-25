using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Desktop.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// A repo's heading on the Repos tab (M.TIX-T135): its name, how many of its tickets are queued, need approval or are
/// blocked, its git state, and a Start link that opens the Orchestrator to start a CLI there. One instance per repo,
/// kept across refreshes: the Repos list groups its tickets by it, so the heading updates in place.
/// </summary>
public sealed class RepoHeaderViewModel(string name) : ObservableObject
{
    private string counts = "", git = "";
    private bool gitKnown;

    public string Name { get; } = name;

    /// <summary>"3 queued · 1 needs approval · 2 blocked" over its open tickets.</summary>
    public string Counts { get => counts; private set => Set(ref counts, value); }

    /// <summary>Its git state, or "…" until it's been read.</summary>
    public string Git { get => git; private set => Set(ref git, value); }

    public bool GitKnown { get => gitKnown; private set => Set(ref gitKnown, value); }

    public string StartName => $"Start a CLI in {Name}: opens the Orchestrator";

    public override string ToString() => Name;

    public void SetCounts(IEnumerable<TicketInfo> tickets)
    {
        int queued = 0, approval = 0, blocked = 0;
        foreach (var t in tickets)
        {
            if (t.Queued) queued++;
            if (t.NeedsApproval) approval++;
            if (t.Blocked) blocked++;
        }
        Counts = string.Join(" · ", new[]
        {
            $"{queued} queued",
            approval > 0 ? $"{approval} need{(approval == 1 ? "s" : "")} approval" : null,
            blocked > 0 ? $"{blocked} blocked" : null,
        }.Where(s => s is not null));
    }

    public void SetGit(RepoGitState? state)
    {
        GitKnown = state is not null;
        Git = state?.Text ?? "git: …";
    }
}
