using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MindAttic.Tickets.Desktop.ViewModels;

namespace MindAttic.Tickets.Desktop;

/// <summary>
/// The command palette (Ctrl+K, M.TIX-T81): a search box over every command, ticket and story. Keyboard only works end to end
/// (2.1.1): typing filters, Up/Down choose, Enter runs, Esc closes; a screen reader hears how many results there are and which
/// one is selected as either changes, without focus leaving the box (4.1.3). <see cref="Result"/> is the entry chosen.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly CommandPaletteViewModel model;
    private string lastSummary = "";
    private bool announcePending;

    public CommandPaletteWindow(CommandPaletteViewModel model)
    {
        InitializeComponent();
        this.model = model;
        DataContext = model;
        model.PropertyChanged += OnModelChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            DialogResult = false;
        };
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
            ScheduleAnnounce();
        };
        Closed += (_, _) => model.PropertyChanged -= OnModelChanged;
    }

    public PaletteItem? Result { get; private set; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(CommandPaletteViewModel.SelectedSpoken) or nameof(CommandPaletteViewModel.Summary))) return;
        if (model.Selected is { } s) ResultList.ScrollIntoView(s);
        ScheduleAnnounce();
    }

    /// <summary>One announcement per change, however many properties it raised: the count when it changed, then the selection.</summary>
    private void ScheduleAnnounce()
    {
        if (announcePending) return;
        announcePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            announcePending = false;
            var text = model.Summary != lastSummary || model.Selected is null ? model.Summary : "";
            lastSummary = model.Summary;
            if (model.SelectedSpoken.Length > 0) text = (text + " " + model.SelectedSpoken).Trim();
            if (text.Length == 0) return;
            Announcer.Text = text;
            (UIElementAutomationPeer.FromElement(Announcer) ?? UIElementAutomationPeer.CreatePeerForElement(Announcer))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.Down: model.Move(+1); break;
            case Key.Up: model.Move(-1); break;
            case Key.PageDown: model.Move(+PageSize); break;
            case Key.PageUp: model.Move(-PageSize); break;
            case Key.Home when ctrl: model.MoveToEnd(last: false); break;
            case Key.End when ctrl: model.MoveToEnd(last: true); break;
            case Key.Enter: Choose(); break;
            default: return;
        }
        e.Handled = true;
    }

    private const int PageSize = 8;

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Choose();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(ResultList, d) is ListBoxItem) Choose();
    }

    private void Choose()
    {
        if (model.Selected is not { } item) return;
        Result = item;
        DialogResult = true;
    }
}
