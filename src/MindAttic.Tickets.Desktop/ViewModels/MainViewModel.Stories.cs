using System.Collections.ObjectModel;
using MindAttic.Components.Wpf.Mvvm;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The Stories view (M.TIX-T82): every repo's stories under their epics, filtered by repo, search and status, with an editor
/// for a story's statement, status, acceptance and evidence (Save / Revert), new stories and epics, renaming an epic, and the
/// tickets that implement a story (its <c>implements</c> links). A story ID clicked anywhere opens it here.
/// </summary>
public sealed partial class MainViewModel
{
    public const string StoriesView = "Stories";

    private string storyStatusFilter = AnyStatus;
    private bool groupByEpic;
    private StoryRowViewModel? selectedStoryRow;
    private StoryEditorViewModel? storyEditor;
    private int storyRequest;

    /// <summary>Editors with unsaved edits, by story key: kept while another story is shown, so switching loses nothing.</summary>
    private readonly Dictionary<string, StoryEditorViewModel> drafts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsStoriesView => SelectedView == StoriesView;

    /// <summary>The ticket detail pane shows beside every view but Stories, which has its own editor there.</summary>
    public bool ShowsDetailPane => IsDetailOpen && !IsStoriesView;

    public ObservableCollection<StoryRowViewModel> StoryRows { get; } = [];
    public IReadOnlyList<string> StoryStatusChoices { get; } = [AnyStatus, .. StoryStatus.All];

    public string StoryStatusFilter { get => storyStatusFilter; set { if (Set(ref storyStatusFilter, value ?? AnyStatus)) ApplyFilters(); } }

    /// <summary>Stories listed under a heading per epic (they're always in epic order). Remembered between runs.</summary>
    public bool GroupByEpic
    {
        get => groupByEpic;
        set
        {
            if (!Set(ref groupByEpic, value)) return;
            settings.GroupStoriesByEpic = value;
            settings.Save();
        }
    }

    public StoryRowViewModel? SelectedStoryRow
    {
        get => selectedStoryRow;
        set { if (Set(ref selectedStoryRow, value) && value is not null) _ = LoadStoryAsync(value.Repo, value.Id); }
    }

    public StoryEditorViewModel? StoryEditor
    {
        get => storyEditor;
        private set
        {
            if (ReferenceEquals(storyEditor, value)) return;
            if (storyEditor is { } old) old.PropertyChanged -= OnEditorChanged;
            storyEditor = value;
            if (value is not null) value.PropertyChanged += OnEditorChanged;
            Raise();
            Raise(nameof(IsStoryEditorOpen));
            Raise(nameof(ShowsStoryEditor));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsStoryEditorOpen => StoryEditor is not null;

    /// <summary>The story editor shows beside the Stories list, where the ticket detail pane shows in the other views.</summary>
    public bool ShowsStoryEditor => IsStoriesView && StoryEditor is not null;

    /// <summary>Raised when a story row should take keyboard focus (opened from elsewhere, or just added).</summary>
    public event Action<StoryRowViewModel>? FocusStory;

    public ICommand SaveStoryCommand { get; private set; } = null!;
    public ICommand RevertStoryCommand { get; private set; } = null!;
    public ICommand NewStoryCommand { get; private set; } = null!;
    public ICommand NewEpicCommand { get; private set; } = null!;
    public ICommand RenameEpicCommand { get; private set; } = null!;
    public ICommand AddImplementerCommand { get; private set; } = null!;
    public ICommand RemoveImplementerCommand { get; private set; } = null!;
    public ICommand CloseStoryCommand { get; private set; } = null!;

    private void InitStories()
    {
        groupByEpic = settings.GroupStoriesByEpic;
        SaveStoryCommand = new RelayCommand(async () => await SaveStoryAsync(), () => StoryEditor is { IsDirty: true } && !Busy);
        RevertStoryCommand = new RelayCommand(RevertStory, () => StoryEditor is { IsDirty: true });
        NewStoryCommand = new RelayCommand(async p => await NewStoryAsync(p as EpicGroup), _ => snapshot is not null);
        NewEpicCommand = new RelayCommand(async () => await NewEpicAsync(), () => snapshot is not null);
        RenameEpicCommand = new RelayCommand(async p => await RenameEpicAsync(p as EpicGroup), p => (p as EpicGroup ?? StoryEditor?.Epic) is { IsEpic: true });
        AddImplementerCommand = new RelayCommand(async () => await AddImplementerAsync(), () => StoryEditor is not null);
        RemoveImplementerCommand = new RelayCommand(async p => await RemoveImplementerAsync(p as ImplementerLine), p => p is ImplementerLine);
        CloseStoryCommand = new RelayCommand(() => { SelectedStoryRow = null; StoryEditor = null; }, () => StoryEditor is not null);
    }

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StoryEditorViewModel.IsDirty) || sender is not StoryEditorViewModel ed) return;
        if (ed.IsDirty) drafts[ed.Key] = ed; else drafts.Remove(ed.Key);
        if (StoryRows.FirstOrDefault(r => r.Key == ed.Key) is { } row) row.IsEdited = ed.IsDirty;
        CommandManager.InvalidateRequerySuggested();
    }

    // ── The list ────────────────────────────────────────────────────────────

    private EpicGroup GroupOf(Snapshot s, StoredStory story) =>
        story.Epic is { Length: > 0 } key && s.Epics.FirstOrDefault(e => e.Repo.Equals(story.Repo, StringComparison.OrdinalIgnoreCase) && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } e
            ? new EpicGroup(e.Repo, e.Key, e.Title, e.Id)
            : new EpicGroup(story.Repo, "", "", "");

    private IReadOnlyList<string> ImplementersOf(Snapshot s, string storyId) => s.Implementers.TryGetValue(storyId, out var ids) ? ids : [];

    private void ApplyStories(Snapshot s)
    {
        // Each repo's epics in file order, stories outside an epic after them, each epic's stories in file order.
        var epicRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in s.Epics) epicRank.TryAdd(StoryRowViewModel.KeyOf(e.Repo, e.Key), epicRank.Count);
        var fileOrder = new Dictionary<StoredStory, int>();
        for (var i = 0; i < s.Stories.Count; i++) fileOrder[s.Stories[i]] = i;

        bool Match(StoredStory st)
        {
            if (RepoFilter != AllRepos && !st.Repo.Equals(RepoFilter, StringComparison.OrdinalIgnoreCase)) return false;
            if (StoryStatusFilter != AnyStatus && st.Status != StoryStatusFilter) return false;
            if (Search.Length == 0) return true;
            var g = GroupOf(s, st);
            return st.Id.Contains(Search, StringComparison.OrdinalIgnoreCase) || st.Text.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                   st.Repo.Contains(Search, StringComparison.OrdinalIgnoreCase) || g.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                   ImplementersOf(s, st.Id).Any(t => t.Contains(Search, StringComparison.OrdinalIgnoreCase));
        }

        var shown = s.Stories.Where(Match)
            .OrderBy(st => st.Repo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(st => st.Epic is { } k && epicRank.TryGetValue(StoryRowViewModel.KeyOf(st.Repo, k), out var r) ? r : int.MaxValue)
            .ThenBy(st => fileOrder[st]);
        StoryRows.SyncBy(shown, st => StoryRowViewModel.KeyOf(st.Repo, st.Id), r => r.Key,
            st => new StoryRowViewModel(st, GroupOf(s, st), ImplementersOf(s, st.Id)) { IsEdited = drafts.ContainsKey(StoryRowViewModel.KeyOf(st.Repo, st.Id)) },
            (row, st) => { row.Update(st, GroupOf(s, st), ImplementersOf(s, st.Id)); row.IsEdited = drafts.ContainsKey(row.Key); });
        if (selectedStoryRow is { } sel && !StoryRows.Contains(sel)) { selectedStoryRow = null; Raise(nameof(SelectedStoryRow)); }
    }

    /// <summary>Clears what hides stories (search, repo, story status), without re-filtering (the caller does).</summary>
    private void ClearStoryFilters()
    {
        search = ""; repoFilter = AllRepos; storyStatusFilter = AnyStatus;
        foreach (var p in new[] { nameof(Search), nameof(RepoFilter), nameof(StoryStatusFilter) }) Raise(p);
    }

    /// <summary>The story <paramref name="id"/> (of <paramref name="repo"/> when given), or null when there's none.</summary>
    private StoredStory? FindStory(string id, string? repo = null) =>
        snapshot?.Stories.FirstOrDefault(st => st.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                              (repo is null || st.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Opens story <paramref name="id"/> in the Stories view (its filters cleared when they hide it). False when there's no such story.</summary>
    public bool OpenStory(string id, string? repo = null)
    {
        if (FindStory(id, repo) is not { } story) return false;
        SelectedView = StoriesView;
        var key = StoryRowViewModel.KeyOf(story.Repo, story.Id);
        var row = StoryRows.FirstOrDefault(r => r.Key == key);
        if (row is null)
        {
            ClearStoryFilters();
            ApplyFilters();
            row = StoryRows.FirstOrDefault(r => r.Key == key);
        }
        if (row is null) return false;
        SelectedStoryRow = row;
        FocusStory?.Invoke(row);
        return true;
    }

    // ── The editor ──────────────────────────────────────────────────────────

    /// <summary>
    /// Shows story <paramref name="id"/> of <paramref name="repo"/> in the editor, read afresh with its links. Unsaved edits to it
    /// (kept while another story was shown) are picked up again; a refresh (<paramref name="quiet"/>) follows changes made
    /// elsewhere only when there are no edits to lose.
    /// </summary>
    public async Task LoadStoryAsync(string repo, string id, bool quiet = false)
    {
        var request = ++storyRequest;
        try
        {
            var d = await Task.Run(() => store.StoryIn(repo, id));
            if (request != storyRequest) return;
            if (d is null)
            {
                if (!quiet) StatusMessage = $"{id} isn't a story of {repo} any more.";
                StoryEditor = null;
                return;
            }
            var key = StoryRowViewModel.KeyOf(d.Story.Repo, d.Story.Id);
            var editor = StoryEditor is { } shown && shown.Key == key ? shown : drafts.TryGetValue(key, out var draft) ? draft : null;
            if (editor is null)
            {
                editor = new StoryEditorViewModel(d.Story);
                editor.Revert();
            }
            else if (!editor.IsDirty) editor.Reset(d.Story);
            editor.SetLinks(snapshot is { } s ? GroupOf(s, d.Story) : null, Implementers(d), OtherLinks(d));
            StoryEditor = editor;
            if (!quiet) StatusMessage = $"{d.Story.Id}: {StoryStatus.Text(d.Story.Status)}, {editor.Implementers.Count} implementing ticket(s).";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = ex.Message;
        }
    }

    private IEnumerable<ImplementerLine> Implementers(StoryDetail d) =>
        d.Links.Where(l => l.Kind == "implements" && l.To.Equals(d.Story.Id, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.From).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => snapshot?.Tickets.FirstOrDefault(x => x.Ticket.Id.Equals(t, StringComparison.OrdinalIgnoreCase)) is { } info
                ? new ImplementerLine(info.Ticket.Id, d.Story.Id, info.Ticket.Summary, info.Ticket.Status)
                : new ImplementerLine(t, d.Story.Id, "", "not a ticket here"));

    private static IEnumerable<LinkLine> OtherLinks(StoryDetail d) =>
        d.Links.Where(l => !(l.Kind == "implements" && l.To.Equals(d.Story.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(l => l.From.Equals(d.Story.Id, StringComparison.OrdinalIgnoreCase)
                ? new LinkLine(DetailViewModel.Outgoing(l.Kind), l.To, l)
                : new LinkLine(DetailViewModel.Incoming(l.Kind), l.From, l));

    /// <summary>
    /// Saves the editor's story: its statement, acceptance, evidence and status in one write, the repo's USER_STORIES.md
    /// rendered to match. Refused, with the edits kept, when the story was changed elsewhere since it was opened.
    /// </summary>
    public async Task<bool> SaveStoryAsync()
    {
        if (StoryEditor is not { IsDirty: true } ed) return false;
        ed.Error = "";
        StoryDetail? now;
        try { now = await Task.Run(() => store.StoryIn(ed.Repo, ed.Id)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException) { now = null; ed.Error = ex.Message; }
        if (now is null)
        {
            if (ed.Error.Length == 0) ed.Error = $"{ed.Id} isn't a story of {ed.Repo} any more.";
            StatusMessage = ed.Error;
            Announcement = "Error: " + ed.Error;
            return false;
        }
        if (!ed.IsUnchangedSince(now.Story))
        {
            ed.Error = $"{ed.Id} was changed elsewhere since you opened it. Revert to see the new text, then edit again.";
            StatusMessage = ed.Error;
            Announcement = "Error: " + ed.Error;
            return false;
        }
        var (statement, status, acceptance, evidence) = (ed.Statement, ed.Status.Length == 0 ? null : ed.Status, ed.Acceptance, ed.Evidence);
        if (!await Do(() => store.UpdateStory(ed.Repo, ed.Id, statement, status, acceptance, evidence), $"{ed.Id} saved."))
        {
            ed.Error = StatusMessage;
            return false;
        }
        if (await Task.Run(() => store.StoryIn(ed.Repo, ed.Id)) is { } saved) ed.Reset(saved.Story);
        StatusMessage = Announcement = $"{ed.Id} saved.";
        return true;
    }

    public void RevertStory()
    {
        if (StoryEditor is not { } ed) return;
        ed.Revert();
        StatusMessage = Announcement = $"{ed.Id}: edits reverted.";
    }

    /// <summary>The repo a new story or epic goes in: <paramref name="group"/>'s, the story shown, the repo filter, else asked.</summary>
    private string? RepoForNew(EpicGroup? group, string what)
    {
        var repo = group?.Repo ?? StoryEditor?.Repo ?? (RepoFilter != AllRepos ? RepoFilter : null);
        if (repo is not null || snapshot is null) return repo;
        var answer = Dialogs.Ask(new Prompt(what, "Which repo?", TextRequired: false, ChoiceLabel: "Repo", Choices: snapshot.Repos, Choice: settings.LastRepo, Ok: "Next"));
        return answer?.Choice;
    }

    /// <summary>Adds a story (planned) under an epic of the repo; the new story opens in the editor.</summary>
    public async Task NewStoryAsync(EpicGroup? group = null)
    {
        if (snapshot is null || RepoForNew(group, "New story") is not { } repo) return;
        var epics = snapshot.Epics.Where(e => e.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (epics.Count == 0)
        {
            StatusMessage = Announcement = $"{repo} has no epics yet: add one with New epic first.";
            return;
        }
        static string Label(StoredEpic e) => $"{e.Key} — {e.Title}";
        var current = group is { IsEpic: true } ? group.Key : StoryEditor?.Epic is { IsEpic: true } shown && shown.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) ? shown.Key : null;
        var answer = Dialogs.Ask(new Prompt($"New story in {repo}", "It starts as planned; set its status, acceptance and evidence in the editor.",
            "Story (As a …, I can …)", ChoiceLabel: "Epic", Choices: [.. epics.Select(Label)],
            Choice: epics.FirstOrDefault(e => e.Key.Equals(current, StringComparison.OrdinalIgnoreCase)) is { } c ? Label(c) : null, Ok: "Add story"));
        if (answer is null || epics.FirstOrDefault(e => Label(e) == answer.Choice) is not { } epic) return;
        var id = "";
        if (!await Do(() => id = store.AddStory(epic.Repo, epic.Key, answer.Text, "planned"), $"Added a story to {epic.Repo} epic {epic.Key}.")) return;
        OpenStory(id, epic.Repo);
        StatusMessage = Announcement = $"Added {id}.";
    }

    private static readonly Regex EpicKey = new("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>Adds an epic (its key, then its title) to the repo.</summary>
    public async Task NewEpicAsync()
    {
        if (snapshot is null || RepoForNew(null, "New epic") is not { } repo) return;
        var key = Dialogs.Ask(new Prompt($"New epic in {repo}", "Its key names its stories (a key of B gives stories like CODE-US-B1).", "Key (letters and digits)", Ok: "Next"));
        if (key is null) return;
        var k = key.Text.Trim().ToUpperInvariant();
        if (!EpicKey.IsMatch(k))
        {
            StatusMessage = Announcement = $"'{key.Text}' can't be an epic key: use letters and digits only.";
            return;
        }
        var title = Dialogs.Ask(new Prompt($"New epic {k} in {repo}", "What the epic is about.", "Title", Ok: "Add epic"));
        if (title is null) return;
        await Do(() => store.AddEpic(repo, k, title.Text), $"Added epic {k} to {repo}.");
    }

    /// <summary>Renames <paramref name="group"/>'s epic, or the shown story's.</summary>
    public async Task RenameEpicAsync(EpicGroup? group = null)
    {
        if ((group ?? StoryEditor?.Epic) is not { IsEpic: true } epic) return;
        var answer = Dialogs.Ask(new Prompt($"Rename epic {epic.Key} of {epic.Repo}", "Its stories keep their IDs.", "Title", Initial: epic.Title, Ok: "Rename"));
        if (answer is null || answer.Text == epic.Title) return;
        if (await Do(() => store.EditEpic(epic.Repo, epic.Key, answer.Text), $"Epic {epic.Key} of {epic.Repo} renamed.") && StoryEditor is { } ed)
            await LoadStoryAsync(ed.Repo, ed.Id, quiet: true);
    }

    /// <summary>Links a ticket to the shown story as implementing it (<c>TICKET implements STORY</c>); Ctrl+Z undoes it.</summary>
    public async Task AddImplementerAsync()
    {
        if (StoryEditor is not { } ed || snapshot is null) return;
        var answer = Dialogs.Ask(new Prompt($"Implementing ticket for {ed.Id}", $"A ticket that implements {ed.Id}: the link reads 'TICKET implements {ed.Id}'.",
            "Ticket ID", Ok: "Link"));
        if (answer is null) return;
        var id = answer.Text.Trim();
        if (snapshot.Tickets.FirstOrDefault(t => t.Ticket.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } t)
        {
            ed.Error = $"{id} isn't a ticket.";
            StatusMessage = ed.Error;
            Announcement = "Error: " + ed.Error;
            return;
        }
        if (await Undoable(() => store.AddLink(t.Ticket.Id, ed.Id, "implements"), $"{t.Ticket.Id} implements {ed.Id}."))
            await LoadStoryAsync(ed.Repo, ed.Id, quiet: true);
    }

    public async Task RemoveImplementerAsync(ImplementerLine? line)
    {
        if (line is null) return;
        if (await Undoable(() => store.RemoveLink(line.TicketId, line.StoryId, "implements"), $"Removed: {line.TicketId} implements {line.StoryId}.") && StoryEditor is { } ed)
            await LoadStoryAsync(ed.Repo, ed.Id, quiet: true);
    }
}
