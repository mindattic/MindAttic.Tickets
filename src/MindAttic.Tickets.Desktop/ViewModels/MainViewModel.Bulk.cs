using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// Bulk actions (M.TIX-T79): select several tickets in the Queue or Repos list (Shift/Ctrl+click, Shift+Up/Down, Ctrl+Space,
/// Ctrl+A) and approve, unapprove, reject, close, won't-do, set the type of, or move them to the top or bottom of the queue in
/// one go. Each ticket is changed, and recorded in its own history, on its own: one that can't take the change (it's closed)
/// is named in the result and the rest go ahead.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How many IDs a bulk prompt lists before "and N more".</summary>
    private const int PromptIds = 12;

    private IReadOnlyList<TicketRowViewModel> selectedRows = [];

    /// <summary>The rows selected in the current list, in the list's order; the window keeps it in step with the list.</summary>
    public IReadOnlyList<TicketRowViewModel> SelectedRows
    {
        get => selectedRows;
        set
        {
            selectedRows = value ?? [];
            Raise();
            Raise(nameof(IsMultiSelect));
            Raise(nameof(SelectionText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>More than one ticket is selected: the bulk bar shows and the actions take them all.</summary>
    public bool IsMultiSelect => selectedRows.Count > 1 && !IsBoardView;

    public string SelectionText => $"{selectedRows.Count} tickets selected";

    /// <summary>Raised when the selection should be emptied (the bulk bar's Clear, Esc).</summary>
    public event Action? ClearSelectionRequested;

    public ICommand CloseRowsCommand { get; private set; } = null!;
    public ICommand WontDoRowsCommand { get; private set; } = null!;
    public ICommand SetTypeRowsCommand { get; private set; } = null!;
    public ICommand MoveBottomCommand { get; private set; } = null!;
    public ICommand ClearSelectionCommand { get; private set; } = null!;

    private void InitBulk()
    {
        CloseRowsCommand = new RelayCommand(async () => await CloseRowsAsync(SelectedRows, TicketRules.Closed), () => SelectedRows.Any(r => r.IsOpen));
        WontDoRowsCommand = new RelayCommand(async () => await CloseRowsAsync(SelectedRows, TicketRules.WontDo), () => SelectedRows.Any(r => r.IsOpen));
        SetTypeRowsCommand = new RelayCommand(async () => await SetTypeRowsAsync(SelectedRows), () => SelectedRows.Count > 0);
        MoveBottomCommand = new RelayCommand(async () => await MoveRowsAsync(SelectedRows, toTop: false), () => IsQueueView && SelectedRows.Any(r => r.IsOpen));
        ClearSelectionCommand = new RelayCommand(() => ClearSelectionRequested?.Invoke(), () => IsMultiSelect);
    }

    /// <summary>What an action on <paramref name="row"/> acts on: the whole selection when the row is part of a multi-selection
    /// (a key pressed on one of them), else the row alone.</summary>
    public IReadOnlyList<TicketRowViewModel> RowsFor(TicketRowViewModel? row) =>
        IsMultiSelect && (row is null || SelectedRows.Contains(row)) ? SelectedRows : row is null ? [] : [row];

    /// <summary>A command's rows: its parameter (a row's own button), else the selection, else the current view's selected row.</summary>
    private IReadOnlyList<TicketRowViewModel> Targets(object? p) =>
        p is TicketRowViewModel row ? [row] : IsMultiSelect ? SelectedRows : Target(null) is { } t ? [t] : [];

    public Task ApproveRowsAsync(IReadOnlyList<TicketRowViewModel> rows) => rows.Count switch
    {
        0 => Task.CompletedTask,
        1 => ApproveAsync(rows[0]),
        _ => RunBulk(rows, "Approved", ids => store.ApproveMany(ids), r => r.IsOpen),
    };

    public Task UnapproveRowsAsync(IReadOnlyList<TicketRowViewModel> rows) => rows.Count switch
    {
        0 => Task.CompletedTask,
        1 => UnapproveAsync(rows[0]),
        _ => RunBulk(rows, "Back to proposed", ids => store.UnapproveMany(ids)),
    };

    public async Task RejectRowsAsync(IReadOnlyList<TicketRowViewModel> rows)
    {
        if (rows.Count == 1) { await RejectAsync(rows[0]); return; }
        if (rows.Count == 0) return;
        var answer = Dialogs.Ask(new Prompt($"Reject {rows.Count} tickets", $"{IdList(rows)}\n\nRejecting closes each one as wont-do, with this reason as its ref. Why?",
            "Reason", Ok: "Reject all"));
        if (answer is null) return;
        foreach (var r in rows) ownCloses.Add(r.Id); // no toast for this window's own closes
        await RunBulk(rows, "Rejected", ids => store.RejectMany(ids, answer.Text), r => r.IsOpen);
    }

    /// <summary>Closes the rows (as closed or wont-do) after one prompt for the ref (optional for closed, the reason for wont-do).</summary>
    public async Task CloseRowsAsync(IReadOnlyList<TicketRowViewModel> rows, string status)
    {
        if (rows.Count == 0) return;
        var wontDo = status == TicketRules.WontDo;
        var verb = wontDo ? "Won't do" : "Close";
        var what = rows.Count == 1 ? rows[0].Id : $"{rows.Count} tickets";
        var answer = Dialogs.Ask(new Prompt($"{verb} {what}", rows.Count == 1 ? rows[0].Summary : IdList(rows),
            wontDo ? "Reason (becomes each one's ref)" : "Ref: test, commit or story (optional; each keeps its current ref when empty)",
            TextRequired: wontDo, Initial: "", Ok: rows.Count == 1 ? verb : verb + " all"));
        if (answer is null) return;
        foreach (var r in rows) ownCloses.Add(r.Id); // no toast for this window's own closes
        await RunBulk(rows, wontDo ? "Closed as wont-do" : "Closed",ids => store.CloseMany(ids, status, answer.Text), r => r.IsOpen);
    }

    /// <summary>Sets the rows' type (what labels a ticket until it has labels of its own), with one reason recorded on each.</summary>
    public async Task SetTypeRowsAsync(IReadOnlyList<TicketRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        var what = rows.Count == 1 ? rows[0].Id : $"{rows.Count} tickets";
        var common = rows.Select(r => r.Type).Distinct().Count() == 1 ? rows[0].Type : TicketRules.Types[0];
        var answer = Dialogs.Ask(new Prompt($"Set the type of {what}", $"{IdList(rows)}\n\nEach change is recorded in that ticket's history with your reason.",
            "Reason", ChoiceLabel: "Type", Choices: TicketRules.Types, Choice: common, Ok: "Set type"));
        if (answer?.Choice is not { } type) return;
        await RunBulk(rows, $"Set to {type}", ids => store.SetTypeMany(ids, type, answer.Text), r => r.Type != type);
    }

    /// <summary>Moves the rows to the top (or bottom) of the queue together, in the order they're listed (Alt+Home / Alt+End).</summary>
    public async Task MoveRowsAsync(IReadOnlyList<TicketRowViewModel> rows, bool toTop)
    {
        if (rows.Count == 0) return;
        var ordered = rows.OrderBy(r => QueueRows.IndexOf(r) is var i and >= 0 ? i : int.MaxValue).ToList();
        await RunBulk(ordered, toTop ? "Moved to the top" : "Moved to the bottom", ids => store.MoveMany(ids, toTop), r => r.IsOpen);
        if (ordered.FirstOrDefault(r => QueueRows.Contains(r)) is { } first) FocusRow?.Invoke(first);
    }

    /// <summary>
    /// Runs a bulk change for <paramref name="rows"/> off the UI thread and announces its tally. Rows <paramref name="applies"/>
    /// rules out are left alone and not sent (named as skipped, so the tally says so); the store reports any the database refused.
    /// </summary>
    private async Task RunBulk(IReadOnlyList<TicketRowViewModel> rows, string verb, Func<IReadOnlyList<string>, BulkResult> work, Func<TicketRowViewModel, bool>? applies = null)
    {
        var ids = rows.Where(r => applies?.Invoke(r) ?? true).Select(r => r.Id).ToList();
        var skipped = rows.Where(r => !(applies?.Invoke(r) ?? true)).Select(r => new BulkFailure(r.Id, SkipReason(r, verb))).ToList();
        BulkResult result = new([], []);
        var text = verb;
        if (!await Do(() => result = ids.Count > 0 ? work(ids) : result, verb,
            () => text = (result with { Failed = [.. skipped, .. result.Failed] }).Describe(verb))) return;
        if (result.Change is { IsEmpty: false } change) Remember(change, text);
    }

    private static string SkipReason(TicketRowViewModel r, string verb) =>
        !r.IsOpen ? $"it's {r.Status}" : verb.StartsWith("Set to ", StringComparison.Ordinal) ? $"already a {r.Type}" : "nothing to change";

    private static string IdList(IReadOnlyList<TicketRowViewModel> rows) =>
        string.Join(", ", rows.Take(PromptIds).Select(r => r.Id)) + (rows.Count > PromptIds ? $" and {rows.Count - PromptIds} more" : "");
}
