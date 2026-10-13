// SPECTRE LIVE: Spectre.Console's built-in AnsiConsole.Live(...) region. You still own the key
// loop (Spectre has no input/widget model), but Live does the cursor-up-and-overwrite for you.
// It draws inline under your shell prompt instead of taking over the screen. Live can't draw a
// region taller than the window, so this spike scrolls a window of rows itself, which is the part
// you'd be hand-rolling if you went this way.
using Spectre.Console;
using Spectre.Console.Rendering;
using Spike.Shared;

var nav = new MenuNavigator(DemoMenu.Root);
var exit = false;
Console.CursorVisible = false;

AnsiConsole.Live(Build())
    .AutoClear(true)
    .Overflow(VerticalOverflow.Crop)
    .Cropping(VerticalOverflowCropping.Bottom)
    .Start(ctx =>
    {
        var lastSecond = -1;
        while (!exit)
        {
            if (Console.KeyAvailable)
            {
                exit = nav.Handle(Console.ReadKey(intercept: true).Key) == NavResult.Exit;
                ctx.UpdateTarget(Build());
            }
            else if (DateTime.Now.Second != lastSecond)
            {
                lastSecond = DateTime.Now.Second;
                ctx.UpdateTarget(Build());
            }
            else
            {
                Thread.Sleep(30);
            }
        }
    });

Console.CursorVisible = true;

IRenderable Build()
{
    var trail = string.Join(" > ", new[] { DemoMenu.Title }.Concat(nav.Breadcrumbs));
    var rows = nav.Items;

    // Visible window: window height minus the chrome (rule, status, panel borders, footer lines).
    var visible = Math.Max(3, Console.WindowHeight - 8);
    var first = Math.Clamp(nav.Index - visible / 2, 0, Math.Max(0, rows.Count - visible));

    var table = new Table().NoBorder().HideHeaders().AddColumn("").AddColumn("");
    for (var i = first; i < Math.Min(rows.Count, first + visible); i++)
    {
        var name = Markup.Escape(rows[i].Name);
        table.AddRow(
            new Markup(i == nav.Index ? $"[black on yellow] {name} [/]" : $" {name} "),
            new Markup($"[grey50]{Markup.Escape(rows[i].Description)}[/]"));
    }

    var panel = new Panel(table).Border(BoxBorder.Rounded).BorderColor(Color.Grey35);
    if (rows.Count > visible) panel.Header($"[grey50]{nav.Index + 1}/{rows.Count}[/]");

    var items = new List<IRenderable>
    {
        new Rule($"[cyan1]{Markup.Escape(trail)}[/]") { Justification = Justify.Left },
        new Markup($"  [grey50]{Markup.Escape(DemoMenu.Status())}[/]"),
        panel,
    };
    if (nav.LastPicked is not null) items.Add(new Markup($"  [green]Picked:[/] {Markup.Escape(nav.LastPicked)}"));
    items.Add(new Markup("  [green]Up/Down PgUp/PgDn[/][grey50] navigate  [/][green]Enter/→[/][grey50] open  [/][green]Esc/←[/][grey50] back  [/][green]Q[/][grey50] quit[/]"));
    return new Rows(items);
}
