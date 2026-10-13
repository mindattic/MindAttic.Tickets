// BASELINE: a faithful copy of how MindAttic.Launcher draws today (Ui/Menu.cs + Ui/Screen.cs).
//  - Entering/leaving a menu (and the refresh timer): AnsiConsole.Clear() + header + full redraw.
//  - Arrow keys: cursor back to the menu's top row, ESC[J (erase below), then one MarkupLine per row.
// Hold an arrow key down to see the flicker the other spikes are compared against.
using Spectre.Console;
using Spike.Shared;

var nav = new MenuNavigator(DemoMenu.Root);
var startTop = 0;
var lastSecond = -1;
Console.CursorVisible = false;

try
{
    FullRedraw();
    while (true)
    {
        if (!Console.KeyAvailable)
        {
            // Stand-in for the Launcher's refresh timer: a full clear-and-redraw.
            if (DateTime.Now.Second != lastSecond) FullRedraw();
            Thread.Sleep(50);
            continue;
        }

        switch (nav.Handle(Console.ReadKey(intercept: true).Key))
        {
            case NavResult.Exit:
                return;
            case NavResult.Entered or NavResult.Left or NavResult.Picked:
                FullRedraw();
                break;
            case NavResult.Moved:
                Console.SetCursorPosition(0, startTop);
                Console.Write("\x1b[J");
                RenderMenu();
                break;
        }
    }
}
finally
{
    Console.CursorVisible = true;
}

void FullRedraw()
{
    lastSecond = DateTime.Now.Second;
    AnsiConsole.Clear();
    var trail = string.Join(" > ", new[] { DemoMenu.Title }.Concat(nav.Breadcrumbs));
    AnsiConsole.Write(new Rule($"[cyan1]{Markup.Escape(trail)}[/]") { Justification = Justify.Left });
    AnsiConsole.MarkupLine($"  [grey50]{Markup.Escape(DemoMenu.Status())}[/]");
    AnsiConsole.WriteLine();
    startTop = Console.CursorTop;
    RenderMenu();
}

void RenderMenu()
{
    var rows = nav.Items;
    var width = rows.Max(r => r.Name.Length);
    for (var i = 0; i < rows.Count; i++)
    {
        var name = Markup.Escape(rows[i].Name.PadRight(width));
        var desc = $"  [grey50]{Markup.Escape(rows[i].Description)}[/]";
        AnsiConsole.MarkupLine(i == nav.Index ? $"[yellow]> {name}[/]{desc}" : $"  {name}{desc}");
    }
    AnsiConsole.WriteLine();
    if (nav.LastPicked is not null) AnsiConsole.MarkupLine($"  [green]Picked:[/] {Markup.Escape(nav.LastPicked)}");
    AnsiConsole.MarkupLine("  [green]Up/Down[/][grey50] navigate  [/][green]Enter[/][grey50] select  [/][green]Esc[/][grey50] back  [/][green]Q[/][grey50] quit[/]");
}
