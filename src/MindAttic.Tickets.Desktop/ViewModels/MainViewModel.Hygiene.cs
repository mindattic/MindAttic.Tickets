using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The two M.TIX-T134 functions that had reached only the engine and the CLI (M.TIX-T138): ticket hygiene on the Audit tab
/// (closed fixes and features with no Ref, or that a later ticket says came back, as <c>tickets ticket hygiene</c>) and
/// GitHub intake (issues labelled orchestrator and CI failures on main, as <c>tickets ticket intake</c>), which shows its
/// dry run first and files only what it showed, and only on an explicit click.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How many would-be tickets the intake preview lists before it says how many more.</summary>
    public const int IntakePreviewLines = 25;

    private ICommand? intakeCommand;

    /// <summary>Closed tickets to double-check, narrowed by the repo filter and the search.</summary>
    public ObservableCollection<HygieneFinding> HygieneRows { get; } = [];

    public ICommand IntakeCommand => intakeCommand ??= new RelayCommand(async () => await IntakeAsync(), () => snapshot is not null && !Busy);

    /// <summary>The snapshot's hygiene findings (worked out off the UI thread with the read): the ones the repo filter and search let through.</summary>
    private void ApplyHygiene(Snapshot s)
    {
        bool Shown(HygieneFinding f) =>
            (RepoFilter == AllRepos || f.Repo.Equals(RepoFilter, StringComparison.OrdinalIgnoreCase)) &&
            (Search.Length == 0 || new[] { f.Repo, f.TicketId, f.Summary, f.Why }.Any(x => x.Contains(Search, StringComparison.OrdinalIgnoreCase)));
        Replace(HygieneRows, s.Hygiene.Where(Shown));
    }

    /// <summary>
    /// GitHub intake: asks GitHub (off the UI thread) what would be filed, shows that preview and files exactly those
    /// tickets only when the owner clicks File; cancelling, or nothing new, writes nothing.
    /// </summary>
    public async Task IntakeAsync()
    {
        IReadOnlyList<IntakePreview> preview;
        Busy = true;
        StatusMessage = Announcement = "Asking GitHub for issues labelled 'orchestrator' and CI failures on main…";
        try { preview = await Task.Run(store.PreviewIntake); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            TicketsLog.For("Intake").Warning(ex, "GitHub intake preview failed");
            StatusMessage = Announcement = $"Couldn't ask GitHub: {ex.Message}";
            return;
        }
        finally
        {
            Busy = false;
            CommandManager.InvalidateRequerySuggested();
        }

        if (preview.Count == 0)
        {
            StatusMessage = Announcement = store.IntakeError is { } why
                ? $"Nothing new found, but GitHub didn't answer every question: {why}"
                : "Nothing new on GitHub (issues labelled 'orchestrator', CI failures on main in the last day).";
            return;
        }
        var answer = Dialogs.Ask(new Prompt($"File {preview.Count} ticket(s) from GitHub?", IntakeText(preview), TextLabel: null,
            Ok: $"File {preview.Count}"));
        if (answer is null)
        {
            StatusMessage = Announcement = "Nothing filed from GitHub.";
            return;
        }
        var filed = 0;
        if (!await Do(() => filed = store.FileIntake(preview).Count, "Filed from GitHub.")) return;
        StatusMessage = Announcement = filed < preview.Count
            ? $"Filed {filed} of {preview.Count} ticket(s) from GitHub (the rest were filed meanwhile)."
            : $"Filed {filed} ticket(s) from GitHub.";
    }

    /// <summary>The preview the owner confirms: one line per would-be ticket (repo, type, summary).</summary>
    internal static string IntakeText(IReadOnlyList<IntakePreview> preview)
    {
        var text = new StringBuilder("This dry run found what no ticket mentions yet. Nothing is written to GitHub.\n");
        foreach (var p in preview.Take(IntakePreviewLines))
            text.Append('\n').Append(p.Repo).Append("  ·  ").Append(p.Item.Type).Append("  ·  ").Append(p.Item.Summary);
        if (preview.Count > IntakePreviewLines) text.Append("\n…and ").Append(preview.Count - IntakePreviewLines).Append(" more.");
        return text.ToString();
    }
}
