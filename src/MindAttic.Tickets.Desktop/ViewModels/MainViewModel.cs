using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The window: the Queue (open tickets in priority order, to approve, reject and reorder) and Tickets (every ticket,
/// filtered and sorted), a detail pane for the selected ticket, and live refresh. Database work runs off the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string QueueView = "Queue", TicketsView = "Tickets";
    public const string AllRepos = "All repos", AnyStatus = "Any status", AnyType = "Any type", AnyApproval = "Any approval";

    private readonly TicketStore store;
    private readonly DesktopSettings settings;
    private Snapshot? snapshot;
    private string selectedView;
    private string search = "", repoFilter = AllRepos, statusFilter = AnyStatus, typeFilter = AnyType, approvalFilter = AnyApproval;
    private string sortBy = "ID";
    private bool sortDescending, showArchived, busy;
    private TicketRowViewModel? selectedQueueRow, selectedTicketRow;
    private DetailViewModel? detail;
    private string announcement = "", statusMessage = "", lastRefresh = "", headline = "";

    public MainViewModel(TicketStore store, DesktopSettings settings, IDialogs? dialogs = null)
    {
        this.store = store;
        this.settings = settings;
        Dialogs = dialogs ?? new NoDialogs();
        selectedView = settings.View == TicketsView ? TicketsView : QueueView;
        zoom = Math.Clamp(settings.Zoom, MinZoom, MaxZoom);

        RefreshCommand = new RelayCommand(async () => await RefreshAsync());
        NewTicketCommand = new RelayCommand(async () => await NewTicketAsync(), () => snapshot is not null);
        SyncCommand = new RelayCommand(async () => await SyncAsync(), () => !busy);
        ApproveCommand = new RelayCommand(async p => await ApproveAsync(Target(p)), p => Target(p) is { IsOpen: true });
        UnapproveCommand = new RelayCommand(async p => await UnapproveAsync(Target(p)), p => Target(p) is { IsOpen: true, Approval: TicketsDb.Approved });
        RejectCommand = new RelayCommand(async p => await RejectAsync(Target(p)), p => Target(p) is { IsOpen: true });
        MoveUpCommand = new RelayCommand(async p => await MoveByAsync(QueueTarget(p), -1), p => QueueTarget(p) is { } r && QueueRows.IndexOf(r) > 0);
        MoveDownCommand = new RelayCommand(async p => await MoveByAsync(QueueTarget(p), +1), p => QueueTarget(p) is { } r && QueueRows.IndexOf(r) is var i && i >= 0 && i < QueueRows.Count - 1);
        MoveTopCommand = new RelayCommand(async p => await MoveToAsync(QueueTarget(p), 0), p => QueueTarget(p) is { } r && QueueRows.IndexOf(r) > 0);
        CloseDetailCommand = new RelayCommand(() => Detail = null, () => Detail is not null);
        CloseTicketCommand = new RelayCommand(async () => await CloseTicketAsync(TicketRules.Closed), () => Detail is { IsOpen: true });
        WontDoCommand = new RelayCommand(async () => await CloseTicketAsync(TicketRules.WontDo), () => Detail is { IsOpen: true });
        CorrectTypeCommand = new RelayCommand(async () => await CorrectTypeAsync(), () => Detail is not null);
        AddLinkCommand = new RelayCommand(async () => await AddLinkAsync(), () => Detail is not null);
        RemoveLinkCommand = new RelayCommand(async p => await RemoveLinkAsync(p as LinkLine), p => p is LinkLine);
        GoToCommand = new RelayCommand(p => GoTo(p as string), p => p is string);
        ToggleSortCommand = new RelayCommand(p => ToggleSort(p as string), p => p is string);
    }

    public IDialogs Dialogs { get; set; }

    // ── What's shown ────────────────────────────────────────────────────────

    public IReadOnlyList<string> Views { get; } = [QueueView, TicketsView];

    public string SelectedView
    {
        get => selectedView;
        set
        {
            if (!Set(ref selectedView, value)) return;
            Raise(nameof(IsQueueView));
            Raise(nameof(IsTicketsView));
            settings.View = value;
            settings.Save();
        }
    }

    public bool IsQueueView => SelectedView == QueueView;
    public bool IsTicketsView => SelectedView == TicketsView;

    public ObservableCollection<TicketRowViewModel> QueueRows { get; } = [];
    public ObservableCollection<TicketRowViewModel> TicketRows { get; } = [];
    public ObservableCollection<string> RepoChoices { get; } = [AllRepos];
    public IReadOnlyList<string> StatusChoices { get; } = [AnyStatus, .. TicketRules.Statuses];
    public IReadOnlyList<string> TypeChoices { get; } = [AnyType, .. TicketRules.Types];
    public IReadOnlyList<string> ApprovalChoices { get; } = [AnyApproval, .. TicketsDb.Approvals];
    public IReadOnlyList<string> SortChoices { get; } = ["ID", "Date", "Repo", "Type", "Status", "Approval", "Priority", "Summary"];

    public string Search { get => search; set { if (Set(ref search, value)) ApplyFilters(); } }
    public string RepoFilter { get => repoFilter; set { if (Set(ref repoFilter, value ?? AllRepos)) ApplyFilters(); } }
    public string StatusFilter { get => statusFilter; set { if (Set(ref statusFilter, value ?? AnyStatus)) ApplyFilters(); } }
    public string TypeFilter { get => typeFilter; set { if (Set(ref typeFilter, value ?? AnyType)) ApplyFilters(); } }
    public string ApprovalFilter { get => approvalFilter; set { if (Set(ref approvalFilter, value ?? AnyApproval)) ApplyFilters(); } }
    public bool ShowArchived { get => showArchived; set { if (Set(ref showArchived, value)) ApplyFilters(); } }
    public string SortBy { get => sortBy; set { if (Set(ref sortBy, value ?? "ID")) ApplyFilters(); } }
    public bool SortDescending { get => sortDescending; set { if (Set(ref sortDescending, value)) ApplyFilters(); } }

    public bool IsFiltered => Search.Length > 0 || RepoFilter != AllRepos || TypeFilter != AnyType || ApprovalFilter != AnyApproval;

    public TicketRowViewModel? SelectedQueueRow
    {
        get => selectedQueueRow;
        set { if (Set(ref selectedQueueRow, value) && value is not null) _ = ShowDetailAsync(value.Id); }
    }

    public TicketRowViewModel? SelectedTicketRow
    {
        get => selectedTicketRow;
        set { if (Set(ref selectedTicketRow, value) && value is not null) _ = ShowDetailAsync(value.Id); }
    }

    public DetailViewModel? Detail
    {
        get => detail;
        private set
        {
            if (!Set(ref detail, value)) return;
            Raise(nameof(IsDetailOpen));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsDetailOpen => Detail is not null;

    /// <summary>The header's one-line summary of the queue.</summary>
    public string Headline { get => headline; private set => Set(ref headline, value); }

    /// <summary>Said by a screen reader as it changes, without moving focus (4.1.3).</summary>
    public string Announcement { get => announcement; private set { announcement = ""; Raise(); Set(ref announcement, value); } }

    public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public string LastRefresh { get => lastRefresh; private set => Set(ref lastRefresh, value); }
    public string DbPath => store.DbPath;
    public bool Busy { get => busy; private set => Set(ref busy, value); }

    /// <summary>Raised when a row should take keyboard focus (after a move, or a jump to a linked ticket).</summary>
    public event Action<TicketRowViewModel>? FocusRow;

    // ── Commands ────────────────────────────────────────────────────────────

    public ICommand RefreshCommand { get; }
    public ICommand NewTicketCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand UnapproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand MoveTopCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand CloseTicketCommand { get; }
    public ICommand WontDoCommand { get; }
    public ICommand CorrectTypeCommand { get; }
    public ICommand AddLinkCommand { get; }
    public ICommand RemoveLinkCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand ToggleSortCommand { get; }

    /// <summary>The row a command acts on: its parameter, else the selection in the current view.</summary>
    private TicketRowViewModel? Target(object? p) => p as TicketRowViewModel ?? (IsQueueView ? SelectedQueueRow : SelectedTicketRow);

    private TicketRowViewModel? QueueTarget(object? p) => p as TicketRowViewModel ?? (IsQueueView ? SelectedQueueRow : null);

    // ── Loading ─────────────────────────────────────────────────────────────

    private int refreshRequest;

    public async Task RefreshAsync()
    {
        // The poll, the file watcher and every action refresh independently; a snapshot read before a change must not
        // land after (and overwrite) one read after it.
        var request = ++refreshRequest;
        try
        {
            var s = await Task.Run(store.Load);
            if (request != refreshRequest) return;
            snapshot = s;
            foreach (var r in s.Repos.Where(r => !RepoChoices.Contains(r))) RepoChoices.Add(r);
            ApplyFilters();
            if (Detail is { } d) await ShowDetailAsync(d.Id, quiet: true);
            LastRefresh = s.Read.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            TicketsLog.For("Refresh").Warning(ex, "Couldn't read {Db}", store.DbPath);
            StatusMessage = $"Couldn't read the ticket database: {ex.Message}";
            Announcement = StatusMessage;
        }
    }

    private void ApplyFilters()
    {
        Raise(nameof(IsFiltered));
        if (snapshot is not { } s) return;

        bool Match(TicketInfo t) =>
            (RepoFilter == AllRepos || t.Repo.Equals(RepoFilter, StringComparison.OrdinalIgnoreCase)) &&
            (TypeFilter == AnyType || t.Ticket.Type == TypeFilter) &&
            (ApprovalFilter == AnyApproval || t.Approval == ApprovalFilter) &&
            (Search.Length == 0 || t.Ticket.Id.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             t.Ticket.Summary.Contains(Search, StringComparison.OrdinalIgnoreCase) || t.Ticket.Ref.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             t.Repo.Contains(Search, StringComparison.OrdinalIgnoreCase));

        QueueRows.SyncBy(s.Queue.Where(Match), t => t.Ticket.Id, r => r.Id, TicketRowViewModel.From, (r, t) => r.Update(t));
        for (var i = 0; i < QueueRows.Count; i++) QueueRows[i].Rank = i + 1;

        var tickets = s.Tickets.Where(Match)
            .Where(t => StatusFilter != AnyStatus ? t.Ticket.Status == StatusFilter : ShowArchived || t.Ticket.Status == TicketRules.Open);
        TicketRows.SyncBy(Sort(tickets), t => t.Ticket.Id, r => r.Id, TicketRowViewModel.From, (r, t) => r.Update(t));

        var ready = s.Queue.Count(t => t.Ticket.Type == "fix" || t.Approval == TicketsDb.Approved);
        Headline = $"{s.Queue.Count} open · {ready} ready for autopilot · {s.Queue.Count - ready} awaiting approval · {s.Repos.Count} repos";
        StatusMessage = IsQueueView
            ? $"Queue: {QueueRows.Count} of {s.Queue.Count} open tickets shown."
            : $"Tickets: {TicketRows.Count} of {s.Tickets.Count} shown.";
    }

    private IEnumerable<TicketInfo> Sort(IEnumerable<TicketInfo> rows)
    {
        Func<TicketInfo, IComparable> key = SortBy switch
        {
            "Date" => t => t.Ticket.Date,
            "Repo" => t => t.Repo.ToUpperInvariant(),
            "Type" => t => t.Ticket.Type,
            "Status" => t => t.Ticket.Status,
            "Approval" => t => t.Approval,
            "Priority" => t => t.Priority,
            "Summary" => t => t.Ticket.Summary.ToUpperInvariant(),
            _ => t => t.Repo.ToUpperInvariant(),
        };
        var ordered = SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return ordered.ThenBy(t => t.Repo, StringComparer.OrdinalIgnoreCase).ThenBy(t => TicketRules.ParseNumber(t.Ticket.Id) ?? 0);
    }

    private void ToggleSort(string? column)
    {
        if (column is null) return;
        if (SortBy == column) SortDescending = !SortDescending;
        else { sortDescending = false; Raise(nameof(SortDescending)); SortBy = column; }
        Announcement = $"Sorted by {SortBy}, {(SortDescending ? "descending" : "ascending")}.";
    }

    private int detailRequest;

    private async Task ShowDetailAsync(string id, bool quiet = false)
    {
        // Loads run on the thread pool and can finish out of order (arrowing through rows, a refresh beside a click):
        // only the latest request may set the pane, or it shows, and Close / Won't do act on, a ticket that isn't selected.
        var request = ++detailRequest;
        try
        {
            var d = await Task.Run(() => store.Detail(id));
            if (request != detailRequest) return;
            Detail = d is null ? null : new DetailViewModel(d);
            if (!quiet && Detail is { } v) StatusMessage = $"{v.Id}: {v.WorkText}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Shows <paramref name="id"/>: selects it in the current view if it's there, else in Tickets with filters cleared.</summary>
    public void GoTo(string? id)
    {
        if (id is null || snapshot is null) return;
        if (snapshot.Tickets.All(t => !t.Ticket.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"{id} isn't a ticket (stories open in a later version).";
            Announcement = StatusMessage;
            return;
        }
        var row = (IsQueueView ? QueueRows : TicketRows).FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            ClearFilters();
            SelectedView = TicketsView;
            ApplyFilters();
            row = TicketRows.First(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        if (IsQueueView) SelectedQueueRow = row; else SelectedTicketRow = row;
        FocusRow?.Invoke(row);
    }

    /// <summary>Clears search and every filter and shows archived tickets, without re-filtering (the caller does).</summary>
    private void ClearFilters()
    {
        search = ""; repoFilter = AllRepos; typeFilter = AnyType; approvalFilter = AnyApproval; statusFilter = AnyStatus; showArchived = true;
        foreach (var p in new[] { nameof(Search), nameof(RepoFilter), nameof(TypeFilter), nameof(ApprovalFilter), nameof(StatusFilter), nameof(ShowArchived) }) Raise(p);
    }

    /// <summary>
    /// Opens <paramref name="id"/> as <c>--ticket</c> asks (at start, or handed over by a second start): reads the database
    /// afresh (a link can name a ticket filed seconds ago), selects the ticket and opens its detail pane, even when it was
    /// already selected with the pane closed. False, with the reason shown, when there's no such ticket.
    /// </summary>
    public async Task<bool> OpenTicketAsync(string id)
    {
        await RefreshAsync();
        var known = snapshot?.Tickets.FirstOrDefault(t => t.Ticket.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            StatusMessage = snapshot is null ? $"Couldn't open {id}: the ticket database isn't readable." : $"{id} isn't a ticket.";
            Announcement = StatusMessage;
            return false;
        }
        id = known.Ticket.Id;

        // An open ticket opens in the Queue, where it can be approved and moved up or down as well as closed; a closed
        // one in Tickets. Filters that hide it are cleared.
        var open = known.Ticket.Status == TicketRules.Open;
        SelectedView = open ? QueueView : TicketsView;
        var rows = open ? QueueRows : TicketRows;
        bool Shown(TicketRowViewModel r) => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase);
        if (!rows.Any(Shown))
        {
            ClearFilters();
            ApplyFilters();
        }
        if (rows.FirstOrDefault(Shown) is { } row)
        {
            if (open) SelectedQueueRow = row; else SelectedTicketRow = row;
            FocusRow?.Invoke(row);
        }
        else GoTo(id);
        await ShowDetailAsync(id);
        Announcement = $"Opened {id}.";
        return Detail?.Id == id;
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    /// <summary>Runs a database change off the UI thread, then refreshes and announces <paramref name="done"/>.</summary>
    private async Task<bool> Do(Action work, string done)
    {
        if (Detail is { } d) d.Error = "";
        Busy = true;
        try
        {
            await Task.Run(work);
            await RefreshAsync();
            StatusMessage = done;
            Announcement = done;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or System.Data.Common.DbException)
        {
            TicketsLog.For("Action").Warning(ex, "{Action:l} failed: {Message:l}", done, ex.Message);
            if (Detail is { } dd) dd.Error = ex.Message;
            StatusMessage = ex.Message;
            Announcement = "Error: " + ex.Message;
            return false;
        }
        finally
        {
            Busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public Task ApproveAsync(TicketRowViewModel? row) =>
        row is null ? Task.CompletedTask : Do(() => store.Approve(row.Id), $"{row.Id} approved.");

    public Task UnapproveAsync(TicketRowViewModel? row) =>
        row is null ? Task.CompletedTask : Do(() => store.Unapprove(row.Id), $"{row.Id} back to proposed.");

    public async Task RejectAsync(TicketRowViewModel? row)
    {
        if (row is null) return;
        var answer = Dialogs.Ask(new Prompt($"Reject {row.Id}", $"{row.Summary}\n\nRejecting closes it as wont-do. Why?", "Reason", Ok: "Reject"));
        if (answer is null) return;
        await Do(() => store.Reject(row.Id, answer.Text), $"{row.Id} rejected and closed as wont-do.");
    }

    /// <summary>Moves a queue row by <paramref name="delta"/> places (Alt+Up / Alt+Down, the ▲▼ buttons).</summary>
    public Task MoveByAsync(TicketRowViewModel? row, int delta) =>
        row is null ? Task.CompletedTask : MoveToAsync(row, QueueRows.IndexOf(row) + delta);

    /// <summary>Moves a queue row to <paramref name="index"/> in the list as shown (drag and drop, keyboard, buttons).</summary>
    public async Task MoveToAsync(TicketRowViewModel? row, int index)
    {
        if (row is null) return;
        var from = QueueRows.IndexOf(row);
        index = Math.Clamp(index, 0, QueueRows.Count - 1);
        if (from < 0 || from == index) return;
        var order = QueueRows.Select(r => r.Id).ToList();
        order.RemoveAt(from);
        order.Insert(index, row.Id);
        if (await Do(() => store.Move(row.Id, order), $"{row.Id} moved to position {index + 1} of {order.Count}."))
        {
            SelectedQueueRow = row;
            FocusRow?.Invoke(row);
        }
    }

    private async Task CloseTicketAsync(string status)
    {
        if (Detail is not { } d) return;
        var verb = status == TicketRules.WontDo ? "Won't do" : "Close";
        var answer = Dialogs.Ask(new Prompt($"{verb} {d.Id}", d.Summary,
            status == TicketRules.WontDo ? "Reason (becomes its ref)" : "Ref: test, commit or story (optional; keeps the current ref when empty)",
            TextRequired: status == TicketRules.WontDo, Initial: "", Ok: verb));
        if (answer is null) return;
        await Do(() => store.Close(d.Id, status, answer.Text), $"{d.Id} {status}.");
    }

    private async Task CorrectTypeAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Correct the type of {d.Id}", "Types are append-only; a correction is recorded in the ticket's history with your reason.",
            "Reason", ChoiceLabel: "Type", Choices: TicketRules.Types, Choice: d.Type, Ok: "Correct"));
        if (answer?.Choice is not { } type) return;
        await Do(() => store.CorrectType(d.Id, type, answer.Text), $"{d.Id} is now a {type}.");
    }

    private async Task AddLinkAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Link {d.Id}", $"{d.Id} [kind] the ticket or story you name. 'requires' means {d.Id} waits for it.",
            "Other ticket or story ID", ChoiceLabel: "Kind", Choices: TicketsDb.LinkKinds, Choice: "requires", Ok: "Link"));
        if (answer?.Choice is not { } kind) return;
        var other = answer.Text.Trim().ToUpperInvariant();
        await Do(() => store.AddLink(d.Id, other, kind), $"{d.Id} {kind} {other}.");
    }

    private async Task RemoveLinkAsync(LinkLine? line)
    {
        if (line is null) return;
        await Do(() => store.RemoveLink(line.Link.From, line.Link.To, line.Link.Kind), $"Removed: {line.Link.From} {line.Link.Kind} {line.Link.To}.");
    }

    public async Task NewTicketAsync()
    {
        if (snapshot is null) return;
        var repo = RepoFilter != AllRepos ? RepoFilter : settings.LastRepo;
        if (Dialogs.NewTicket(snapshot.Repos, repo, settings.LastType) is not { } n) return;
        settings.LastRepo = n.Repo;
        settings.LastType = n.Type;
        settings.Save();
        string? id = null;
        if (await Do(() => id = store.Add(n.Repo, n.Type, n.Summary, n.Ref).Id, $"Filed {n.Repo} ticket.") && id is not null)
        {
            StatusMessage = Announcement = $"Filed {id}.";
            GoTo(id);
        }
    }

    public async Task SyncAsync()
    {
        Busy = true;
        StatusMessage = "Syncing the ticket database…";
        try
        {
            var (code, output) = await store.SyncAsync();
            var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            StatusMessage = code == 0 ? $"Synced. {last}" : $"Sync failed ({code}): {last}";
            Announcement = StatusMessage;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusMessage = Announcement = $"Couldn't run 'tickets db sync': {ex.Message}";
        }
        finally
        {
            Busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private sealed class NoDialogs : IDialogs
    {
        public PromptAnswer? Ask(Prompt prompt) => null;
        public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type) => null;
    }
}
