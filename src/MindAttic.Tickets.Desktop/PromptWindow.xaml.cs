using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using MindAttic.Tickets.Desktop.ViewModels;

namespace MindAttic.Tickets.Desktop;

/// <summary>A small question: an optional choice and an optional line of text, with the error said beside it (3.3.1).</summary>
public partial class PromptWindow : Window
{
    private readonly Prompt prompt;

    public PromptWindow(Prompt prompt)
    {
        InitializeComponent();
        this.prompt = prompt;
        Title = prompt.Title;
        Heading.Text = prompt.Title;
        Message.Text = prompt.Message;
        OkButton.Content = prompt.Ok;

        ChoicePanel.Visibility = prompt.Choices is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        ChoiceLabel.Content = prompt.ChoiceLabel ?? "Choice";
        AutomationProperties.SetName(Choice, prompt.ChoiceLabel ?? "Choice");
        Choice.ItemsSource = prompt.Choices;
        Choice.SelectedItem = prompt.Choice ?? prompt.Choices?.FirstOrDefault();

        TextPanel.Visibility = prompt.TextLabel is null ? Visibility.Collapsed : Visibility.Visible;
        TextLabel.Content = prompt.TextLabel;
        AutomationProperties.SetName(Answer, prompt.TextLabel ?? "");
        AutomationProperties.SetIsRequiredForForm(Answer, prompt.TextRequired);
        Answer.Text = prompt.Initial ?? "";
        if (prompt.Multiline)
        {
            Answer.AcceptsReturn = true;
            Answer.MinHeight = 180;
            Answer.MaxHeight = 420;
            Answer.VerticalContentAlignment = VerticalAlignment.Top;
            Answer.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        }

        Loaded += (_, _) => (prompt.TextLabel is not null ? Answer : prompt.Choices is { Count: > 0 } ? Choice : (UIElement)OkButton).Focus();
    }

    public PromptAnswer? Result { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (prompt.TextLabel is not null && prompt.TextRequired && string.IsNullOrWhiteSpace(Answer.Text))
        {
            Error.Text = $"{prompt.TextLabel} is required.";
            Error.Visibility = Visibility.Visible;
            (UIElementAutomationPeer.FromElement(Error) ?? UIElementAutomationPeer.CreatePeerForElement(Error))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            Answer.Focus();
            return;
        }
        // A multi-line answer keeps its indentation (markdown); the store trims what doesn't matter.
        Result = new PromptAnswer(Choice.SelectedItem as string, prompt.Multiline ? Answer.Text : Answer.Text.Trim());
        DialogResult = true;
    }
}
