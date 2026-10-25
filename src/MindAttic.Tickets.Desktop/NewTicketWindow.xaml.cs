using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop;

/// <summary>
/// Files a new ticket; starts on the last repo and type used (3.3.7 Redundant Entry). The description starts from the
/// type's template, swapped for the new type's while it's untouched, and the open tickets of the repo that read like the
/// summary are listed under it as it's typed (M.TIX-T88).
/// </summary>
public partial class NewTicketWindow : Window
{
    private readonly Func<string, string, IReadOnlyList<SimilarTicket>> similar;

    public NewTicketWindow(NewTicketRequest request)
    {
        InitializeComponent();
        similar = request.Similar;
        Repo.ItemsSource = request.Repos;
        Repo.SelectedItem = request.Repos.FirstOrDefault(r => r.Equals(request.Repo, StringComparison.OrdinalIgnoreCase));
        Type.ItemsSource = TicketRules.Types;
        Type.SelectedItem = TicketRules.Types.Contains(request.Type) ? request.Type : "feature";
        Description.Text = TicketTemplates.For((string)Type.SelectedItem);
        Type.SelectionChanged += (_, _) =>
        {
            if (TicketTemplates.IsBlank(Description.Text) && Type.SelectedItem is string type) Description.Text = TicketTemplates.For(type);
        };
        Summary.TextChanged += (_, _) => ShowDuplicates();
        Repo.SelectionChanged += (_, _) => ShowDuplicates();
        Loaded += (_, _) => (Repo.SelectedItem is null ? Repo : (UIElement)Summary).Focus();
    }

    public NewTicket? Result { get; private set; }

    private void ShowDuplicates()
    {
        IReadOnlyList<SimilarTicket> found = Repo.SelectedItem is string repo && !string.IsNullOrWhiteSpace(Summary.Text) ? similar(repo, Summary.Text) : [];
        var text = found.Count == 0 ? "" : "Reads like an open ticket already filed:\n" +
            string.Join("\n", found.Select(s => $"  {s.Ticket.Id} ({s.Score:P0} alike): {s.Ticket.Summary}"));
        if (text == Duplicates.Text) return;
        Duplicates.Text = text;
        Duplicates.Visibility = found.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (found.Count > 0)
            (UIElementAutomationPeer.FromElement(Duplicates) ?? UIElementAutomationPeer.CreatePeerForElement(Duplicates))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

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
        Result = new NewTicket((string)Repo.SelectedItem!, (string)Type.SelectedItem, Summary.Text.Trim(), Ref.Text.Trim(),
            TicketTemplates.Keep(Description.Text) ?? "");
        DialogResult = true;
    }
}
