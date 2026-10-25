using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The window's tabs, Repos | Queue | Audit (M.TIX-T135): Repos (every ticket, filtered and sorted, under a heading per
/// repo with its git state and a Start link), the Queue (open tickets in the order /allocate hands them out, to approve,
/// reject and reorder, each claimed one showing its live worker) and Audit (the Orchestrator's drift and duplicate-code
/// findings); a detail pane for the selected ticket, and live refresh. Database work runs off the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The tabs. <see cref="TicketsView"/> is the Repos tab (it was "Tickets" before M.TIX-T135).</summary>
    public const string QueueView = "Queue", TicketsView = "Repos", ReposView = TicketsView, AuditView = "Audit";
    public const string AllRepos = "All repos", AnyStatus = "Any status", AnyType = "Any type", AnyApproval = "Any approval", AnyLabel = "Any label";

    /// <summary>The work filter (M.TIX-T133): where an open ticket stands with /allocate.</summary>
    public const string AnyWork = "Any", Queued = "Queued", NeedsApproval = "Needs approval", Blocked = "Blocked";

    private readonly TicketStore store;
    private readonly DesktopSettings settings;
    private Snapshot? snapshot;
    private string selectedView;
    private string search = "", repoFilter = AllRepos, statusFilter = AnyStatus, typeFilter = AnyType, approvalFilter = AnyApproval, workFilter = AnyWork, labelFilter = AnyLabel;
    private string sortBy = "ID";
    private bool sortDescending, showArchived, busy, groupByRepo;
    private TicketRowViewModel? selectedQueueRow, selectedTicketRow;
    private DetailViewModel? detail;
    private string announcement = "", statusMessage = "", lastRefresh = "", headline = "";

    public MainViewModel(TicketStore store, DesktopSettings settings, IDialogs? dialogs = null, OrchestratorState? orchestrator = null)
    {
        this.store = store;
        this.settings = settings;
        Dialogs = dialogs ?? new NoDialogs();
        selectedView = settings.View switch { TicketsView or "Tickets" => TicketsView, AuditView => AuditView, BoardView => BoardView, ActivityView => ActivityView, StatsView => StatsView, StoriesView => StoriesView, _ => QueueView };
        InitOrchestrator(orchestrator);
        InitQuery();
        InitExport();
        InitStories();
        zoom = Math.Clamp(settings.Zoom, MinZoom, MaxZoom);
        groupByRepo = settings.GroupByRepo;

        // F5 re-reads every repo's git state too (the poll leaves that to its own slower interval).
        RefreshCommand = new RelayCommand(async () => { await RefreshAsync(); await RefreshGitAsync(force: true); });
        NewTicketCommand = new RelayCommand(async () => await NewTicketAsync(), () => snapshot is not null);
        SyncCommand = new RelayCommand(async () => await SyncAsync(), () => !busy);
        // A button in a row acts on that row; the action bar's act on the selection, every selected ticket when several are (M.TIX-T79).
        ApproveCommand = new RelayCommand(async p => await ApproveRowsAsync(Targets(p)), p => Targets(p).Any(r => r.IsOpen));
        UnapproveCommand = new RelayCommand(async p => await UnapproveRowsAsync(Targets(p)), p => Targets(p).Any(r => r is { IsOpen: true, Approval: TicketsDb.Approved }));
        RejectCommand = new RelayCommand(async p => await RejectRowsAsync(Targets(p)), p => Targets(p).Any(r => r.IsOpen));
        InitBulk();
        MoveUpCommand = new RelayCommand(async p => await MoveByAsync(QueueTarget(p), -1), p => QueueTarget(p) is { } r && QueueRows.IndexOf(r) > 0);
        MoveDownCommand = new RelayCommand(async p => await MoveByAsync(QueueTarget(p), +1), p => QueueTarget(p) is { } r && QueueRows.IndexOf(r) is var i && i >= 0 && i < QueueRows.Count - 1);
        MoveTopCommand = new RelayCommand(async p => { if (p is null && IsMultiSelect) await MoveRowsAsync(SelectedRows, toTop: true); else await MoveToAsync(QueueTarget(p), 0); },
            p => p is null && IsMultiSelect ? IsQueueView : QueueTarget(p) is { } r && QueueRows.IndexOf(r) > 0);
        CloseDetailCommand = new RelayCommand(() => Detail = null, () => Detail is not null);
        CloseTicketCommand = new RelayCommand(async () => await CloseTicketAsync(TicketRules.Closed), () => Detail is { IsOpen: true });
        WontDoCommand = new RelayCommand(async () => await CloseTicketAsync(TicketRules.WontDo), () => Detail is { IsOpen: true });
        SetRefCommand = new RelayCommand(async () => await SetRefAsync(), () => Detail is not null);
        CorrectTypeCommand = new RelayCommand(async () => await CorrectTypeAsync(), () => Detail is not null);
        CorrectSummaryCommand = new RelayCommand(async () => await CorrectSummaryAsync(), () => Detail is not null);
        EditDescriptionCommand = new RelayCommand(async () => await EditDescriptionAsync(), () => Detail is not null);
        AddLinkCommand = new RelayCommand(async () => await AddLinkAsync(), () => Detail is not null);
        RemoveLinkCommand = new RelayCommand(async p => await RemoveLinkAsync(p as LinkLine), p => p is LinkLine);
        GoToCommand = new RelayCommand(p => GoTo(p as string), p => p is string);
        OlderVersionCommand = new RelayCommand(() => StepVersion(-1), () => Detail is { CanGoOlder: true });
        NewerVersionCommand = new RelayCommand(() => StepVersion(+1), () => Detail is { CanGoNewer: true });
        ToggleSortCommand = new RelayCommand(p => ToggleSort(p as string), p => p is string);
        UndoCommand = new RelayCommand(async () => await UndoAsync(), () => CanUndo && !Busy);
        DismissUndoToastCommand = new RelayCommand(HideUndoToast, () => IsUndoToastShown);
    }

    public IDialogs Dialogs { get; set; }

    // ── What's shown ────────────────────────────────────────────────────────

    public IReadOnlyList<string> Views { get; } = [ReposView, QueueView, AuditView, BoardView, ActivityView, StatsView, StoriesView];

    public string SelectedView
    {
        get => selectedView;
        set
        {
            if (!Set(ref selectedView, value)) return;
            Raise(nameof(IsQueueView));
            Raise(nameof(IsTicketsView));
            Raise(nameof(IsAuditView));
            Raise(nameof(IsBoardView));
            Raise(nameof(IsActivityView));
            Raise(nameof(IsStatsView));
            Raise(nameof(IsStoriesView));
            Raise(nameof(ShowsStoryEditor));
            Raise(nameof(ShowsDetailPane));
            Raise(nameof(ShowsTicketFilters));
            settings.View = value;
            settings.Save();
            ApplyFilters();
            if (IsTicketsView) _ = RefreshGitAsync();
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
    public IReadOnlyList<string> WorkChoices { get; } = [AnyWork, Queued, NeedsApproval, Blocked];
    public IReadOnlyList<string> SortChoices { get; } = ["ID", "Date", "Repo", "Type", "Status", "Approval", "Priority", "Estimate", "Summary"];

    public string Search { get => search; set { if (Set(ref search, value)) ApplyFilters(); } }
    public string RepoFilter { get => repoFilter; set { if (Set(ref repoFilter, value ?? AllRepos)) ApplyFilters(); } }
    public string StatusFilter { get => statusFilter; set { if (Set(ref statusFilter, value ?? AnyStatus)) ApplyFilters(); } }
    public string TypeFilter { get => typeFilter; set { if (Set(ref typeFilter, value ?? AnyType)) ApplyFilters(); } }
    public string ApprovalFilter { get => approvalFilter; set { if (Set(ref approvalFilter, value ?? AnyApproval)) ApplyFilters(); } }
    public string WorkFilter { get => workFilter; set { if (Set(ref workFilter, value ?? AnyWork)) ApplyFilters(); } }

    /// <summary>Every label in use, after <see cref="AnyLabel"/> (M.TIX-T74).</summary>
    public ObservableCollection<string> LabelChoices { get; } = [AnyLabel];

    /// <summary>Only tickets with this label (M.TIX-T74); a component is a <c>component:</c> label.</summary>
    public string LabelFilter { get => labelFilter; set { if (Set(ref labelFilter, value ?? AnyLabel)) ApplyFilters(); } }

    /// <summary>
    /// Tickets listed under a heading per repo, each repo's tickets together (the owner: "organized by repos, and each repo
    /// has tickets"); the sort then orders tickets within each repo. Remembered between runs.
    /// </summary>
    public bool GroupByRepo
    {
        get => groupByRepo;
        set
        {
            if (!Set(ref groupByRepo, value)) return;
            settings.GroupByRepo = value;
            settings.Save();
            ApplyFilters();
        }
    }

    public bool ShowArchived { get => showArchived; set { if (Set(ref showArchived, value)) ApplyFilters(); } }
    public string SortBy { get => sortBy; set { if (Set(ref sortBy, value ?? "ID")) ApplyFilters(); } }
    public bool SortDescending { get => sortDescending; set { if (Set(ref sortDescending, value)) ApplyFilters(); } }

    public bool IsFiltered => Search.Length > 0 || !parsedQuery.IsEmpty || RepoFilter != AllRepos || TypeFilter != AnyType || ApprovalFilter != AnyApproval || WorkFilter != AnyWork || LabelFilter != AnyLabel;

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
            Raise(nameof(ShowsDetailPane));
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
    public ICommand SetRefCommand { get; }
    public ICommand CorrectTypeCommand { get; }
    public ICommand CorrectSummaryCommand { get; }
    public ICommand EditDescriptionCommand { get; }
    public ICommand AddLinkCommand { get; }
    public ICommand RemoveLinkCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand OlderVersionCommand { get; }
    public ICommand NewerVersionCommand { get; }
    public ICommand ToggleSortCommand { get; }

    /// <summary>The row a command acts on: its parameter, else the selection in the current view.</summary>
    private TicketRowViewModel? Target(object? p) => p as TicketRowViewModel ??
        (IsQueueView ? SelectedQueueRow : IsBoardView ? SelectedBoardRow : IsActivityView ? DetailRow : SelectedTicketRow);

    /// <summary>The listed row of the ticket the detail pane shows (Activity lists changes, not tickets).</summary>
    private TicketRowViewModel? DetailRow => Detail is { } d
        ? QueueRows.Concat(TicketRows).FirstOrDefault(r => r.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase)) : null;

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
            var orch = LoadOrchestratorAsync();
            var s = await Task.Run(store.Load);
            await orch;
            if (request != refreshRequest) return;
            snapshot = s;
            SyncSavedFilters(s);
            foreach (var r in s.Repos.Where(r => !RepoChoices.Contains(r))) RepoChoices.Add(r);
            SyncLabelChoices(s.LabelNames);
            ApplyFilters();
            if (Detail is { } d) await ShowDetailAsync(d.Id, quiet: true);
            await RefreshActivityAsync();
            if (StoryEditor is { } ed) await LoadStoryAsync(ed.Repo, ed.Id, quiet: true);
            LastRefresh = s.Read.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            CommandManager.InvalidateRequerySuggested();
            _ = RefreshGitAsync();
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
            WorkMatch(t) &&
            QueryMatch(t) &&
            (LabelFilter == AnyLabel || t.Labels.Contains(LabelFilter, StringComparer.Ordinal)) &&
            (Search.Length == 0 || t.Ticket.Id.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             t.Ticket.Summary.Contains(Search, StringComparison.OrdinalIgnoreCase) || t.Ticket.Ref.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             t.Repo.Contains(Search, StringComparison.OrdinalIgnoreCase) || t.Labels.Any(l => l.Contains(Search, StringComparison.OrdinalIgnoreCase)));

        ApplyRepoHeaders(s);
        QueueRows.SyncBy(s.Queue.Where(Match), t => t.Ticket.Id, r => r.Id, NewRow, UpdateRow);
        for (var i = 0; i < QueueRows.Count; i++) QueueRows[i].Rank = i + 1;

        // A query naming a status picks it, as the Status box does: archived tickets aren't hidden from it.
        var queryPicksStatus = parsedQuery.Terms.Any(q => q.Field == TicketQuery.StatusField);
        var tickets = s.Tickets.Where(Match)
            .Where(t => StatusFilter != AnyStatus ? t.Ticket.Status == StatusFilter : ShowArchived || queryPicksStatus || t.Ticket.Status == TicketRules.Open);
        TicketRows.SyncBy(Sort(tickets), t => t.Ticket.Id, r => r.Id, NewRow, UpdateRow);
        ApplyBoard(s, Match);
        ApplyStories(s);
        ApplyAudit();
        ApplyHygiene(s);
        ApplyActivity();
        ApplyStats(s);

        var ready = s.Queue.Count(t => t.Ticket.Type == "fix" || t.Approval == TicketsDb.Approved);
        var blocked = s.Queue.Count(t => t.Blocked);
        var working = s.Queue.Count(t => WorkerOf(t.Ticket.Id) is { Orphaned: false });
        Headline = $"{s.Queue.Count} open · {ready} ready for /allocate · {s.Queue.Count - ready} awaiting approval · {blocked} blocked · {working} being worked · {s.Repos.Count} repos";
        StatusMessage = IsQueueView ? $"Queue: {QueueRows.Count} of {s.Queue.Count} open tickets shown."
            : IsBoardView ? $"Board: {string.Join(", ", BoardColumns.Select(c => $"{c.Rows.Count} {c.Title.ToLowerInvariant()}"))} shown."
            : IsActivityView ? ActivityStatus
            : IsStatsView ? $"Stats: {Metrics?.Periods.Count ?? 0} {StatsBy.ToLowerInvariant()}s, {Metrics?.Periods.Sum(p => p.Closed) ?? 0} closed, {Metrics?.Periods.LastOrDefault()?.OpenAtEnd ?? 0} open now."
            : IsStoriesView ? $"Stories: {StoryRows.Count} of {s.Stories.Count} stories shown."
            : IsAuditView ? $"Audit: {HygieneRows.Count} closed tickets to double-check, {DriftRows.Count} drifted, {MissingRows.Count} missing, {DuplicateRows.Count} duplicate groups shown."
            : $"Repos: {TicketRows.Count} of {s.Tickets.Count} tickets shown.";
    }

    private bool WorkMatch(TicketInfo t) => WorkFilter switch
    {
        Queued => t.Queued,
        NeedsApproval => t.NeedsApproval,
        Blocked => t.Blocked,
        _ => true,
    };

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
            // Smallest effort first; a ticket with no estimate after every one that has.
            "Estimate" => t => t.Estimate ?? long.MaxValue,
            "Summary" => t => t.Ticket.Summary.ToUpperInvariant(),
            // An ID is its repo then its number (T9 before T10), so descending reverses both, not just the repo.
            _ => t => (t.Repo.ToUpperInvariant(), TicketRules.ParseNumber(t.Ticket.Id) ?? 0),
        };
        // Grouped by repo, each repo's tickets stay together (the list's headings follow the rows' order), sorted within it.
        IOrderedEnumerable<TicketInfo> ordered;
        if (GroupByRepo)
        {
            var byRepo = rows.OrderBy(t => t.Repo, StringComparer.OrdinalIgnoreCase);
            ordered = SortDescending ? byRepo.ThenByDescending(key) : byRepo.ThenBy(key);
        }
        else ordered = SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
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
            // A refresh keeps an older version on screen if one was stepped to; on the current one, it follows new saves.
            int? keep = quiet && Detail is { IsCurrentVersion: false } shown && string.Equals(shown.Id, id, StringComparison.OrdinalIgnoreCase) ? shown.VersionNumber : null;
            Detail = d is null ? null : new DetailViewModel(d, keep, WorkerOf(d.Info.Ticket.Id));
            if (!quiet && Detail is { } v) StatusMessage = $"{v.Id}: {v.WorkText}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Steps the detail pane to an older (−1) or newer (+1) version of its ticket and says which (M.TIX-T137); false
    /// at either end.</summary>
    public bool StepVersion(int delta)
    {
        if (Detail is not { } d || !d.Step(delta)) return false;
        StatusMessage = $"{d.Id}: {d.VersionText}";
        Announcement = d.VersionAnnouncement;
        return true;
    }

    /// <summary>Shows <paramref name="id"/>: selects it in the current view if it's there, else in Tickets with filters cleared.</summary>
    public void GoTo(string? id)
    {
        if (id is null || snapshot is null) return;
        // Audit and Activity have no ticket list: a finding's or a change's ticket opens in Repos.
        if ((IsAuditView || IsActivityView) &&snapshot.Tickets.Any(t => t.Ticket.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) SelectedView = TicketsView;
        if (snapshot.Tickets.All(t => !t.Ticket.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            // A story opens in the Stories view, where it can be edited (M.TIX-T82); one that can't (no such row) shows its summary (M.TIX-T81).
            if (!OpenStory(id)) _ = ShowStoryAsync(id);
            return;
        }
        if (IsBoardView && BoardColumns.SelectMany(c => c.Rows).FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } card)
        {
            SelectedBoardRow = card;
            FocusRow?.Invoke(card);
            return;
        }
        if (IsStoriesView) SelectedView = TicketsView; // Stories lists no tickets: Repos does
        if (IsBoardView) SelectedView = TicketsView; // not on the board (filtered, or an older closed ticket): Repos lists it
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
        search = ""; repoFilter = AllRepos; typeFilter = AnyType; approvalFilter = AnyApproval; workFilter = AnyWork; labelFilter = AnyLabel; statusFilter = AnyStatus; showArchived = true;
        query = ""; queryError = ""; parsedQuery = TicketQuery.All; selectedSavedFilter = NoSavedFilter;
        foreach (var p in new[] { nameof(Search), nameof(RepoFilter), nameof(TypeFilter), nameof(ApprovalFilter), nameof(WorkFilter), nameof(LabelFilter), nameof(StatusFilter), nameof(ShowArchived),
                     nameof(Query), nameof(QueryError), nameof(SelectedSavedFilter) }) Raise(p);
    }

    /// <summary>
    /// Opens <paramref name="id"/> as <c>--ticket</c> asks (at start, or handed over by a second start): reads the database
    /// afresh (a link can name a ticket filed seconds ago), selects the ticket and opens its detail pane, even when it was
    /// already selected with the pane closed. False, with the reason shown, when there's no such ticket.
    /// </summary>
    public async Task<bool> OpenTicketAsync(string id)
    {
        // Fast: a ticket the window already lists opens straight away (its detail is read afresh below, and the file
        // watcher keeps the lists current); only one it doesn't know yet costs a full read first.
        TicketInfo? Known() => snapshot?.Tickets.FirstOrDefault(t => t.Ticket.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        var known = Known();
        if (known is null)
        {
            await RefreshAsync();
            known = Known();
        }
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

    /// <summary>Runs a database change off the UI thread, then refreshes and announces <paramref name="done"/> (or, when given,
    /// what <paramref name="doneText"/> says once the change is made: a bulk action's tally).</summary>
    private async Task<bool> Do(Action work, string done, Func<string>? doneText = null)
    {
        if (Detail is { } d) d.Error = "";
        Busy = true;
        try
        {
            await Task.Run(work);
            if (doneText is not null) done = doneText();
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
        row is null ? Task.CompletedTask : Undoable(() => store.Approve(row.Id), $"{row.Id} approved.");

    public Task UnapproveAsync(TicketRowViewModel? row) =>
        row is null ? Task.CompletedTask : Undoable(() => store.Unapprove(row.Id), $"{row.Id} back to proposed.");

    public async Task RejectAsync(TicketRowViewModel? row)
    {
        if (row is null) return;
        var answer = Dialogs.Ask(new Prompt($"Reject {row.Id}", $"{row.Summary}\n\nRejecting closes it as wont-do. Why?", "Reason", Ok: "Reject"));
        if (answer is null) return;
        ownCloses.Add(row.Id); // the feed shows it; no toast for this window's own close
        await Undoable(() => store.Reject(row.Id, answer.Text), $"{row.Id} rejected and closed as wont-do.");
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
        if (await Undoable(() => store.Move(row.Id, order), $"{row.Id} moved to position {index + 1} of {order.Count}."))
        {
            SelectedQueueRow = row;
            FocusRow?.Invoke(row);
        }
    }

    public async Task CloseTicketAsync(string status)
    {
        if (Detail is not { } d) return;
        var verb = status == TicketRules.WontDo ? "Won't do" : "Close";
        var answer = Dialogs.Ask(new Prompt($"{verb} {d.Id}", d.Summary,
            status == TicketRules.WontDo ? "Reason (becomes its ref)" : "Ref: test, commit or story (optional; keeps the current ref when empty)",
            TextRequired: status == TicketRules.WontDo, Initial: "", Ok: verb));
        if (answer is null) return;
        ownCloses.Add(d.Id); // the feed shows it; no toast for this window's own close
        await Undoable(() => store.Close(d.Id, status, answer.Text), $"{d.Id} {status}.");
    }

    /// <summary>Sets the ticket's Ref without closing it; an empty answer is refused inline, as the database refuses it.</summary>
    public async Task SetRefAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Set the ref of {d.Id}", d.Summary, "Ref: test, commit or story",
            TextRequired: true, Initial: d.Source.Info.Ticket.Ref is "—" ? "" : d.Source.Info.Ticket.Ref, Ok: "Set ref"));
        if (answer is null) return;
        await Do(() => store.SetRef(d.Id, answer.Text), $"{d.Id} ref set to {answer.Text.Trim()}.");
    }

    public async Task CorrectTypeAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Correct the type of {d.Id}", "Types are append-only; a correction is recorded in the ticket's history with your reason.",
            "Reason", ChoiceLabel: "Type", Choices: TicketRules.Types, Choice: d.Type, Ok: "Correct"));
        if (answer?.Choice is not { } type) return;
        await Do(() => store.CorrectType(d.Id, type, answer.Text), $"{d.Id} is now a {type}.");
    }

    /// <summary>Rewords the summary (M.TIX-T73): the new wording, then why; both kept in its history.</summary>
    private async Task CorrectSummaryAsync()
    {
        if (Detail is not { } d) return;
        var current = d.Source.Info.Ticket.Summary;
        var wording = Dialogs.Ask(new Prompt($"Reword the summary of {d.Id}", "Summaries are append-only; a rewording is recorded in the ticket's history, the old wording kept.",
            "Summary (one line)", Initial: current, Ok: "Next"));
        if (wording is null || wording.Text == current) return;
        var reason = Dialogs.Ask(new Prompt($"Reword the summary of {d.Id}", $"From: {current}\nTo: {wording.Text}\n\nWhy?", "Reason", Ok: "Reword"));
        if (reason is null) return;
        await Do(() => store.CorrectSummary(d.Id, wording.Text, reason.Text), $"{d.Id} reworded.");
    }

    /// <summary>Sets, edits or (emptied) removes the markdown description (M.TIX-T73).</summary>
    private async Task EditDescriptionAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Description of {d.Id}", "Markdown, kept apart from the one-line summary. Leave it empty to remove it; every change is kept in the ticket's history.",
            "Description", TextRequired: false, Initial: d.Source.Description ?? "", Ok: "Save", Multiline: true));
        if (answer is null) return;
        await Do(() => store.SetDescription(d.Id, answer.Text), string.IsNullOrWhiteSpace(answer.Text) ? $"{d.Id}'s description removed." : $"{d.Id}'s description saved.");
    }

    public async Task AddLinkAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Link {d.Id}", $"{d.Id} [kind] the ticket or story you name. 'requires' means {d.Id} waits for it.",
            "Other ticket or story ID", ChoiceLabel: "Kind", Choices: TicketsDb.LinkKinds, Choice: "requires", Ok: "Link"));
        if (answer?.Choice is not { } kind) return;
        var other = answer.Text.Trim().ToUpperInvariant();
        await Undoable(() => store.AddLink(d.Id, other, kind), $"{d.Id} {kind} {other}.");
    }

    public async Task RemoveLinkAsync(LinkLine? line)
    {
        if (line is null) return;
        await Undoable(() => store.RemoveLink(line.Link.From, line.Link.To, line.Link.Kind), $"Removed: {line.Link.From} {line.Link.Kind} {line.Link.To}.");
    }

    public async Task NewTicketAsync()
    {
        if (snapshot is null) return;
        var repo = RepoFilter != AllRepos ? RepoFilter : settings.LastRepo;
        if (Dialogs.NewTicket(new NewTicketRequest(snapshot.Repos, repo, settings.LastType, SimilarOpenTickets)) is not { } n) return;
        settings.LastRepo = n.Repo;
        settings.LastType = n.Type;
        settings.Save();
        var similar = SimilarOpenTickets(n.Repo, n.Summary);
        string? id = null;
        // An untouched template isn't a description (M.TIX-T88).
        if (await Do(() => id = store.Add(n.Repo, n.Type, n.Summary, n.Ref, TicketTemplates.Keep(n.Description)).Id, $"Filed {n.Repo} ticket.") && id is not null)
        {
            GoTo(id);
            // Selecting the row starts a detail load that reports the ticket's readiness in the status bar; a quiet load
            // supersedes it, so "Filed" is what's shown and announced.
            await ShowDetailAsync(id, quiet: true);
            StatusMessage = Announcement = similar.Count == 0 ? $"Filed {id}."
                : $"Filed {id}; it reads like {string.Join(", ", similar.Select(s => s.Ticket.Id))}.";
        }
    }

    /// <summary>The repo's open tickets whose summary reads like <paramref name="summary"/> (M.TIX-T88), most alike first.</summary>
    public IReadOnlyList<SimilarTicket> SimilarOpenTickets(string repo, string summary) =>
        snapshot is null ? [] : TicketSimilarity.Similar(summary,
            snapshot.Tickets.Where(t => t.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).Select(t => (t.Repo, t.Ticket)));

    public async Task SyncAsync()
    {
        Busy = true;
        StatusMessage = "Syncing the ticket database…";
        try
        {
            var (code, output) = await store.SyncAsync();
            var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            // The sync renders, commits and pushes: show the lists and the repos' git state as they now are, then its result
            // (a refresh rewrites the status line, so the result goes last).
            await RefreshAsync();
            await RefreshGitAsync(force: true);
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
