namespace Spike.Shared;

public enum NavResult { None, Moved, Entered, Left, Picked, Exit }

/// <summary>
/// Framework-free navigation state for the hand-rolled spikes (Baseline, FlickerFree,
/// SpectreLive): a stack of (menu, highlighted index). The framework spikes use their
/// own list widgets instead and only borrow the menu tree.
/// </summary>
public sealed class MenuNavigator
{
    private readonly List<(MenuNode Node, int Index)> _stack = [];

    public MenuNavigator(MenuNode root) => _stack.Add((root, 0));

    public MenuNode Current => _stack[^1].Node;
    public int Index => _stack[^1].Index;
    public IReadOnlyList<MenuNode> Items => Current.Children ?? [];
    public IEnumerable<string> Breadcrumbs => _stack.Skip(1).Select(f => f.Node.Name);
    public string? LastPicked { get; private set; }

    public NavResult Handle(ConsoleKey key)
    {
        var count = Items.Count;
        switch (key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K:
                return Move(Index == 0 ? count - 1 : Index - 1);
            case ConsoleKey.DownArrow or ConsoleKey.J:
                return Move((Index + 1) % count);
            case ConsoleKey.Home:
                return Move(0);
            case ConsoleKey.End:
                return Move(count - 1);
            case ConsoleKey.PageUp:
                return Move(Math.Max(0, Index - 10));
            case ConsoleKey.PageDown:
                return Move(Math.Min(count - 1, Index + 10));
            case ConsoleKey.Enter or ConsoleKey.RightArrow:
                var chosen = Items[Index];
                if (!chosen.IsLeaf)
                {
                    _stack.Add((chosen, 0));
                    return NavResult.Entered;
                }
                if (key == ConsoleKey.RightArrow) return NavResult.None;
                LastPicked = string.Join(" > ", Breadcrumbs.Append(chosen.Name));
                return NavResult.Picked;
            case ConsoleKey.Escape or ConsoleKey.LeftArrow or ConsoleKey.Backspace:
                if (_stack.Count == 1) return key == ConsoleKey.Escape ? NavResult.Exit : NavResult.None;
                _stack.RemoveAt(_stack.Count - 1);
                return NavResult.Left;
            case ConsoleKey.Q:
                return NavResult.Exit;
            default:
                return NavResult.None;
        }
    }

    private NavResult Move(int index)
    {
        if (index == Index) return NavResult.None;
        _stack[^1] = (Current, index);
        return NavResult.Moved;
    }
}
