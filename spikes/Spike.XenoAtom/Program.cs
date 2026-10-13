// XENOATOM.TERMINAL.UI: a retained-mode widget tree. You build the tree once and mutate state;
// the framework diffs a cell buffer and writes only changed cells (with DEC 2026 synchronized
// output). Nothing gets "re-rendered" by your code. Navigation, scrolling, type-to-jump, mouse
// and focus come from OptionList. Fullscreen via Terminal.Run (alternate screen).
// F12 toggles the built-in perf overlay (frame timings, invalidation, diff size). Ctrl+Q quits.
using Spike.Shared;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

using var session = Terminal.Open();

var stack = new List<(MenuNode Node, int Index)> { (DemoMenu.Root, 0) };
var crumbs = new State<string>(DemoMenu.Title);
var status = new State<string>(DemoMenu.Status());
var picked = new State<string>("");
var exit = new State<bool>(false);

var list = new OptionList<OptionListItem>().ActivateOnClick(true);
Load();

list.ItemActivated((_, e) => Open(e.Index, pick: true));
list.KeyDown((_, e) =>
{
    switch (e.Key)
    {
        case TerminalKey.Right:
            Open(list.SelectedIndex, pick: false);
            e.Handled = true;
            break;
        case TerminalKey.Escape or TerminalKey.Left or TerminalKey.Backspace:
            if (stack.Count == 1)
            {
                if (e.Key == TerminalKey.Escape) exit.Value = true;
            }
            else
            {
                stack.RemoveAt(stack.Count - 1);
                Load();
            }
            e.Handled = true;
            break;
    }
});

var root = new DockLayout(
    top: new VStack(
        new Rule(new Markup(() => $"[bold]{crumbs.Value}[/]")),
        new TextBlock(() => "  " + status.Value)),
    content: new Group("Menu").Content(list),
    bottom: new VStack(
        new TextBlock(() => picked.Value),
        new Footer(new TextBlock("↑↓ PgUp PgDn navigate · type to jump · Enter/→ open · Esc/← back · F12 perf · Ctrl+Q quit"))));

var lastSecond = -1;
Terminal.Run(root, () =>
{
    if (DateTime.Now.Second != lastSecond)
    {
        lastSecond = DateTime.Now.Second;
        status.Value = DemoMenu.Status(); // only the cells of this one line change on screen
    }
    return exit.Value ? TerminalLoopResult.Stop : TerminalLoopResult.Continue;
});

void Open(int index, bool pick)
{
    var current = stack[^1].Node;
    if (index < 0 || index >= current.Children!.Count) return;
    var chosen = current.Children[index];
    if (chosen.IsLeaf)
    {
        if (pick) picked.Value = "  Picked: " + string.Join(" > ", stack.Skip(1).Select(f => f.Node.Name).Append(chosen.Name));
        return;
    }
    stack[^1] = (current, index);
    stack.Add((chosen, 0));
    Load();
}

void Load()
{
    var (node, index) = stack[^1];
    list.Items.Clear();
    foreach (var child in node.Children!)
        list.Items.Add(new OptionListItem(new TextBlock(child.Name), new TextBlock(child.Description)));
    list.SelectedIndex = index;
    crumbs.Value = string.Join(" > ", new[] { DemoMenu.Title }.Concat(stack.Skip(1).Select(f => f.Node.Name)));
}
