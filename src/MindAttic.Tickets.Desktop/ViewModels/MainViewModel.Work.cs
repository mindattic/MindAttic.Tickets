using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The ticket functions taken over from the Orchestrator (M.TIX-T134): assign a ticket to a member's login (or take it off
/// them), reserve tokens for it, and, for a ticket being worked, open the Orchestrator on its login, session or the ticket.
/// </summary>
public sealed partial class MainViewModel
{
    private ICommand? assignCommand, unassignCommand, reserveCommand, estimateCommand, openInOrchestratorCommand, addNoteCommand;

    public ICommand EstimateCommand => estimateCommand ??= new RelayCommand(async () => await EstimateAsync(), () => Detail is not null);

    /// <summary>Appends a comment to the ticket (M.TIX-T72); notes are never changed or deleted.</summary>
    public ICommand AddNoteCommand => addNoteCommand ??= new RelayCommand(async () => await AddNoteAsync(), () => Detail is not null);

    private async Task AddNoteAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Add a note to {d.Id}", "Notes are kept forever: they can't be edited or deleted afterwards.",
            "Note (markdown)", Ok: "Add note"));
        if (answer is null || string.IsNullOrWhiteSpace(answer.Text)) return;
        await Do(() => store.AddNote(d.Id, answer.Text), $"Note added to {d.Id}.");
    }

    public ICommand AssignCommand => assignCommand ??= new RelayCommand(async () => await AssignAsync(), () => Detail is { IsOpen: true });
    public ICommand UnassignCommand => unassignCommand ??= new RelayCommand(async () => await UnassignAsync(), () => Detail is { IsAssigned: true });
    public ICommand ReserveCommand => reserveCommand ??= new RelayCommand(async () => await ReserveAsync(), () => Detail is not null);
    public ICommand OpenInOrchestratorCommand => openInOrchestratorCommand ??= new RelayCommand(p => OpenInOrchestrator(p as string), p => p is string);

    /// <summary>Starts the Orchestrator for its <c>--show</c>; replaced in tests.</summary>
    public Func<string, string?> OpenOrchestrator { get; set; } = target => OrchestratorLink.Open(target);

    private async Task AssignAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Assign {d.Id}", "The ticket is then dispatched only on this member's login, and waits for them while they're out of room.",
            "Member's login email", Initial: d.Work.Assignee ?? "", Ok: "Assign"));
        if (answer is null) return;
        var email = answer.Text.Trim();
        await Do(() => store.Assign(d.Id, email), $"{d.Id} assigned to {TicketsDb.NormalizeEmail(email) ?? email}.");
    }

    private async Task UnassignAsync()
    {
        if (Detail is not { } d) return;
        await Do(() => store.Unassign(d.Id), $"{d.Id} is no longer assigned: it's handed out like any unassigned ticket.");
    }

    private async Task ReserveAsync()
    {
        if (Detail is not { } d) return;
        var answer = Dialogs.Ask(new Prompt($"Reserve tokens for {d.Id}", "Set aside uncached tokens so background work can't spend them first; 0 releases them.",
            "Tokens (like 2M, 500k or 0)", Initial: d.Work.Reserved > 0 ? d.Work.Reserved.ToString(System.Globalization.CultureInfo.InvariantCulture) : "", Ok: "Reserve"));
        if (answer is null) return;
        if (!ParseTokens(answer.Text, out var tokens))
        {
            d.Error = "Tokens: a number like 2M, 500k or 0.";
            StatusMessage = d.Error;
            Announcement = "Error: " + d.Error;
            return;
        }
        await Do(() => store.Reserve(d.Id, tokens), tokens > 0 ? $"{d.Id} has {tokens:N0} tokens reserved." : $"{d.Id}'s reservation is released.");
    }

    /// <summary>Sets or clears (empty) the effort estimate (M.TIX-T75), which orders the queue smallest first within a priority.</summary>
    private async Task EstimateAsync()
    {
        if (Detail is not { } d) return;
        var had = d.Source.Info.Estimate;
        var answer = Dialogs.Ask(new Prompt($"Estimate {d.Id}", "The uncached tokens it should take. Within a priority the queue runs the smallest first; leave it empty to clear it.",
            "Tokens (like 300k or 1.2M; empty clears)", Initial: had is { } h ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) : "", Ok: "Estimate"));
        if (answer is null) return;
        long? tokens = null;
        if (answer.Text.Trim().Length > 0)
        {
            if (!ParseTokens(answer.Text, out var t) || t > TicketsDb.MaxEstimate)
            {
                d.Error = "Tokens: a number like 300k or 1.2M, or empty to clear.";
                StatusMessage = d.Error;
                Announcement = "Error: " + d.Error;
                return;
            }
            tokens = t;
        }
        await Do(() => store.SetEstimate(d.Id, tokens), tokens is { } n ? $"{d.Id} is estimated at {n:N0} tokens." : $"{d.Id}'s estimate is cleared.");
    }

    private ICommand? openCommitCommand;

    /// <summary>Opens one of the ticket's commits on GitHub (M.TIX-T85); the parameter is its <see cref="CommitLine"/>.</summary>
    public ICommand OpenCommitCommand => openCommitCommand ??= new RelayCommand(p => OpenCommit(p as CommitLine), p => p is CommitLine { HasUrl: true });

    /// <summary>Opens a URL in the default browser, returning why it couldn't (null when it did); replaced in tests.</summary>
    public Func<string, string?> OpenUrl { get; set; } = url =>
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Couldn't open {url}: {ex.Message}";
        }
    };

    private void OpenCommit(CommitLine? commit)
    {
        if (commit?.Url is not { } url) return;
        var why = OpenUrl(url);
        StatusMessage = why ?? $"Opened commit {commit.Hash} on GitHub.";
        Announcement = StatusMessage;
        if (why is not null && Detail is { } d) d.Error = why;
    }

    private void OpenInOrchestrator(string? target)
    {
        if (target is null) return;
        var why = OpenOrchestrator(target);
        StatusMessage = why ?? $"Opened the Orchestrator on {target}.";
        Announcement = StatusMessage;
        if (why is not null && Detail is { } d) d.Error = why;
    }

    /// <summary>20000000, 20M, 1.5B, 500k, 0.</summary>
    internal static bool ParseTokens(string text, out long value)
    {
        value = 0;
        var t = text.Trim().Replace(",", "").Replace("_", "");
        if (t.Length == 0) return false;
        var scale = char.ToUpperInvariant(t[^1]) switch { 'K' => 1e3, 'M' => 1e6, 'B' => 1e9, _ => 1 };
        if (scale != 1) t = t[..^1];
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0) return false;
        if (n * scale is > 0 and < 1 || n * scale > TicketsDb.MaxReserved) return false;
        value = (long)(n * scale);
        return true;
    }
}
