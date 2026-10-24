using System.Windows.Input;
using MindAttic.Tickets.Data;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The ticket functions taken over from the Orchestrator (M.TIX-T134): assign a ticket to a member's login (or take it off
/// them), reserve tokens for it, and, for a ticket being worked, open the Orchestrator on its login, session or the ticket.
/// </summary>
public sealed partial class MainViewModel
{
    private ICommand? assignCommand, unassignCommand, reserveCommand, openInOrchestratorCommand;

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
