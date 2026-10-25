using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>One line of the Activity feed: a change to a ticket, when, and (for a close) who was working it.</summary>
public sealed class ActivityRowViewModel(ActivityEvent e)
{
    public ActivityEvent Event { get; } = e;
    public long Seq => Event.Seq;
    public string TicketId => Event.TicketId;
    public string Repo => Event.Repo;
    public string Summary => Event.Summary;
    public string When { get; } = MainViewModel.LocalTime(e.At);
    public bool IsClose => Event.IsClose;

    /// <summary>What happened, and by whom when an agent was working it.</summary>
    public string Text { get; } = e.IsClose && e.WorkerName is { } who ? $"{e.Text} by {who}" : e.Text;

    /// <summary>The whole line as a screen reader says it (the row style's automation name).</summary>
    public string SpokenName =>$"{When}, {TicketId} in {Repo}: {Text}. {Summary}";
}

/// <summary>A toast: an agent closed a ticket (M.TIX-T84). Clicking it opens the ticket; it goes by itself after a few seconds.</summary>
public sealed record ActivityToast(string TicketId, string Title, string Body)
{
    public string Spoken => $"{Title}. {Body}";
}

/// <summary>
/// The Activity view (M.TIX-T84): every ticket change across the workspace's repos as it happens, newest first, read from the
/// ticket database's history (<c>ticket_events</c>) with every refresh, so agents' changes show within seconds. When a ticket an
/// agent was working is closed, a toast says so (and a screen reader hears it); changes this window made itself don't toast.
/// </summary>
public sealed partial class MainViewModel
{
    public const string ActivityView = "Activity";

    /// <summary>How many changes the feed keeps, newest first.</summary>
    public const int ActivityLimit = 300;

    /// <summary>How many toasts show at once; the oldest goes first.</summary>
    public const int MaxToasts = 4;

    private readonly List<ActivityEvent> activity = [];
    private readonly HashSet<string> ownCloses = new(StringComparer.OrdinalIgnoreCase);
    private long lastActivitySeq;
    private bool activityLoaded;
    private ActivityRowViewModel? selectedActivityRow;
    private ICommand? openToastCommand, dismissToastCommand;

    public bool IsActivityView => SelectedView == ActivityView;

    public ObservableCollection<ActivityRowViewModel> ActivityRows { get; } = [];

    /// <summary>Toasts showing now, oldest first. The window removes each after <see cref="ToastLifetime"/>.</summary>
    public ObservableCollection<ActivityToast> Toasts { get; } = [];

    public static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(10);

    public ActivityRowViewModel? SelectedActivityRow
    {
        get => selectedActivityRow;
        set { if (Set(ref selectedActivityRow, value) && value is not null) _ = ShowDetailAsync(value.TicketId); }
    }

    /// <summary>Opens a toast's ticket in the detail pane and dismisses it.</summary>
    public ICommand OpenToastCommand => openToastCommand ??= new RelayCommand(async p =>
    {
        if (p is not ActivityToast t) return;
        DismissToast(t);
        await ShowDetailAsync(t.TicketId);
    }, p => p is ActivityToast);

    public ICommand DismissToastCommand => dismissToastCommand ??= new RelayCommand(p => DismissToast(p as ActivityToast), p => p is ActivityToast);

    public void DismissToast(ActivityToast? toast)
    {
        if (toast is not null) Toasts.Remove(toast);
    }

    /// <summary>Reads what changed since the last read. The first read fills the feed and toasts nothing (it's history).</summary>
    private async Task RefreshActivityAsync()
    {
        IReadOnlyList<ActivityEvent> fresh;
        try
        {
            var after = lastActivitySeq;
            fresh = await Task.Run(() => store.Activity(after, activityLoaded ? int.MaxValue : ActivityLimit));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            TicketsLog.For("Activity").Warning(ex, "Couldn't read the activity feed from {Db}", store.DbPath);
            return;
        }
        // Refreshes overlap (the watcher, the poll, an action): only take what no earlier one already took.
        var added = fresh.Where(e => e.Seq > lastActivitySeq).OrderBy(e => e.Seq).ToList();
        var first = !activityLoaded;
        activityLoaded = true;
        if (added.Count == 0) return;
        lastActivitySeq = added[^1].Seq;
        activity.InsertRange(0, added.AsEnumerable().Reverse());
        if (activity.Count > ActivityLimit) activity.RemoveRange(ActivityLimit, activity.Count - ActivityLimit);
        if (!first)
            foreach (var e in added.Where(e => e.IsClose))
                if (!ownCloses.Remove(e.TicketId) && IsAgent(e.Worker)) Toast(e);
        ApplyActivity();
        if (IsActivityView) StatusMessage = ActivityStatus;
    }

    /// <summary>An agent, not a hand: a session recorded as working it that isn't the Board's "worked by hand" marker.</summary>
    private static bool IsAgent(string? worker) =>
        worker is { Length: > 0 } && !worker.Contains(Board.HandSession, StringComparison.OrdinalIgnoreCase);

    private void Toast(ActivityEvent e)
    {
        var verb = e.New == TicketRules.WontDo ? "closed as wont-do" : "closed";
        var toast = new ActivityToast(e.TicketId, $"{e.WorkerName} {verb} {e.TicketId}", $"{e.Repo}: {e.Summary}");
        while (Toasts.Count >= MaxToasts) Toasts.RemoveAt(0);
        Toasts.Add(toast);
        Announcement = toast.Spoken;
    }

    /// <summary>The feed as the repo filter and search narrow it.</summary>
    private void ApplyActivity()
    {
        bool Match(ActivityEvent e) =>
            (RepoFilter == AllRepos || e.Repo.Equals(RepoFilter, StringComparison.OrdinalIgnoreCase)) &&
            (Search.Length == 0 || e.TicketId.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             e.Summary.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Repo.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             e.Text.Contains(Search, StringComparison.OrdinalIgnoreCase) || (e.Worker?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
        ActivityRows.SyncBy(activity.Where(Match), e => e.Seq, r => r.Seq, e => new ActivityRowViewModel(e), (_, _) => { });
        if (selectedActivityRow is { } sel && !ActivityRows.Contains(sel)) selectedActivityRow = null;
    }

    private string ActivityStatus => $"Activity: {ActivityRows.Count} of the {activity.Count} most recent changes shown.";

    internal static string LocalTime(string at) =>
        DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : at;
}
