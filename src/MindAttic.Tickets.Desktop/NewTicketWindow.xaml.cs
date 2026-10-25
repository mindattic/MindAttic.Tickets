using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop;

/// <summary>Files a new ticket; starts on the last repo and type used (3.3.7 Redundant Entry).</summary>
public partial class NewTicketWindow : Window
{
    public NewTicketWindow(IReadOnlyList<string> repos, string? repo, string type)
    {
        InitializeComponent();
        Repo.ItemsSource = repos;
        Repo.SelectedItem = repos.FirstOrDefault(r => r.Equals(repo, StringComparison.OrdinalIgnoreCase));
        Type.ItemsSource = TicketRules.Types;
        Type.SelectedItem = TicketRules.Types.Contains(type) ? type : "feature";
        Loaded += (_, _) => (Repo.SelectedItem is null ? Repo : (UIElement)Summary).Focus();
    }

    public NewTicket? Result { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var problem = Repo.SelectedItem is null ? (Repo, "Pick the repo the ticket belongs to.")
            : string.IsNullOrWhiteSpace(Summary.Text) ? (Summary, "A ticket needs a summary.")
            : Summary.Text.Contains('\n') ? (Summary, "Keep the summary to one line.")
            : ((UIElement?)null, (string?)null);
        if (problem.Item2 is { } message)
        {
            Error.Text = message;
            Error.Visibility = Visibility.Visible;
            (UIElementAutomationPeer.FromElement(Error) ?? UIElementAutomationPeer.CreatePeerForElement(Error))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            problem.Item1?.Focus();
            return;
        }
        Result = new NewTicket((string)Repo.SelectedItem!, (string)Type.SelectedItem, Summary.Text.Trim(), Ref.Text.Trim(), Description.Text);
        DialogResult = true;
    }
}
