using System.Windows;
using MindAttic.Tickets.Desktop.ViewModels;

namespace MindAttic.Tickets.Desktop;

/// <summary>The real dialogs, owned by the main window (so they open centred on it and return focus to it).</summary>
public sealed class Dialogs(Window owner) : IDialogs
{
    public PromptAnswer? Ask(Prompt prompt)
    {
        var w = new PromptWindow(prompt) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    public NewTicket? NewTicket(IReadOnlyList<string> repos, string? repo, string type)
    {
        var w = new NewTicketWindow(repos, repo, type) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }
}
