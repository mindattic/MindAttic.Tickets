using System.Windows.Input;
using MindAttic.Tickets.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// Undo (M.TIX-T80): approve, unapprove, reject, a queue move, close / won't do, a board move, and adding or removing a link
/// can be taken back with Ctrl+Z or the toast's Undo button, newest first. An undo writes compensating changes, each recorded
/// in the ticket's history with an <c>undo</c> event saying what was undone; nothing is deleted, and nothing that someone else
/// has changed again since is overwritten (<see cref="TicketsDb.Undo"/> refuses it).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How many changes back undo reaches.</summary>
    public const int UndoDepth = 20;

    /// <summary>How long the undo toast stays up after a change.</summary>
    public static TimeSpan UndoToastTime { get; set; } = TimeSpan.FromSeconds(10);

    private readonly List<(TicketChange Change, string Done)> undo = [];
    private string undoToast = "";
    private int toastShown;

    public ICommand UndoCommand { get; }
    public ICommand DismissUndoToastCommand { get; }

    public bool CanUndo => undo.Count > 0;

    /// <summary>What Ctrl+Z would undo now, in words, or empty.</summary>
    public string NextUndo => undo.Count > 0 ? undo[^1].Done : "";

    /// <summary>The toast's text after a change ("M.TIX-T5 approved."), empty when it's hidden.</summary>
    public string UndoToast
    {
        get => undoToast;
        private set
        {
            if (!Set(ref undoToast, value)) return;
            Raise(nameof(IsUndoToastShown));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsUndoToastShown => UndoToast.Length > 0;

    public void HideUndoToast() => UndoToast = "";

    /// <summary>Runs an undoable change (as <see cref="Do"/>) and, when it changed something, offers to undo it.</summary>
    private async Task<bool> Undoable(Func<TicketChange> work, string done)
    {
        TicketChange? change = null;
        if (!await Do(() => change = work(), done)) return false;
        if (change is { IsEmpty: false }) Remember(change, done);
        return true;
    }

    private void Remember(TicketChange change, string done)
    {
        undo.Add((change, done));
        if (undo.Count > UndoDepth) undo.RemoveAt(0);
        RaiseUndo();
        UndoToast = done;
        var shown = ++toastShown;
        _ = HideToastLaterAsync(shown);
    }

    private async Task HideToastLaterAsync(int shown)
    {
        await Task.Delay(UndoToastTime);
        if (shown == toastShown) HideUndoToast();
    }

    /// <summary>
    /// Undoes the newest change still on the stack with compensating changes. False when there's nothing to undo or it was
    /// refused (it changed again since, said in the status line); a refused change leaves the stack, since it can't succeed later.
    /// </summary>
    public async Task<bool> UndoAsync()
    {
        HideUndoToast();
        if (undo.Count == 0)
        {
            StatusMessage = Announcement = "Nothing to undo.";
            return false;
        }
        var (change, done) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        RaiseUndo();
        return await Do(() => store.Undo(change), $"Undone: {done}");
    }

    private void RaiseUndo()
    {
        Raise(nameof(CanUndo));
        Raise(nameof(NextUndo));
        CommandManager.InvalidateRequerySuggested();
    }
}
