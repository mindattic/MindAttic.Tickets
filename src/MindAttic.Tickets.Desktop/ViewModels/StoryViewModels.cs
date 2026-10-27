using System.Collections.ObjectModel;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;
using MindAttic.Tickets.Stories;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The epic a story sits under, as the Stories view's group heading (M.TIX-T82, M.TIX-T183): stories of one epic group
/// together under it (<see cref="Key"/> is empty for stories outside any epic). One instance per epic is kept and updated
/// in place across refreshes (by <c>MainViewModel.Stories</c>'s cache, keyed by <see cref="GroupKey"/>) rather than
/// recreated, so <see cref="IsExpanded"/> survives a reload the way <c>StoryRowViewModel</c>'s own edited-state does.
/// </summary>
public sealed class EpicGroupViewModel : ObservableObject
{
    private string title;
    private bool isExpanded = true;
    private int openCount, closedCount, wontDoCount, blockedCount;
    private TicketInfo? nextUp;

    public EpicGroupViewModel(string repo, string key, string title, string id)
    {
        Repo = repo;
        Key = key;
        this.title = title;
        Id = id;
    }

    public string Repo { get; }
    public string Key { get; }
    public string Title { get => title; private set => Set(ref title, value); }
    public string Id { get; }
    public bool IsEpic => Key.Length > 0;
    public string GroupKey => $"{Repo}|{Key}".ToUpperInvariant();
    public string Heading => IsEpic ? $"{Repo} · Epic {Key} — {Title}" : $"{Repo} · not under an epic";
    public string RenameName => $"Rename epic {Key} of {Repo}";
    public string NewStoryName => $"New story in epic {Key} of {Repo}";

    /// <summary>Whether this epic's stories show under its header (M.TIX-T182). Toggled by <c>ToggleEpicCommand</c>,
    /// which also remembers it in <c>DesktopSettings.CollapsedEpics</c>.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set { if (Set(ref isExpanded, value)) { Raise(nameof(ToggleName)); Raise(nameof(ChevronGlyph)); } }
    }

    /// <summary>The expand/collapse toggle button's accessible name, e.g. "Collapse epic B of Alpha".</summary>
    public string ToggleName => $"{(IsExpanded ? "Collapse" : "Expand")} {(IsEpic ? $"epic {Key}" : "group")} of {Repo}";

    /// <summary>Segoe Fluent Icons/MDL2 Assets chevron: down while expanded, right while collapsed.</summary>
    public string ChevronGlyph => IsExpanded ? "" : "";

    /// <summary>Open/closed/wont-do counts across every ticket that implements any story of this epic (M.TIX-T183).</summary>
    public int OpenCount { get => openCount; private set => Set(ref openCount, value); }
    public int ClosedCount { get => closedCount; private set => Set(ref closedCount, value); }
    public int WontDoCount { get => wontDoCount; private set => Set(ref wontDoCount, value); }
    public int TotalCount => OpenCount + ClosedCount + WontDoCount;

    /// <summary>0 when nothing implements this epic yet, else the closed+wont-do share, for an MaProgressBar (M.TIX-T184).</summary>
    public double DoneFraction => TotalCount == 0 ? 0 : (double)(ClosedCount + WontDoCount) / TotalCount;
    public string RollupText => TotalCount == 0 ? "No implementing tickets yet" : $"{ClosedCount + WontDoCount}/{TotalCount} done";

    /// <summary>Open, approved-or-fix and unblocked implementing tickets waiting on another: shown as a count with the
    /// tickets named in its tooltip, standing in for a full execution-order graph (M.TIX-T186 scopes the rest).</summary>
    public int BlockedCount { get => blockedCount; private set => Set(ref blockedCount, value); }

    /// <summary>The highest-priority open, approved-or-fix, unblocked implementing ticket: what to work on next (M.TIX-T187).</summary>
    public TicketInfo? NextUp { get => nextUp; private set => Set(ref nextUp, value); }
    public string NextUpText => NextUp is { } t ? $"Next up: {t.Ticket.Id} — {Trim(t.Ticket.Summary)}" : "";

    private static string Trim(string s) => s.Length <= 70 ? s : s[..67] + "…";

    internal void Retitle(string newTitle) => Title = newTitle;

    /// <summary>Recomputes the rollup from every ticket that implements a story of this epic (duplicates across stories
    /// already collapsed by the caller).</summary>
    internal void UpdateCounts(IReadOnlyList<TicketInfo> implementers)
    {
        OpenCount = implementers.Count(t => t.Ticket.Status == TicketRules.Open);
        ClosedCount = implementers.Count(t => t.Ticket.Status == TicketRules.Closed);
        WontDoCount = implementers.Count(t => t.Ticket.Status == TicketRules.WontDo);
        BlockedCount = implementers.Count(t => t.Blocked);
        NextUp = implementers.Where(t => t.Queued).OrderByDescending(t => t.Priority).ThenBy(t => t.Estimate ?? long.MaxValue).FirstOrDefault();
        Raise(nameof(TotalCount));
        Raise(nameof(DoneFraction));
        Raise(nameof(RollupText));
        Raise(nameof(NextUpText));
    }

    public override string ToString() => Heading;
}

/// <summary>One story in the Stories view's list.</summary>
public sealed class StoryRowViewModel : ObservableObject
{
    private string status = "", statement = "", implementersText = "";
    private EpicGroupViewModel group;
    private bool isEdited;

    public StoryRowViewModel(StoredStory s, EpicGroupViewModel group, IReadOnlyList<ImplementerLine> implementers)
    {
        Repo = s.Repo;
        Id = s.Id;
        this.group = group;
        Update(s, group, implementers);
    }

    public static string KeyOf(string repo, string id) => $"{repo.ToUpperInvariant()}|{id.ToUpperInvariant()}";

    public string Key => KeyOf(Repo, Id);
    public string Repo { get; }
    public string Id { get; }
    public string Status { get => status; private set { if (Set(ref status, value)) Raise(nameof(StatusText)); } }

    /// <summary>The status in words, with its glyph.</summary>
    public string StatusText => StoryStatus.Text(Status);
    public string Statement { get => statement; private set => Set(ref statement, value); }
    public EpicGroupViewModel Group { get => group; private set => Set(ref group, value); }

    /// <summary>"Implemented by AL-T1, AL-T4", or that nothing implements it yet.</summary>
    public string ImplementersText { get => implementersText; private set => Set(ref implementersText, value); }

    /// <summary>The tickets that implement this story, each with its summary and status, as small clickable chips
    /// under the row instead of only the muted <see cref="ImplementersText"/> summary (M.TIX-T185).</summary>
    public IReadOnlyList<ImplementerLine> Implementers { get; private set; } = [];

    /// <summary>It has edits not yet saved (the editor keeps them while another story is shown).</summary>
    public bool IsEdited { get => isEdited; set { if (Set(ref isEdited, value)) Raise(nameof(SpokenName)); } }

    public string SpokenName => $"{Id}, {StoryStatus.Text(Status)}{(IsEdited ? ", unsaved edits" : "")}: {Statement}";

    public void Update(StoredStory s, EpicGroupViewModel g, IReadOnlyList<ImplementerLine> implementers)
    {
        Status = s.Status ?? "";
        Statement = s.Source == "table" ? s.Text : StoryDocument.SplitStory(s.Text).Statement;
        Group = g;
        Implementers = implementers;
        ImplementersText = implementers.Count == 0 ? "No implementing tickets" : "Implemented by " + string.Join(", ", implementers.Select(l => l.TicketId));
        Raise(nameof(Implementers));
        Raise(nameof(SpokenName));
    }
}

/// <summary>A story status in words, with its glyph.</summary>
public static class StoryStatus
{
    public static readonly IReadOnlyList<string> All = ["planned", "partial", "done"];

    public static string Text(string? status) => status switch
    {
        "done" => "✅ done",
        "partial" => "🟡 partial",
        "planned" => "⬜ planned",
        null or "" => "no status",
        _ => status,
    };
}

/// <summary>A ticket that implements the story (its <c>implements</c> link), with what it is.</summary>
public sealed record ImplementerLine(string TicketId, string StoryId, string Summary, string State)
{
    public string SpokenName => $"{TicketId}, {State}: {Summary}";
}

/// <summary>
/// The Stories view's editor (M.TIX-T82): a story's statement, status, acceptance and evidence, edited in place and written
/// with Save (or put back with Revert), and the tickets that implement it. A story table row can only change status.
/// </summary>
public sealed class StoryEditorViewModel : ObservableObject
{
    private string statement = "", acceptance = "", evidence = "", status = "", error = "";
    private StoredStory original;

    public StoryEditorViewModel(StoredStory story) => original = story;

    public string Repo => original.Repo;
    public string Id => original.Id;
    public string Key => StoryRowViewModel.KeyOf(Repo, Id);
    public StoredStory Original => original;

    /// <summary>A row of a story table: its text lives in table cells, so only its status is edited here.</summary>
    public bool IsTable => original.Source == "table";
    public bool CanEditText => !IsTable;

    public string EpicText { get; private set; } = "";
    public EpicGroupViewModel? Epic { get; private set; }

    public IReadOnlyList<string> StatusChoices => StoryStatus.All;

    public string Statement { get => statement; set { if (Set(ref statement, value ?? "")) Changed(); } }
    public string Acceptance { get => acceptance; set { if (Set(ref acceptance, value ?? "")) Changed(); } }
    public string Evidence { get => evidence; set { if (Set(ref evidence, value ?? "")) Changed(); } }
    public string Status { get => status; set { if (Set(ref status, value ?? "")) Changed(); } }

    public ObservableCollection<ImplementerLine> Implementers { get; } = [];
    public ObservableCollection<LinkLine> Links { get; } = [];

    public string Error { get => error; set => Set(ref error, value); }

    public bool IsDirty => Norm(Statement) != Norm(Base.Statement) || Norm(Acceptance) != Norm(Base.Acceptance) ||
                           Norm(Evidence) != Norm(Base.Evidence) || Status != (original.Status ?? "");

    public string DirtyText => IsDirty ? "Unsaved changes" : "Saved";

    private (string Statement, string? Acceptance, string? Evidence) Base =>
        IsTable ? (original.Text, original.Acceptance, original.Evidence) : StoryDocument.SplitStory(original.Text);

    /// <summary>Shows <paramref name="story"/> as saved, dropping any edits.</summary>
    public void Reset(StoredStory story)
    {
        original = story;
        Raise(nameof(Original));
        Revert();
    }

    /// <summary>Puts every field back as last saved.</summary>
    public void Revert()
    {
        var (st, ac, ev) = Base;
        statement = st;
        acceptance = ac ?? "";
        evidence = ev ?? "";
        status = original.Status ?? "";
        Error = "";
        foreach (var p in new[] { nameof(Statement), nameof(Acceptance), nameof(Evidence), nameof(Status), nameof(IsTable), nameof(CanEditText) }) Raise(p);
        Changed();
    }

    /// <summary>Whether <paramref name="now"/> (read afresh) is still the story these edits started from.</summary>
    public bool IsUnchangedSince(StoredStory now) => now.Text == original.Text && now.Status == original.Status;

    internal void SetLinks(EpicGroupViewModel? epic, IEnumerable<ImplementerLine> implementers, IEnumerable<LinkLine> links)
    {
        Epic = epic;
        EpicText = epic is { IsEpic: true } ? $"Epic {epic.Key} — {epic.Title}" : "Not under an epic";
        Raise(nameof(Epic));
        Raise(nameof(EpicText));
        Implementers.ReorderTo([.. implementers]);
        Links.ReorderTo([.. links]);
    }

    private void Changed()
    {
        Raise(nameof(IsDirty));
        Raise(nameof(DirtyText));
    }

    private static string Norm(string? text) => string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
