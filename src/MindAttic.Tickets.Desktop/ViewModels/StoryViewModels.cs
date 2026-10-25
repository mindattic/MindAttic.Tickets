using System.Collections.ObjectModel;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Stories;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The epic a story sits under, as the Stories view's group heading (M.TIX-T82). A record, so stories of one epic group
/// together; <see cref="Key"/> is empty for stories outside any epic.
/// </summary>
public sealed record EpicGroup(string Repo, string Key, string Title, string Id)
{
    public bool IsEpic => Key.Length > 0;
    public string Heading => IsEpic ? $"{Repo} · Epic {Key} — {Title}" : $"{Repo} · not under an epic";
    public string RenameName => $"Rename epic {Key} of {Repo}";
    public string NewStoryName => $"New story in epic {Key} of {Repo}";
    public override string ToString() => Heading;
}

/// <summary>One story in the Stories view's list.</summary>
public sealed class StoryRowViewModel : ObservableObject
{
    private string status = "", statement = "", implementersText = "";
    private EpicGroup group;
    private bool isEdited;

    public StoryRowViewModel(StoredStory s, EpicGroup group, IReadOnlyList<string> implementers)
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
    public EpicGroup Group { get => group; private set => Set(ref group, value); }

    /// <summary>"Implemented by AL-T1, AL-T4", or that nothing implements it yet.</summary>
    public string ImplementersText { get => implementersText; private set => Set(ref implementersText, value); }

    /// <summary>It has edits not yet saved (the editor keeps them while another story is shown).</summary>
    public bool IsEdited { get => isEdited; set { if (Set(ref isEdited, value)) Raise(nameof(SpokenName)); } }

    public string SpokenName => $"{Id}, {StoryStatus.Text(Status)}{(IsEdited ? ", unsaved edits" : "")}: {Statement}";

    public void Update(StoredStory s, EpicGroup g, IReadOnlyList<string> implementers)
    {
        Status = s.Status ?? "";
        Statement = s.Source == "table" ? s.Text : StoryDocument.SplitStory(s.Text).Statement;
        Group = g;
        ImplementersText = implementers.Count == 0 ? "No implementing tickets" : "Implemented by " + string.Join(", ", implementers);
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
    public EpicGroup? Epic { get; private set; }

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

    internal void SetLinks(EpicGroup? epic, IEnumerable<ImplementerLine> implementers, IEnumerable<LinkLine> links)
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
