using System.Collections.ObjectModel;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The Milestones view (M.TIX-T336, Ctrl+9): every milestone that isn't dropped, in milestone order with the reached ones
/// last, each with its state, repos, progress, acceptance checks and growth since activation; the selected one shows its
/// goal, checks, the epics and tickets under it, the additions waiting for approval (Approve / Decline) and its history.
/// Activate and Send to acceptance act on it; reaching and dropping stay with the CLI. Read with every refresh.
/// </summary>
public sealed partial class MainViewModel
{
    public const string MilestonesView = "Milestones";
    public const string NoMilestonesText = "No milestones yet. Create one with: tickets milestone new --name ... --repo ...";

    private MilestoneRowViewModel? selectedMilestoneRow;
    private string milestonesEmptyText = NoMilestonesText;

    public bool IsMilestonesView => SelectedView == MilestonesView;

    public ObservableCollection<MilestoneRowViewModel> MilestoneRows { get; } = [];

    public MilestoneRowViewModel? SelectedMilestoneRow
    {
        get => selectedMilestoneRow;
        set
        {
            if (!Set(ref selectedMilestoneRow, value)) return;
            Raise(nameof(ShowsMilestonePane));
            CommandManager.InvalidateRequerySuggested();
            if (value is not null) StatusMessage = $"{value.Id}: {value.StateText}, {value.ProgressText}.";
        }
    }

    /// <summary>The milestone's detail shows beside the Milestones list, where the ticket detail pane shows in other views.</summary>
    public bool ShowsMilestonePane => IsMilestonesView && SelectedMilestoneRow is not null;

    /// <summary>What the empty list says: that there are no milestones (and how to make one), or that none match.</summary>
    public string MilestonesEmptyText { get => milestonesEmptyText; private set => Set(ref milestonesEmptyText, value); }

    public ICommand ActivateMilestoneCommand { get; private set; } = null!;
    public ICommand SendMilestoneToAcceptanceCommand { get; private set; } = null!;
    public ICommand ApproveAdditionCommand { get; private set; } = null!;
    public ICommand DeclineAdditionCommand { get; private set; } = null!;
    public ICommand CloseMilestoneCommand { get; private set; } = null!;

    private void InitMilestones()
    {
        ActivateMilestoneCommand = new RelayCommand(async () => await ActivateMilestoneAsync(), () => SelectedMilestoneRow is { CanActivate: true } && !Busy);
        SendMilestoneToAcceptanceCommand = new RelayCommand(async () => await SendMilestoneToAcceptanceAsync(), () => SelectedMilestoneRow is { CanSendToAcceptance: true } && !Busy);
        ApproveAdditionCommand = new RelayCommand(async p => await ApproveAdditionAsync(p as MilestonePendingLine), p => p is MilestonePendingLine && !Busy);
        DeclineAdditionCommand = new RelayCommand(async p => await DeclineAdditionAsync(p as MilestonePendingLine), p => p is MilestonePendingLine && !Busy);
        CloseMilestoneCommand = new RelayCommand(() => SelectedMilestoneRow = null, () => SelectedMilestoneRow is not null);
    }

    private string MilestonesStatus => snapshot is not { Milestones.Count: > 0 } s ? NoMilestonesText
        : $"Milestones: {MilestoneRows.Count} of {s.Milestones.Count} shown, {s.Milestones.Count(m => m.Milestone.IsActive)} active, " +
          $"{MilestoneWords.Count(s.Milestones.Sum(m => m.Pending.Count), "addition")} waiting for approval.";

    private void ApplyMilestones(Snapshot s)
    {
        bool Match(MilestoneInfo m) =>
            (RepoFilter == AllRepos || m.Milestone.Repos.Contains(RepoFilter, StringComparer.OrdinalIgnoreCase)) &&
            (Search.Length == 0 || m.Milestone.Id.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             m.Milestone.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || m.Milestone.Goal.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             m.Milestone.Repos.Any(r => r.Contains(Search, StringComparison.OrdinalIgnoreCase)));

        // Milestone order (the snapshot's), the reached ones after those still to reach.
        var shown = s.Milestones.Where(Match).OrderBy(m => m.Milestone.State == MilestoneStates.Reached ? 1 : 0);
        MilestoneRows.SyncBy(shown, m => m.Milestone.Id, r => r.Id, m => new MilestoneRowViewModel(m), (row, m) => row.Update(m));
        if (selectedMilestoneRow is { } sel && !MilestoneRows.Contains(sel)) SelectedMilestoneRow = null;
        MilestonesEmptyText = s.Milestones.Count == 0 ? NoMilestonesText : "No milestones match.";
    }

    /// <summary>Runs a change to <paramref name="row"/>'s milestone as <see cref="Do"/> does; a refusal shows on the milestone too.</summary>
    private async Task<bool> DoMilestone(MilestoneRowViewModel row, Action work, string done)
    {
        row.Error = "";
        if (await Do(work, done)) return true;
        row.Error = StatusMessage;
        return false;
    }

    /// <summary>Makes the selected planned milestone active: its scope is locked from then on.</summary>
    public async Task ActivateMilestoneAsync()
    {
        if (SelectedMilestoneRow is not { } row) return;
        await DoMilestone(row, () => store.ActivateMilestone(row.Id), $"{row.Id} is active.");
    }

    /// <summary>Moves the selected active milestone into acceptance.</summary>
    public async Task SendMilestoneToAcceptanceAsync()
    {
        if (SelectedMilestoneRow is not { } row) return;
        await DoMilestone(row, () => store.SendMilestoneToAcceptance(row.Id), $"{row.Id} is in acceptance.");
    }

    private MilestoneRowViewModel? RowOf(MilestonePendingLine line) =>
        MilestoneRows.FirstOrDefault(r => r.Id.Equals(line.MilestoneId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Approves a pending addition as <see cref="TicketStore.Author"/>: it joins the milestone.</summary>
    public async Task ApproveAdditionAsync(MilestonePendingLine? line)
    {
        if (line is null || RowOf(line) is not { } row) return;
        await DoMilestone(row, () => store.ApproveMilestoneAddition(line.MilestoneId, line.Item.Id),
            $"{line.Item.Id} is on {line.MilestoneId}, approved by {TicketStore.Author}.");
    }

    /// <summary>Declines a pending addition as <see cref="TicketStore.Author"/>, with a reason: it stops waiting and never joined.</summary>
    public async Task DeclineAdditionAsync(MilestonePendingLine? line)
    {
        if (line is null || RowOf(line) is not { } row) return;
        var answer = Dialogs.Ask(new Prompt($"Decline {line.Item.Id} for {line.MilestoneId}",
            $"{line.Item.Summary}\n\nDeclining it stops it waiting: it doesn't join {line.MilestoneId}. Why?", "Reason", Ok: "Decline"));
        if (answer is null) return;
        await DoMilestone(row, () => store.DeclineMilestoneAddition(line.MilestoneId, line.Item.Id, answer.Text),
            $"{line.Item.Id} was declined for {line.MilestoneId} by {TicketStore.Author}.");
    }
}
