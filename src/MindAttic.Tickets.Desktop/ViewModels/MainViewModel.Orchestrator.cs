using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Desktop.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// What the window shows of the Orchestrator beside the tickets (M.TIX-T135): each claimed ticket's live worker in the
/// Queue (a link that opens the Orchestrator on it), each repo's git state and a Start link on the Repos tab, and the
/// Orchestrator's audit findings (shared-file drift, missing shared files, duplicate code) on the Audit tab. All of it is
/// read from the Orchestrator's database read-only; nothing here writes it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How often the Repos tab re-reads every repo's git state by itself (F5 always does).</summary>
    public static readonly TimeSpan GitRefreshInterval = TimeSpan.FromMinutes(2);

    private OrchestratorState orchestratorState = null!;
    private OrchestratorSnapshot orchestrator = OrchestratorSnapshot.None(null);
    private readonly Dictionary<string, RepoHeaderViewModel> repoHeaders = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, RepoGitState> gitStates = new Dictionary<string, RepoGitState>(StringComparer.OrdinalIgnoreCase);
    private DateTime gitReadAt = DateTime.MinValue;
    private Task? gitRead;
    private string auditSummary = "", orchestratorNote = "";

    /// <summary>Starts the Orchestrator with the given arguments (the Start link's); the reason it couldn't, or null. Tests replace it.</summary>
    public Func<IReadOnlyList<string>, string?> LaunchOrchestrator { get; set; } = args => OrchestratorLink.Launch(args);

    /// <summary>Reads every repo's git state; tests replace it (no git in a temporary workspace).</summary>
    public Func<string, RepoGitState> ReadGit { get; set; } = RepoGitState.Read;

    public ICommand ShowWorkerCommand { get; private set; } = null!;
    public ICommand StartCliCommand { get; private set; } = null!;

    public bool IsAuditView => SelectedView == AuditView;

    /// <summary>The ticket filters (type, approval, work) apply to Repos, Queue and Board, not Audit or Activity.</summary>
    public bool ShowsTicketFilters => !IsAuditView && !IsActivityView;

    public ObservableCollection<DriftFinding> DriftRows { get; } = [];
    public ObservableCollection<MissingFinding> MissingRows { get; } = [];
    public ObservableCollection<DuplicateGroup> DuplicateRows { get; } = [];

    /// <summary>When the audit last ran and what it scanned, or why there are no findings.</summary>
    public string AuditSummary { get => auditSummary; private set => Set(ref auditSummary, value); }

    /// <summary>Why the Orchestrator's state couldn't be read, empty when it could.</summary>
    public string OrchestratorNote { get => orchestratorNote; private set => Set(ref orchestratorNote, value); }

    public string OrchestratorDbPath => orchestratorState.DbPath;

    /// <summary>The worker on <paramref name="ticketId"/>, from the last read of the Orchestrator's state.</summary>
    public Worker? WorkerOf(string ticketId) => orchestrator.Workers.TryGetValue(ticketId, out var w) ? w : null;

    private void InitOrchestrator(OrchestratorState? state)
    {
        orchestratorState = state ?? OrchestratorState.For(store.Root);
        ShowWorkerCommand = new RelayCommand(p => ShowWorker(p), p => WorkerFrom(p) is not null);
        StartCliCommand = new RelayCommand(p => StartCli(p), p => RepoFrom(p) is not null);
    }

    private Worker? WorkerFrom(object? p) => p switch
    {
        Worker w => w,
        TicketRowViewModel r => r.Worker,
        string id => WorkerOf(id),
        _ => null,
    };

    private static string? RepoFrom(object? p) => p switch
    {
        RepoHeaderViewModel h => h.Name,
        string s when s.Length > 0 => s,
        _ => null,
    };

    /// <summary>Opens the Orchestrator on a ticket's live work (<c>--show &lt;ticket&gt;</c>, through <see cref="OpenOrchestrator"/>).</summary>
    public void ShowWorker(object? p)
    {
        if (WorkerFrom(p) is not { } w) return;
        var failed = OpenOrchestrator(w.ShowTarget);
        StatusMessage = Announcement = failed ?? $"Opened the Orchestrator on {w.TicketId}.";
    }

    /// <summary>Opens the Orchestrator to start a CLI in a repo (<c>--start &lt;repo&gt;</c>).</summary>
    public void StartCli(object? p)
    {
        if (RepoFrom(p) is not { } repo) return;
        var failed = LaunchOrchestrator(OrchestratorLink.StartArguments(repo));
        StatusMessage = Announcement = failed ?? $"Opened the Orchestrator to start a CLI in {repo}.";
    }

    private async Task LoadOrchestratorAsync()
    {
        orchestrator = await Task.Run(orchestratorState.Load);
        OrchestratorNote = orchestrator.Error ?? "";
    }

    private RepoHeaderViewModel HeaderOf(string repo)
    {
        if (!repoHeaders.TryGetValue(repo, out var h))
        {
            repoHeaders[repo] = h = new RepoHeaderViewModel(repo);
            h.SetGit(gitStates.TryGetValue(repo, out var g) ? g : null);
        }
        return h;
    }

    /// <summary>A new row, with its repo heading and worker set before it joins a list (the list groups by the heading).</summary>
    private TicketRowViewModel NewRow(TicketInfo t)
    {
        var row = TicketRowViewModel.From(t);
        row.RepoHeader = HeaderOf(t.Repo);
        row.Worker = WorkerOf(t.Ticket.Id);
        return row;
    }

    private void UpdateRow(TicketRowViewModel row, TicketInfo t)
    {
        row.Update(t);
        row.RepoHeader = HeaderOf(t.Repo);
        row.Worker = WorkerOf(t.Ticket.Id);
    }

    /// <summary>Each repo heading's counts, from every ticket (not just the ones the filters show).</summary>
    private void ApplyRepoHeaders(Snapshot s)
    {
        foreach (var g in s.Tickets.GroupBy(t => t.Repo, StringComparer.OrdinalIgnoreCase)) HeaderOf(g.Key).SetCounts(g);
    }

    /// <summary>The audit lists, narrowed by the repo filter and the search (repo, file or path).</summary>
    private void ApplyAudit()
    {
        var a = orchestrator.Audit;
        bool Repo(string repo) => RepoFilter == AllRepos || repo.Equals(RepoFilter, StringComparison.OrdinalIgnoreCase);
        bool Text(params string[] fields) => Search.Length == 0 || fields.Any(f => f.Contains(Search, StringComparison.OrdinalIgnoreCase));
        Replace(DriftRows, a.Drift.Where(d => Repo(d.Repo) && Text(d.Repo, d.File, d.Path)));
        Replace(MissingRows, a.Missing.Where(m => Repo(m.Repo) && Text(m.Repo, m.File)));
        Replace(DuplicateRows, a.Duplicates.Where(g => g.Files.Any(f => Repo(f.Repo)) && (Search.Length == 0 || g.Files.Any(f => Text(f.Repo, f.Path)) || Text(g.SuggestedHome))));
        AuditSummary = !a.HasRun
            ? orchestrator.Error ?? "The Orchestrator hasn't run its audit yet (orchestrator audit runs it)."
            : $"Last run {a.RanUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({a.Took.TotalSeconds:0}s): {a.ReposScanned} repos, {a.FilesScanned} files. " +
              $"{a.Drift.Count} drifted, {a.Missing.Count} missing, {a.Duplicates.Count} duplicate groups.";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var i in list) target.Add(i);
    }

    /// <summary>
    /// Re-reads every repo's git state off the UI thread (<paramref name="force"/>: now; else only when the Repos tab is shown
    /// and the last read is older than <see cref="GitRefreshInterval"/>), then updates the headings in place. A call while a
    /// read is under way waits for that one rather than starting another.
    /// </summary>
    public async Task RefreshGitAsync(bool force = false)
    {
        if (gitRead is { IsCompleted: false } running) { await running; return; }
        if (snapshot is not { } s) return;
        if (!force && (!IsTicketsView || DateTime.UtcNow - gitReadAt < GitRefreshInterval)) return;
        await (gitRead = ReadGitAsync(s));
    }

    private async Task ReadGitAsync(Snapshot s)
    {
        try
        {
            var root = store.Root;
            var ws = new Workspace(root);
            var repos = s.Repos.ToList();
            var read = ReadGit;
            gitStates = await Task.Run(() =>
            {
                var found = new System.Collections.Concurrent.ConcurrentDictionary<string, RepoGitState>(StringComparer.OrdinalIgnoreCase);
                var dirs = ws.Repos().ToDictionary(r => r.Name, r => r.Path, StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(repos, new ParallelOptions { MaxDegreeOfParallelism = 8 }, repo =>
                    found[repo] = read(dirs.TryGetValue(repo, out var d) ? d : Path.Combine(root, repo)));
                return (IReadOnlyDictionary<string, RepoGitState>)new Dictionary<string, RepoGitState>(found, StringComparer.OrdinalIgnoreCase);
            });
            gitReadAt = DateTime.UtcNow;
            foreach (var (name, h) in repoHeaders) h.SetGit(gitStates.TryGetValue(name, out var g) ? g : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            TicketsLog.For("Git").Warning(ex, "Couldn't read the repos' git state");
        }
    }
}
