using System.IO;
using System.Text;
using System.Windows.Input;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The command palette's entries (M.TIX-T81): every command that can run right now (named for the ticket it acts on), then
/// every ticket (open ones first, in queue order) and every story, each of which it can jump to.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// What the palette lists: <paramref name="windowCommands"/> (the window's own: views, search, help) first, then the
    /// commands that can run now, then tickets and stories.
    /// </summary>
    public IReadOnlyList<PaletteItem> PaletteItems(IEnumerable<PaletteItem>? windowCommands = null)
    {
        var list = new List<PaletteItem>(windowCommands ?? []);
        list.AddRange(PaletteCommands());
        if (snapshot is not { } s) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in s.Queue.Concat(s.Tickets.Where(t => t.IsOpen)).Concat(s.Tickets.Where(t => !t.IsOpen)))
        {
            if (!seen.Add(t.Ticket.Id)) continue;
            var id = t.Ticket.Id;
            var detail = t.IsOpen
                ? $"{t.Repo}, {t.Ticket.Type}, open, {t.Approval}{(t.Blocked ? ", blocked" : "")}"
                : $"{t.Repo}, {t.Ticket.Type}, {t.Ticket.Status}";
            list.Add(new PaletteItem(PaletteKind.Ticket, id, t.Ticket.Summary, detail, () => OpenTicketAsync(id)));
        }
        foreach (var story in s.Stories)
        {
            if (!seen.Add(story.Id)) continue;
            var id = story.Id;
            list.Add(new PaletteItem(PaletteKind.Story, id, StoryText(story), $"{story.Repo}, {StoryKind(story)}{(story.Status is { } st ? ", " + st : "")}",
                () => ShowStoryAsync(id)));
        }
        return list;
    }

    private IEnumerable<PaletteItem> PaletteCommands()
    {
        PaletteItem Cmd(string title, Func<Task> run, string? shortcut = null, string detail = "") =>
            new(PaletteKind.Command, title, title, detail, run, shortcut);
        Func<Task> Exec(ICommand c, object? p = null) => () => { c.Execute(p); return Task.CompletedTask; };

        yield return Cmd("Refresh", async () => { await RefreshAsync(); await RefreshGitAsync(force: true); }, "F5");
        if (UndoCommand.CanExecute(null)) yield return Cmd("Undo", UndoAsync, "Ctrl+Z", NextUndo);
        if (NewTicketCommand.CanExecute(null)) yield return Cmd("New ticket", NewTicketAsync, "Ctrl+N");
        if (SyncCommand.CanExecute(null)) yield return Cmd("Sync the ticket database", SyncAsync, "Ctrl+Shift+S", "commit and push it");
        if (IntakeCommand.CanExecute(null)) yield return Cmd("GitHub intake", IntakeAsync, detail: "preview, then file, issues and CI failures");
        if (ExportCommand.CanExecute(null)) yield return Cmd("Export the shown tickets", ExportAsync, "Ctrl+E", "as CSV or markdown");

        if (Target(null) is { IsOpen: true } row)
        {
            yield return Cmd($"Approve {row.Id}", () => ApproveAsync(row), "A");
            if (row.Approval == TicketsDb.Approved) yield return Cmd($"Back to proposed: {row.Id}", () => UnapproveAsync(row), "U");
            yield return Cmd($"Reject {row.Id}", () => RejectAsync(row), "R", "closes it as wont-do, with a reason");
            if (IsQueueView && QueueRows.IndexOf(row) > 0) yield return Cmd($"Move {row.Id} to the top of the queue", () => MoveToAsync(row, 0), "Alt+Home");
        }

        if (Detail is { } d)
        {
            if (d.IsOpen)
            {
                yield return Cmd($"Close {d.Id}", () => CloseTicketAsync(TicketRules.Closed));
                yield return Cmd($"Won't do {d.Id}", () => CloseTicketAsync(TicketRules.WontDo));
                yield return Cmd($"Assign {d.Id}", AssignAsync);
            }
            if (d.IsAssigned) yield return Cmd($"Unassign {d.Id}", UnassignAsync);
            yield return Cmd($"Set the ref of {d.Id}", SetRefAsync);
            yield return Cmd($"Correct the type of {d.Id}", CorrectTypeAsync);
            yield return Cmd($"Link {d.Id} to another ticket or story", AddLinkAsync);
            yield return Cmd($"Add a note to {d.Id}", AddNoteAsync);
            yield return Cmd($"Reserve tokens for {d.Id}", ReserveAsync);
            if (d.CanGoOlder) yield return Cmd($"Older version of {d.Id}", () => { StepVersion(-1); return Task.CompletedTask; }, "Left");
            if (d.CanGoNewer) yield return Cmd($"Newer version of {d.Id}", () => { StepVersion(+1); return Task.CompletedTask; }, "Right");
            yield return Cmd("Close the detail pane", Exec(CloseDetailCommand), "Esc");
        }

        yield return Cmd(GroupByRepo ? "Stop grouping by repo" : "Group by repo", () => { GroupByRepo = !GroupByRepo; return Task.CompletedTask; });
        yield return Cmd(ShowArchived ? "Hide closed tickets" : "Show closed tickets", () => { ShowArchived = !ShowArchived; return Task.CompletedTask; });
        if (IsFiltered || StatusFilter != AnyStatus)
            yield return Cmd("Clear search and filters", () => { ClearFilters(); ApplyFilters(); StatusMessage = Announcement = "Search and filters cleared."; return Task.CompletedTask; });
        foreach (var column in SortChoices)
            if (column != SortBy) yield return Cmd($"Sort by {column}", () => { ToggleSort(column); return Task.CompletedTask; });
        yield return Cmd(SortDescending ? "Sort ascending" : "Sort descending", () => { ToggleSort(SortBy); return Task.CompletedTask; });
        yield return Cmd("Zoom in", Exec(ZoomInCommand), "Ctrl+Plus");
        yield return Cmd("Zoom out", Exec(ZoomOutCommand), "Ctrl+Minus");
        yield return Cmd("Reset zoom", Exec(ZoomResetCommand), "Ctrl+0");
    }

    private static string StoryText(StoredStory s)
    {
        var text = s.Text.ReplaceLineEndings(" ").Trim();
        return text.Length > 0 ? text : s.Section ?? s.Epic ?? "";
    }

    private static string StoryKind(StoredStory s) =>
        s.Id.Contains("-EPIC-", StringComparison.OrdinalIgnoreCase) ? "epic" : "story";

    /// <summary>
    /// Shows the story <paramref name="id"/>: its text, epic and status, and the tickets linked to it, any of which it then
    /// opens. False, with the reason shown, when there's no such story.
    /// </summary>
    public async Task<bool> ShowStoryAsync(string id)
    {
        StoryDetail? found;
        try { found = await Task.Run(() => store.Story(id)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            TicketsLog.For("Story").Warning(ex, "Couldn't read story {Id}", id);
            StatusMessage = Announcement = $"Couldn't read {id}: {ex.Message}";
            return false;
        }
        if (found is null)
        {
            StatusMessage = Announcement = $"{id} isn't a ticket or a story.";
            return false;
        }

        var s = found.Story;
        var tickets = found.Links
            .Select(l => l.From.Equals(s.Id, StringComparison.OrdinalIgnoreCase) ? (Other: l.To, Line: $"{s.Id} {l.Kind} {l.To}") : (Other: l.From, Line: $"{l.From} {l.Kind} {s.Id}"))
            .ToList();
        var openable = tickets.Select(t => t.Other)
            .Where(o => snapshot?.Tickets.Any(t => t.Ticket.Id.Equals(o, StringComparison.OrdinalIgnoreCase)) == true)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var text = new StringBuilder(StoryText(s));
        text.Append("\n\n").Append(string.Join(" · ", new[] { string.Join(", ", found.Rows.Select(r => r.Repo).Distinct()), StoryKind(s), s.Status, s.Epic is { } e ? "epic " + e : null }
            .Where(x => !string.IsNullOrEmpty(x))));
        if (s.Acceptance is { Length: > 0 } acc) text.Append("\n\nAcceptance: ").Append(acc.Trim());
        text.Append(tickets.Count == 0 ? "\n\nNo tickets or stories are linked to it." : "\n\nLinks:\n" + string.Join("\n", tickets.Select(t => "  " + t.Line)));

        StatusMessage = $"{s.Id}: {StoryText(s)}";
        Announcement = $"Story {s.Id}.";
        var answer = Dialogs.Ask(openable.Count > 0
            ? new Prompt(s.Id, text.ToString(), TextLabel: null, ChoiceLabel: "Linked ticket", Choices: openable, Ok: "Open ticket")
            : new Prompt(s.Id, text.ToString(), TextLabel: null, Ok: "Close"));
        if (answer?.Choice is { } pick) await OpenTicketAsync(pick);
        return true;
    }
}
