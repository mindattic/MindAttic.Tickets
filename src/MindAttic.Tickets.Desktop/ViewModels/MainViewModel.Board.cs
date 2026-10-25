using System.Collections.ObjectModel;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>One column of the Board: its tickets as cards, and a heading that says how many.</summary>
public sealed class BoardColumnViewModel(BoardColumn column) : ObservableObject
{
    private string heading = Board.Title(column);
    private bool isDropTarget;

    public BoardColumn Column { get; } = column;
    public string Title { get; } = Board.Title(column);
    public ObservableCollection<TicketRowViewModel> Rows { get; } = [];

    /// <summary>The title and count ("Done: 50 most recent of 812" when the column is capped).</summary>
    public string Heading { get => heading; internal set => Set(ref heading, value); }

    /// <summary>A card is being dragged over this column (its outline shows where it would land).</summary>
    public bool IsDropTarget { get => isDropTarget; set => Set(ref isDropTarget, value); }
}

/// <summary>
/// The Board view (M.TIX-T77): every ticket the filters show, as a card in one of four columns, Proposed, Approved, In progress
/// and Done. Moving a card, by drag and drop or Alt+Left / Alt+Right, changes what the column stands for: its approval, its
/// work record or its status (<see cref="TicketStore.MoveCard"/>).
/// </summary>
public sealed partial class MainViewModel
{
    public const string BoardView = "Board";

    /// <summary>How many closed tickets the Done column shows, most recent first (the whole history would bury the board).</summary>
    public const int DoneLimit = 50;

    private TicketRowViewModel? selectedBoardRow;

    public bool IsBoardView => SelectedView == BoardView;

    public IReadOnlyList<BoardColumnViewModel> BoardColumns { get; } = [.. Board.Columns.Select(c => new BoardColumnViewModel(c))];

    public BoardColumnViewModel BoardColumnOf(BoardColumn c) => BoardColumns[(int)c];

    /// <summary>The column a card is in now, or null when the board doesn't show it.</summary>
    public BoardColumnViewModel? ColumnHolding(TicketRowViewModel row) => BoardColumns.FirstOrDefault(c => c.Rows.Contains(row));

    public TicketRowViewModel? SelectedBoardRow
    {
        get => selectedBoardRow;
        set { if (Set(ref selectedBoardRow, value) && value is not null) _ = ShowDetailAsync(value.Id); }
    }

    private void ApplyBoard(Snapshot s, Func<TicketInfo, bool> match)
    {
        // Open cards keep the queue's order (what /allocate takes first at the top); Done shows the most recent.
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < s.Queue.Count; i++) rank.TryAdd(s.Queue[i].Ticket.Id, i);
        var byColumn = s.Tickets.Where(match).ToLookup(t => Board.ColumnOf(t, WorkerOf(t.Ticket.Id) is { Orphaned: false }));
        foreach (var column in BoardColumns)
        {
            var cards = byColumn[column.Column];
            List<TicketInfo> shown;
            if (column.Column == BoardColumn.Done)
            {
                var all = cards.OrderByDescending(t => t.Ticket.Date, StringComparer.Ordinal)
                    .ThenByDescending(t => TicketRules.ParseNumber(t.Ticket.Id) ?? 0).ToList();
                shown = [.. all.Take(DoneLimit)];
                column.Heading = all.Count > shown.Count ? $"{column.Title}: {shown.Count} most recent of {all.Count}" : $"{column.Title}: {all.Count}";
            }
            else
            {
                shown = [.. cards.OrderBy(t => rank.TryGetValue(t.Ticket.Id, out var r) ? r : int.MaxValue).ThenBy(t => t.Ticket.Id, StringComparer.OrdinalIgnoreCase)];
                column.Heading = $"{column.Title}: {shown.Count}";
            }
            column.Rows.SyncBy(shown, t => t.Ticket.Id, r => r.Id, NewRow, UpdateRow);
        }
        if (selectedBoardRow is { } sel && ColumnHolding(sel) is null) selectedBoardRow = null;
    }

    /// <summary>Moves a card one column left (−1) or right (+1): Alt+Left / Alt+Right.</summary>
    public Task MoveCardByAsync(TicketRowViewModel? row, int delta)
    {
        if (row is null || ColumnHolding(row) is not { } from) return Task.CompletedTask;
        var to = (int)from.Column + delta;
        if (to < 0 || to >= BoardColumns.Count)
        {
            StatusMessage = Announcement = $"{row.Id} is already in the {(delta < 0 ? "first" : "last")} column, {from.Title}.";
            return Task.CompletedTask;
        }
        return MoveCardAsync(row, (BoardColumn)to);
    }

    /// <summary>
    /// Moves a card to <paramref name="to"/> (drag and drop, the keyboard): Done asks for an optional ref first (Cancel leaves it
    /// where it was); a card in Done stays there, since a closed ticket can't be reopened. Then selects and focuses the card in
    /// its new column. False when nothing changed.
    /// </summary>
    public async Task<bool> MoveCardAsync(TicketRowViewModel? row, BoardColumn to)
    {
        if (row is null) return false;
        var from = ColumnHolding(row)?.Column;
        if (from == to) return false;
        if (from == BoardColumn.Done || !row.IsOpen)
        {
            StatusMessage = Announcement = $"{row.Id} is {row.Status}: a closed ticket can't be moved back (tickets are append-only). Ctrl+Z undoes a close you just made; otherwise file a new ticket.";
            return false;
        }
        string? reference = null;
        if (to == BoardColumn.Done)
        {
            var answer = Dialogs.Ask(new Prompt($"Close {row.Id}", row.Summary,
                "Ref: test, commit or story (optional; keeps the current ref when empty)", Initial: "", Ok: "Close"));
            if (answer is null) return false;
            reference = answer.Text;
            ownCloses.Add(row.Id); // no toast for this window's own close
        }
        var title = Board.Title(to);
        var done = to switch
        {
            BoardColumn.Proposed => $"{row.Id} moved to Proposed: back to proposed.",
            BoardColumn.Approved => $"{row.Id} moved to Approved: approved.",
            BoardColumn.InProgress => $"{row.Id} moved to In progress: approved and marked as worked by hand.",
            _ => $"{row.Id} moved to Done: closed.",
        };
        if (!await Undoable(() => store.MoveCard(row.Id, to, reference), done)) return false;
        if (BoardColumnOf(to).Rows.FirstOrDefault(r => r.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)) is { } moved)
        {
            SelectedBoardRow = moved;
            if (IsBoardView) FocusRow?.Invoke(moved);
        }
        else if (BoardColumns.SelectMany(c => c.Rows).FirstOrDefault(r => r.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)) is { } still)
        {
            // Saved, but the Orchestrator still has a live worker on it, so it shows In progress until that worker lets go.
            SelectedBoardRow = still;
            StatusMessage = Announcement = $"{done} It stays in {ColumnHolding(still)!.Title} while the Orchestrator's worker has it.";
            if (IsBoardView) FocusRow?.Invoke(still);
        }
        else StatusMessage = Announcement = $"{done} The filters hide it in {title}.";
        return true;
    }
}
