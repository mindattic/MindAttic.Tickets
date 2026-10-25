using MindAttic.Tickets.Data;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>The Board view's columns, left to right (M.TIX-T77).</summary>
public enum BoardColumn { Proposed, Approved, InProgress, Done }

public static class Board
{
    public static IReadOnlyList<BoardColumn> Columns { get; } = [BoardColumn.Proposed, BoardColumn.Approved, BoardColumn.InProgress, BoardColumn.Done];

    /// <summary>
    /// Where a ticket sits on the board: Done when it's closed or wont-do; In progress when it's open and a session works it
    /// (recorded in the ticket database, or <paramref name="liveWorker"/>: the Orchestrator has a live worker on it); else
    /// Approved or Proposed by its approval.
    /// </summary>
    public static BoardColumn ColumnOf(TicketInfo t, bool liveWorker = false) =>
        !t.IsOpen ? BoardColumn.Done
        : t.Working || liveWorker ? BoardColumn.InProgress
        : t.Approval == TicketsDb.Approved ? BoardColumn.Approved
        : BoardColumn.Proposed;

    public static string Title(BoardColumn c) => c switch
    {
        BoardColumn.Proposed => "Proposed",
        BoardColumn.Approved => "Approved",
        BoardColumn.InProgress => "In progress",
        _ => "Done",
    };

    /// <summary>The session name a card moved to In progress by hand is recorded as working under.</summary>
    public const string HandSession = "tickets-desktop";
}
