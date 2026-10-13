// TERMINAL.GUI v2: a full windowing toolkit (Window, ListView, Pos/Dim layout, mouse, themes).
// Double-buffered: views mark themselves dirty and only dirty regions are redrawn. It owns the
// whole screen and looks like a desktop app, not like the Launcher's breadcrumb-and-list style.
// Esc is rebound from "quit app" to "back"; Ctrl+Q quits.
using System.Collections.ObjectModel;
using Spike.Shared;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// By default Esc quits the running window; the Launcher's Esc means "back", so move quit to Ctrl+Q.
Application.SetDefaultKeyBinding(Command.Quit, Bind.All(Key.Q.WithCtrl));

using IApplication app = Application.Create().Init();
using var window = new MenuWindow();
app.Run(window);

internal sealed class MenuWindow : Window
{
    private readonly List<(MenuNode Node, int Index)> _stack = [(DemoMenu.Root, 0)];
    private readonly Label _status = new() { X = 1, Y = 0, Width = Dim.Fill() };
    private readonly Label _picked = new() { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
    private readonly ListView _list = new() { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(2) };
    private object? _timer;

    public MenuWindow()
    {
        _list.Accepting += (_, e) =>
        {
            Open(pick: true);
            e.Handled = true; // don't let Accept bubble up to the Window
        };
        _list.KeyDown += (_, key) =>
        {
            if (key == Key.CursorRight)
            {
                Open(pick: false);
                key.Handled = true;
            }
            else if (key == Key.Esc || key == Key.CursorLeft || key == Key.Backspace)
            {
                if (_stack.Count > 1)
                {
                    _stack.RemoveAt(_stack.Count - 1);
                    Load();
                }
                key.Handled = true;
            }
        };

        var hint = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Text = "↑↓ PgUp PgDn navigate · type to jump · Enter/→ open · Esc/← back · Ctrl+Q quit",
        };

        Add(_status, _list, _picked, hint);
        Load();

        Initialized += (_, _) =>
        {
            _status.Text = DemoMenu.Status();
            _timer = App!.AddTimeout(TimeSpan.FromSeconds(1), () =>
            {
                _status.Text = DemoMenu.Status(); // marks only this label dirty
                return true;
            });
        };
    }

    private void Open(bool pick)
    {
        var current = _stack[^1].Node;
        var index = _list.SelectedItem ?? 0;
        if (index < 0 || index >= current.Children!.Count) return;
        var chosen = current.Children[index];
        if (chosen.IsLeaf)
        {
            if (pick) _picked.Text = "Picked: " + string.Join(" > ", _stack.Skip(1).Select(f => f.Node.Name).Append(chosen.Name));
            return;
        }
        _stack[^1] = (current, index);
        _stack.Add((chosen, 0));
        Load();
    }

    private void Load()
    {
        var (node, index) = _stack[^1];
        var children = node.Children!;
        var width = children.Max(c => c.Name.Length);
        _list.SetSource(new ObservableCollection<string>(
            children.Select(c => $"{c.Name.PadRight(width)}   {c.Description}")));
        _list.SelectedItem = index;
        Title = string.Join(" > ", new[] { DemoMenu.Title }.Concat(_stack.Skip(1).Select(f => f.Node.Name)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _timer is not null) App?.RemoveTimeout(_timer);
        base.Dispose(disposing);
    }
}
