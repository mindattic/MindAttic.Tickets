using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly MainViewModel model;
    private DatabaseWatch? watch;

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
        CommandBindings.Add(new CommandBinding(WindowCommands.ShowAudit, (_, _) => model.SelectedView = MainViewModel.AuditView));
        CommandBindings.Add(new CommandBinding(WindowCommands.ShowBoard, (_, _) => { model.SelectedView = MainViewModel.BoardView; FocusList(); }));
        CommandBindings.Add(new CommandBinding(WindowCommands.ShowActivity, (_, _) => { model.SelectedView = MainViewModel.ActivityView; FocusActivity(); }));
        CommandBindings.Add(new CommandBinding(WindowCommands.ShowStats, (_, _) => model.SelectedView = MainViewModel.StatsView));
        CommandBindings.Add(new CommandBinding(WindowCommands.ShowStories, (_, _) => { model.SelectedView = MainViewModel.StoriesView; FocusStories(); }));
        CommandBindings.Add(new CommandBinding(WindowCommands.SaveStory, async (_, _) =>
        {
            // A text box commits its text on every keystroke, so what's typed is what's saved.
            if (model.IsStoriesView && model.SaveStoryCommand.CanExecute(null)) await model.SaveStoryAsync();
        }));
        CommandBindings.Add(new CommandBinding(WindowCommands.Help, (_, _) => ShowHelp()));
        CommandBindings.Add(new CommandBinding(WindowCommands.Undo, async (_, _) =>
        {
            // A text box with nothing left to undo lets Ctrl+Z through; typing there shouldn't take back a ticket change.
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            if (!model.Busy) await model.UndoAsync();
        }));
        CommandBindings.Add(new CommandBinding(WindowCommands.CommandPalette, async (_, _) => await ShowPaletteAsync()));

        // A toast (M.TIX-T84) goes by itself after a while; ✕ or opening it removes it sooner.
        model.Toasts.CollectionChanged += (_, e) =>
        {
            foreach (var toast in e.NewItems?.OfType<ActivityToast>() ?? [])
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = MainViewModel.ToastLifetime };
                timer.Tick += (_, _) => { timer.Stop(); model.DismissToast(toast); };
                timer.Start();
            }
        };

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Announcement)) Announce(model.Announcement);
            if (e.PropertyName == nameof(MainViewModel.GroupByRepo)) ApplyGrouping();
            if (e.PropertyName == nameof(MainViewModel.GroupByEpic)) ApplyStoryGrouping();
            if (e.PropertyName == nameof(MainViewModel.SelectedView)) SyncSelection();
        };
        ApplyGrouping();
        ApplyStoryGrouping();
        model.FocusStory += row => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => FocusStoryRow(row));
        model.ClearSelectionRequested += () =>
        {
            var list = CurrentList;
            var keep = list.SelectedItem;
            list.SelectedItems.Clear();
            if (keep is not null) list.SelectedItem = keep;
            SyncSelection();
        };
        model.FocusRow += row => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => FocusRow(row));

        // Agents change tickets all the time: follow the database file (settled for a second, since one change is several
        // writes), with a slow poll behind it in case a change notification is missed. Rows are synced by ID, so the
        // selected row, its focus and the scroll position survive every refresh.
        Loaded += async (_, _) =>
        {
            await model.RefreshAsync();
            watch = new DatabaseWatch(model.DbPath, SettleDelay, PollInterval,
                () => Dispatcher.BeginInvoke(DispatcherPriority.Background, async () => await model.RefreshAsync()));
            if (ticket is null || !await model.OpenTicketAsync(ticket)) FocusList();
        };
        Closed += (_, _) => watch?.Dispose();
    }

    /// <summary>
    /// Groups the Repos list by repo, or not, as the model says (the model sorts each repo's tickets together). Grouped by
    /// the repo's heading view model, so each heading shows its counts, git state and Start link (M.TIX-T135).
    /// </summary>
    private void ApplyGrouping()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(model.TicketRows);
        view.GroupDescriptions.Clear();
        if (model.GroupByRepo) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(TicketRowViewModel.RepoHeader)));
    }

    /// <summary>Lists the Stories view's stories under a heading per epic, or not (M.TIX-T82); they're in epic order either way.</summary>
    private void ApplyStoryGrouping()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(model.StoryRows);
        view.GroupDescriptions.Clear();
        if (model.GroupByEpic) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(StoryRowViewModel.Group)));
    }

    /// <summary>A second start handed over <paramref name="id"/> (<c>--ticket</c>): come forward and open it.</summary>
    public async Task OpenTicketAsync(string id)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        // The second start allowed this process to take the foreground (AllowSetForegroundWindow); if Windows still
        // refused, a brief topmost brings the window above the one that has it without stealing it for good.
        if (!Activate()) { Topmost = true; Topmost = false; Activate(); }
        if (!await model.OpenTicketAsync(id)) FocusList();
    }

    private ListBox CurrentList => model.IsQueueView ? QueueList : TicketList; // Audit has no ticket list: Repos' stands in

    /// <summary>The list a row lives in: on the Board, its column's; else the current view's.</summary>
    private ListBox ListOf(TicketRowViewModel? row)
    {
        if (!model.IsBoardView) return CurrentList;
        var column = (row is null ? null : model.ColumnHolding(row))
                     ?? (model.SelectedBoardRow is { } sel ? model.ColumnHolding(sel) : null)
                     ?? model.BoardColumns.FirstOrDefault(c => c.Rows.Count > 0) ?? model.BoardColumns[0];
        return BoardList(column) ?? CurrentList;
    }

    /// <summary>The ListBox showing a Board column's cards (null before the Board is first laid out).</summary>
    private ListBox? BoardList(BoardColumnViewModel column)
    {
        UpdateLayout();
        return Descendants<ListBox>(this).FirstOrDefault(l => ReferenceEquals(l.DataContext, column));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private void FocusList()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var list = ListOf(null);
            if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0; // so the action bar has a ticket to act on
            list.UpdateLayout();
            if (list.SelectedItem is { } item && list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem selected) selected.Focus();
            else list.Focus();
        });
    }

    private void FocusActivity() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (ActivityList.SelectedItem is null && ActivityList.Items.Count > 0) ActivityList.SelectedIndex = 0;
        ActivityList.UpdateLayout();
        if (ActivityList.SelectedItem is { } item && ActivityList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem selected) selected.Focus();
        else ActivityList.Focus();
    });

    /// <summary>Focuses the Stories list on its selected (else first) story, without opening one that isn't selected yet.</summary>
    private void FocusStories() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        StoryList.UpdateLayout();
        var item = StoryList.SelectedItem ?? (StoryList.Items.Count > 0 ? StoryList.Items[0] : null);
        if (item is not null && StoryList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
        else StoryList.Focus();
    });

    private void FocusStoryRow(StoryRowViewModel row)
    {
        StoryList.ScrollIntoView(row);
        StoryList.UpdateLayout();
        (StoryList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
    }

    /// <summary>Enter or F6 on a story goes to its editor; the editor's fields then follow by Tab.</summary>
    private void OnStoriesKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not ListBoxItem || !model.IsStoryEditorOpen) return;
        if (e.Key is Key.Enter or Key.F6) { e.Handled = true; StoryTitle.Focus(); }
    }

    private void FocusRow(TicketRowViewModel row)
    {
        var list = ListOf(row);
        if (model.IsBoardView) list.SelectedItem = row;
        list.ScrollIntoView(row);
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
    }

    // ── Multi-select (M.TIX-T79): Shift/Ctrl+click, Shift+Up/Down, Ctrl+Space, Ctrl+A ──

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) SyncSelection();
    }

    /// <summary>Hands the current list's selection to the model, in the list's order (the order a bulk move keeps).</summary>
    private void SyncSelection()
    {
        if (model.IsBoardView || model.IsAuditView || model.IsStoriesView) { model.SelectedRows = []; return; }
        var list = CurrentList;
        model.SelectedRows = [.. list.SelectedItems.OfType<TicketRowViewModel>().OrderBy(r => list.Items.IndexOf(r))];
    }

    private void OnEscape()
    {
        // In the Stories view, Esc in the editor goes back to the story in the list (its edits are kept); in the list it closes the editor.
        if (model.IsStoriesView && !SearchBox.IsKeyboardFocusWithin)
        {
            if (StoryEditorPane.IsKeyboardFocusWithin)
            {
                if (model.SelectedStoryRow is { } row) FocusStoryRow(row); else FocusStories();
                return;
            }
            if (model.IsStoryEditorOpen) { model.CloseStoryCommand.Execute(null); FocusStories(); return; }
        }
        if (model.IsMultiSelect && !SearchBox.IsKeyboardFocusWithin) { model.ClearSelectionCommand.Execute(null); return; }
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
        // With several selected, Alt+Home / Alt+End move them all, together; Alt+Up / Down still move one place at a time.
        var focused = (e.OriginalSource as ListBoxItem)?.DataContext as TicketRowViewModel ?? row;
        var many = model.RowsFor(focused) is { Count: > 1 } sel ? sel : null;
        switch (key)
        {
            case Key.Up when alt: e.Handled = true; await model.MoveByAsync(row, -1); break;
            case Key.Down when alt: e.Handled = true; await model.MoveByAsync(row, +1); break;
            case Key.Home when alt: e.Handled = true; if (many is not null) await model.MoveRowsAsync(many, toTop: true); else await model.MoveToAsync(row, 0); break;
            case Key.End when alt: e.Handled = true; if (many is not null) await model.MoveRowsAsync(many, toTop: false); else await model.MoveToAsync(row, model.QueueRows.Count - 1); break;
            default: e.Handled = await RowKey(key, focused); break;
        }
    }

    private async void OnTicketsKeyDown(object sender, KeyEventArgs e)
    {
        if (model.SelectedTicketRow is not { } row || e.OriginalSource is not ListBoxItem item) return;
        e.Handled = await RowKey(e.Key == Key.System ? e.SystemKey : e.Key, item.DataContext as TicketRowViewModel ?? row);
    }

    /// <summary>A key on a row: the action takes every selected ticket when the row is one of several selected (M.TIX-T79).</summary>
    private async Task<bool> RowKey(Key key, TicketRowViewModel row)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return false;
        var rows = model.RowsFor(row);
        var anyOpen = rows.Any(r => r.IsOpen);
        switch (key)
        {
            case Key.A when anyOpen: await model.ApproveRowsAsync(rows); return true;
            case Key.U when anyOpen: await model.UnapproveRowsAsync(rows); return true;
            case Key.R or Key.Delete when anyOpen: await model.RejectRowsAsync(rows); return true;
            case Key.C when anyOpen: await model.CloseRowsAsync(rows, TicketRules.Closed); return true;
            case Key.W when anyOpen: await model.CloseRowsAsync(rows, TicketRules.WontDo); return true;
            case Key.T: await model.SetTypeRowsAsync(rows); return true;
            case Key.O when row.Worker is not null: model.ShowWorker(row); return true;
            case Key.Enter or Key.F6 when model.IsDetailOpen: DetailTitle.Focus(); return true;
            case Key.Left when model.IsDetailOpen: model.StepVersion(-1); return true;
            case Key.Right when model.IsDetailOpen: model.StepVersion(+1); return true;
            default: return false;
        }
    }

    /// <summary>Left / Right in the detail pane step through the ticket's versions (M.TIX-T137), except where they move a caret.</summary>
    private void OnDetailKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is TextBox) return;
        if (e.Key == Key.Left) { model.StepVersion(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { model.StepVersion(+1); e.Handled = true; }
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

    // ── Board (M.TIX-T77): drag a card to another column, or Alt+Left / Alt+Right (2.5.7) ──

    private TicketRowViewModel? boardDragRow;
    private BoardColumnViewModel? boardDropOn;

    private void OnBoardSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not ListBox { SelectedItem: TicketRowViewModel row } list) return;
        // One selection across the board: picking a card in one column clears the others.
        foreach (var other in model.BoardColumns.Select(BoardList))
            if (other is not null && !ReferenceEquals(other, list)) other.SelectedItem = null;
        model.SelectedBoardRow = row;
    }

    private async void OnBoardKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: TicketRowViewModel row, DataContext: BoardColumnViewModel column } || e.OriginalSource is not ListBoxItem) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        switch (key)
        {
            case Key.Left or Key.Right when mods == ModifierKeys.Alt:
                e.Handled = true;
                await model.MoveCardByAsync(row, key == Key.Left ? -1 : +1);
                break;
            case Key.Left or Key.Right when mods == ModifierKeys.None:
                e.Handled = true;
                FocusColumn((int)column.Column, key == Key.Left ? -1 : +1);
                break;
            default:
                e.Handled = await RowKey(key, row);
                break;
        }
    }

    /// <summary>Moves focus from the column at <paramref name="from"/> by <paramref name="delta"/> columns to that column's selected
    /// (else first) card, passing over empty columns; nothing past either end.</summary>
    private void FocusColumn(int from, int delta)
    {
        for (var i = from + delta; i >= 0 && i < model.BoardColumns.Count; i += delta)
        {
            var column = model.BoardColumns[i];
            if (column.Rows.Count == 0) continue;
            if (BoardList(column) is not { } list) return;
            if (list.SelectedItem is null) list.SelectedIndex = 0;
            list.ScrollIntoView(list.SelectedItem!);
            list.UpdateLayout();
            (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem)?.Focus();
            return;
        }
    }

    private void OnBoardMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition((IInputElement)sender);
        boardDragRow = RowAt(e.OriginalSource as DependencyObject);
    }

    private void OnBoardMouseMove(object sender, MouseEventArgs e)
    {
        if (boardDragRow is null || e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list) return;
        var d = e.GetPosition(list) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = boardDragRow;
        boardDragRow = null;
        DragDrop.DoDragDrop(list, new DataObject(typeof(TicketRowViewModel), row), DragDropEffects.Move);
        SetBoardDropTarget(null);
    }

    private void OnBoardDragOver(object sender, DragEventArgs e)
    {
        var column = (sender as FrameworkElement)?.DataContext as BoardColumnViewModel;
        var row = e.Data.GetData(typeof(TicketRowViewModel)) as TicketRowViewModel;
        var from = row is null ? null : model.ColumnHolding(row);
        var allowed = column is not null && row is { IsOpen: true } && from is not null && !ReferenceEquals(from, column);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        SetBoardDropTarget(allowed ? column : null);
        e.Handled = true;
    }

    private void OnBoardDragLeave(object sender, DragEventArgs e) => SetBoardDropTarget(null);

    private async void OnBoardDrop(object sender, DragEventArgs e)
    {
        SetBoardDropTarget(null);
        if ((sender as FrameworkElement)?.DataContext is not BoardColumnViewModel column || e.Data.GetData(typeof(TicketRowViewModel)) is not TicketRowViewModel row) return;
        e.Handled = true;
        await model.MoveCardAsync(row, column.Column);
    }

    private void SetBoardDropTarget(BoardColumnViewModel? column)
    {
        if (ReferenceEquals(boardDropOn, column)) return;
        if (boardDropOn is not null) boardDropOn.IsDropTarget = false;
        boardDropOn = column;
        if (column is not null) column.IsDropTarget = true;
    }

    private static TicketRowViewModel? RowAt(DependencyObject? source) =>
        (Ancestor<ListBoxItem>(source)?.DataContext) as TicketRowViewModel;

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ── Command palette (Ctrl+K, M.TIX-T81) ──────────────────────────────────

    private bool paletteOpen;

    /// <summary>Opens the palette over the window's own commands, the model's and every ticket and story, then runs the choice.</summary>
    private async Task ShowPaletteAsync()
    {
        if (paletteOpen) return;
        paletteOpen = true;
        PaletteItem? chosen;
        try
        {
            var palette = new CommandPaletteWindow(new CommandPaletteViewModel(model.PaletteItems(WindowPaletteCommands()))) { Owner = this };
            chosen = palette.ShowDialog() == true ? palette.Result : null;
        }
        finally { paletteOpen = false; }
        if (chosen is null) return;
        await chosen.Run();
        // A command that doesn't move focus itself leaves it on the list it acted on, not on whatever had it before.
        if (chosen.Kind == PaletteKind.Command && Keyboard.FocusedElement is not TextBox) FocusList();
    }

    private IEnumerable<PaletteItem> WindowPaletteCommands()
    {
        PaletteItem Routed(string title, RoutedCommand command, string shortcut) =>
            new(PaletteKind.Command, title, title, "", () => { command.Execute(null, this); return Task.CompletedTask; }, shortcut);
        yield return Routed("Show Repos", WindowCommands.ShowTickets, "Ctrl+1");
        yield return Routed("Show the Queue", WindowCommands.ShowQueue, "Ctrl+2");
        yield return Routed("Show Audit", WindowCommands.ShowAudit, "Ctrl+3");
        yield return Routed("Show the Board", WindowCommands.ShowBoard, "Ctrl+4");
        yield return Routed("Show Activity", WindowCommands.ShowActivity, "Ctrl+5");
        yield return Routed("Show Stats", WindowCommands.ShowStats, "Ctrl+6");
        yield return Routed("Show Stories", WindowCommands.ShowStories, "Ctrl+7");
        yield return Routed("Search tickets", WindowCommands.FocusSearch, "Ctrl+F");
        yield return Routed("Keyboard shortcuts and help", WindowCommands.Help, "F1");
    }
    /// <summary>A Stats table's generated column (M.TIX-T86): headed by its column's caption (its name is c0, c1, ... so it
    /// binds), and numbers right-aligned.</summary>
    internal static void OnStatsColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is DataGrid { ItemsSource: System.Data.DataView { Table: { } table } } && table.Columns[e.PropertyName] is { } column)
            e.Column.Header = column.Caption;
        if (e.PropertyType == typeof(int) && e.Column is DataGridTextColumn text)
            text.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
    }

    private void OnStatsAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e) => OnStatsColumn(sender, e);

    /// <summary>The wheel over a Stats table scrolls the tab, not the table's own (never-needed) vertical scroller.</summary>
    private void OnStatsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void ShowHelp() => MessageBox.Show(this,
        """
        Repos lists every repo's tickets under its name, with its git state and a Start link that opens the Orchestrator
        to start a CLI there. The Queue is every open ticket in the order /allocate hands them out, highest priority
        first; /allocate works fixes and approved tickets, and a ticket being worked shows who has it (click to open the
        Orchestrator on it). Audit shows closed tickets to double-check (closed with no Ref, or that a later ticket says
        came back) and the Orchestrator's drift and duplicate-code findings. GitHub intake shows what it would file from
        GitHub issues labelled 'orchestrator' and CI failures on main, and files them only when you click File. The
        Board shows tickets as cards in Proposed, Approved, In progress and Done: moving a card unapproves, approves,
        marks it worked by hand, or closes it (Done shows the 50 most recent; a closed ticket can't be dragged back).
        Activity is every ticket change in every repo as it happens, newest first; when an agent closes a ticket, a
        toast says so (click it to open the ticket).
        Stats shows ticket metrics from the tickets' history, as tables: tickets filed, closed and open per day, week or
        month, cycle time (filed to closed) by type, and the tickets open at the end of each period by repo and by type.
        Stories lists every repo's user stories under their epics (filter
        them by repo, search and story status): select one to edit its statement, status, acceptance and evidence, and
        Save (Ctrl+S) writes it and rewrites the repo's USER_STORIES.md view, or Revert drops the edits. A story changed
        elsewhere since you opened it isn't overwritten. Add the tickets that implement it, add stories and epics, or
        rename an epic there too. A story ID clicked anywhere opens it in Stories.

        Ctrl+Z undoes your last approve, back to proposed, reject, queue move, close, won't do, board move or link
        change (a story's implementing ticket too), newest first, by writing the opposite change (a close is undone by reopening the ticket with its old
        ref). Nothing is deleted: the ticket's history shows both, and an 'undo' entry says what was undone. A change
        someone else has changed again since isn't undone.

        In a list
          Up / Down            move between tickets
          Alt+Up / Alt+Down    move the ticket up or down the queue
          Alt+Home / Alt+End   move it to the top or bottom
          A                    approve        U   back to proposed
          R or Delete          reject (closes it as wont-do, with a reason)
          C / W                close (optional ref) / won't do (with a reason)
          T                    set the type (with a reason)
          Shift+Up / Down      select several (or Shift/Ctrl+click; Ctrl+Space toggles one, Ctrl+A all);
                               every key above then acts on each selected ticket, recorded on each,
                               Alt+Home / Alt+End move them all to the top or bottom, Esc clears the selection
          O                    open the Orchestrator on the ticket's live worker
          Enter or F6          go to the detail pane
          Left / Right         older / newer version of the ticket shown

        On the Board
          Left / Right         the next column's cards
          Alt+Left / Alt+Right move the card to the column before or after (or drag it)

        In Stories
          Up / Down            move between stories (the selected one opens in the editor)
          Enter or F6          go to the editor          Esc   back to the list (edits are kept)
          Ctrl+S               save the story            Revert drops the edits

        Anywhere
          Ctrl+K              command palette: type part of a ticket or story ID or text, or a command's
                               name; Up / Down choose, Enter runs (> commands only, # tickets and stories only)
          Ctrl+1 / 2 / 3 / 4 / 5 / 6 / 7   Repos / Queue / Audit / Board / Activity / Stats / Stories
          Work filter          Queued (/allocate takes it next), Needs approval, Blocked (waiting on other tickets)
          Ctrl+F               search          Ctrl+N   new ticket
          Ctrl+Z               undo the last change
          Ctrl+E               export the tickets shown (filters, query and sort applied) as CSV or markdown
          F5                   refresh         Ctrl+Shift+S   sync (commit and push the database)
          Ctrl+Plus / Minus / 0   zoom         Esc   close details or clear search

        Changes made by agents appear by themselves within a few seconds.
        """, "Tickets Desktop help", MessageBoxButton.OK, MessageBoxImage.Information);
}
