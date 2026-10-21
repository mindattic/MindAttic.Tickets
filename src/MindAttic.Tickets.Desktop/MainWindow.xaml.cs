using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MindAttic.Tickets.Desktop.ViewModels;

namespace MindAttic.Tickets.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly MainViewModel model;
    private readonly DispatcherTimer poll, settle;
    private FileSystemWatcher? watcher;

    /// <param name="ticket">The ticket <c>--ticket</c> names, opened once the list has loaded; null for none.</param>
    public MainWindow(MainViewModel model, string? ticket = null)
    {
        InitializeComponent();
        this.model = model;
        model.Dialogs = new Dialogs(this);
        DataContext = model;

        CommandBindings.Add(new CommandBinding(WindowCommands.FocusSearch, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        CommandBindings.Add(new CommandBinding(WindowCommands.Escape, (_, _) => OnEscape()));
        CommandBindings.Add(new CommandBinding(WindowCommands.ShowQueue, (_, _) => { model.SelectedView = MainViewModel.QueueView; FocusList(); }));
        CommandBindings.Add(new CommandBinding(WindowCommands.ShowTickets, (_, _) => { model.SelectedView = MainViewModel.TicketsView; FocusList(); }));
        CommandBindings.Add(new CommandBinding(WindowCommands.Help, (_, _) => ShowHelp()));

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Announcement)) Announce(model.Announcement);
        };
        model.FocusRow += row => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => FocusRow(row));

        // Agents change tickets all the time: follow the database file (settled for a second, since one change is several
        // writes), with a slow poll behind it in case a change notification is missed.
        settle = new DispatcherTimer { Interval = SettleDelay };
        settle.Tick += async (_, _) => { settle.Stop(); await model.RefreshAsync(); };
        poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        poll.Tick += async (_, _) => await model.RefreshAsync();

        Loaded += async (_, _) =>
        {
            await model.RefreshAsync();
            Watch();
            poll.Start();
            if (ticket is null || !await model.OpenTicketAsync(ticket)) FocusList();
        };
        Closed += (_, _) =>
        {
            poll.Stop();
            settle.Stop();
            watcher?.Dispose();
        };
    }

    /// <summary>A second start handed over <paramref name="id"/> (<c>--ticket</c>): come forward and open it.</summary>
    public async Task OpenTicketAsync(string id)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!await model.OpenTicketAsync(id)) FocusList();
    }

    private void Watch()
    {
        var dir = Path.GetDirectoryName(model.DbPath);
        if (dir is null || !Directory.Exists(dir)) return;
        watcher = new FileSystemWatcher(dir, Path.GetFileName(model.DbPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { settle.Stop(); settle.Start(); });
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { settle.Stop(); settle.Start(); });
    }

    private ListBox CurrentList => model.IsQueueView ? QueueList : TicketList;

    private void FocusList()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var list = CurrentList;
            if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0; // so the action bar has a ticket to act on
            list.UpdateLayout();
            if (list.SelectedItem is { } item && list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem selected) selected.Focus();
            else list.Focus();
        });
    }

    private void FocusRow(TicketRowViewModel row)
    {
        var list = CurrentList;
        list.ScrollIntoView(row);
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
    }

    private void OnEscape()
    {
        if (SearchBox.IsKeyboardFocusWithin && model.Search.Length > 0) model.Search = "";
        else if (model.IsDetailOpen) { model.CloseDetailCommand.Execute(null); FocusList(); }
        else if (model.Search.Length > 0) model.Search = "";
    }

    /// <summary>Says <paramref name="text"/> through a screen reader without moving focus (WCAG 4.1.3).</summary>
    private void Announce(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Announcer.Text = text;
        (UIElementAutomationPeer.FromElement(Announcer) ?? UIElementAutomationPeer.CreatePeerForElement(Announcer))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ── Keyboard: every mouse action has a key (2.1.1) ───────────────────────

    private async void OnQueueKeyDown(object sender, KeyEventArgs e)
    {
        if (model.SelectedQueueRow is not { } row || e.OriginalSource is not ListBoxItem) return;
        var alt = Keyboard.Modifiers == ModifierKeys.Alt;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Up when alt: e.Handled = true; await model.MoveByAsync(row, -1); break;
            case Key.Down when alt: e.Handled = true; await model.MoveByAsync(row, +1); break;
            case Key.Home when alt: e.Handled = true; await model.MoveToAsync(row, 0); break;
            case Key.End when alt: e.Handled = true; await model.MoveToAsync(row, model.QueueRows.Count - 1); break;
            default: e.Handled = await RowKey(key, row); break;
        }
    }

    private async void OnTicketsKeyDown(object sender, KeyEventArgs e)
    {
        if (model.SelectedTicketRow is not { } row || e.OriginalSource is not ListBoxItem) return;
        e.Handled = await RowKey(e.Key == Key.System ? e.SystemKey : e.Key, row);
    }

    private async Task<bool> RowKey(Key key, TicketRowViewModel row)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return false;
        switch (key)
        {
            case Key.A when row.IsOpen: await model.ApproveAsync(row); return true;
            case Key.U when row.IsOpen: await model.UnapproveAsync(row); return true;
            case Key.R or Key.Delete when row.IsOpen: await model.RejectAsync(row); return true;
            case Key.Enter or Key.F6 when model.IsDetailOpen: DetailTitle.Focus(); return true;
            default: return false;
        }
    }

    // ── Drag and drop (the keyboard and the ▲▼ buttons do the same: 2.5.7) ──

    private Point dragStart;
    private TicketRowViewModel? dragRow, dropOn;

    private void OnQueueMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(QueueList);
        dragRow = Ancestor<Button>(e.OriginalSource as DependencyObject) is null ? RowAt(e.OriginalSource as DependencyObject) : null;
    }

    private void OnQueueMouseMove(object sender, MouseEventArgs e)
    {
        if (dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(QueueList) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = dragRow;
        dragRow = null;
        DragDrop.DoDragDrop(QueueList, new DataObject(typeof(TicketRowViewModel), row), DragDropEffects.Move);
        SetDropTarget(null);
    }

    private void OnQueueDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TicketRowViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(RowAt(e.OriginalSource as DependencyObject));
        e.Handled = true;
    }

    private void OnQueueDragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private async void OnQueueDrop(object sender, DragEventArgs e)
    {
        var target = dropOn;
        SetDropTarget(null);
        if (e.Data.GetData(typeof(TicketRowViewModel)) is not TicketRowViewModel row) return;
        var to = target is null ? model.QueueRows.Count - 1 : model.QueueRows.IndexOf(target);
        var from = model.QueueRows.IndexOf(row);
        if (target is not null && from < to) to--; // the marker sits above the target: dropping there lands just before it
        await model.MoveToAsync(row, to);
    }

    private void SetDropTarget(TicketRowViewModel? row)
    {
        if (ReferenceEquals(dropOn, row)) return;
        if (dropOn is not null) dropOn.IsDropTarget = false;
        dropOn = row;
        if (row is not null) row.IsDropTarget = true;
    }

    private static TicketRowViewModel? RowAt(DependencyObject? source) =>
        (Ancestor<ListBoxItem>(source)?.DataContext) as TicketRowViewModel;

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void ShowHelp() => MessageBox.Show(this,
        """
        The Queue is every open ticket, highest priority first. Autopilot works fixes and approved tickets, in this order.

        In a list
          Up / Down            move between tickets
          Alt+Up / Alt+Down    move the ticket up or down the queue
          Alt+Home / Alt+End   move it to the top or bottom
          A                    approve        U   back to proposed
          R or Delete          reject (closes it as wont-do, with a reason)
          Enter or F6          go to the detail pane

        Anywhere
          Ctrl+1 / Ctrl+2      Queue / Tickets
          Ctrl+F               search          Ctrl+N   new ticket
          F5                   refresh         Ctrl+Shift+S   sync (commit and push the database)
          Ctrl+Plus / Minus / 0   zoom         Esc   close details or clear search

        Changes made by agents appear by themselves within a few seconds.
        """, "Tickets Desktop help", MessageBoxButton.OK, MessageBoxImage.Information);
}
