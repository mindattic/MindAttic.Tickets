// FLICKER-FREE: the same look as Baseline, still "re-render the whole page every key press",
// but every frame is
//   1. rendered off-screen (a Spectre IAnsiConsole writing to a StringWriter),
//   2. written to the real console in ONE Write call,
//   3. painted over the previous frame (cursor home + clear-to-end-of-line per row), never erased first,
//   4. wrapped in DEC 2026 synchronized output so Windows Terminal swaps the frame in atomically,
//   5. drawn on the alternate screen buffer, so scrollback is untouched when you quit.
// A frame identical to the last one is skipped outright.
using System.Text;
using Spectre.Console;
using Spike.Shared;

const string AltScreenOn = "\x1b[?1049h", AltScreenOff = "\x1b[?1049l";
const string SyncBegin = "\x1b[?2026h", SyncEnd = "\x1b[?2026l";
const string Home = "\x1b[H", ClearEol = "\x1b[K", ClearBelow = "\x1b[J";

var nav = new MenuNavigator(DemoMenu.Root);
string? lastFrame = null;
var frames = 0;
var skipped = 0;

Console.OutputEncoding = Encoding.UTF8;
Console.CursorVisible = false;
Console.Write(AltScreenOn);
try
{
    while (true)
    {
        Present(BuildFrame());
        if (!Console.KeyAvailable)
        {
            Thread.Sleep(50); // the status clock ticks once a second; unchanged frames are skipped
            continue;
        }
        if (nav.Handle(Console.ReadKey(intercept: true).Key) == NavResult.Exit) return;
    }
}
finally
{
    Console.Write(AltScreenOff);
    Console.CursorVisible = true;
    Console.WriteLine($"FlickerFree: {frames} frames written, {skipped} identical frames skipped.");
}

string BuildFrame()
{
    var sw = new StringWriter();
    var buffer = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.Yes,
        ColorSystem = ColorSystemSupport.TrueColor,
        Out = new AnsiConsoleOutput(sw),
        Interactive = InteractionSupport.No,
    });
    buffer.Profile.Width = Math.Max(20, Console.WindowWidth);

    var trail = string.Join(" > ", new[] { DemoMenu.Title }.Concat(nav.Breadcrumbs));
    buffer.Write(new Rule($"[cyan1]{Markup.Escape(trail)}[/]") { Justification = Justify.Left });
    buffer.MarkupLine($"  [grey50]{Markup.Escape(DemoMenu.Status())}[/]");
    buffer.WriteLine();

    var rows = nav.Items;
    var width = rows.Max(r => r.Name.Length);
    for (var i = 0; i < rows.Count; i++)
    {
        var name = Markup.Escape(rows[i].Name.PadRight(width));
        var desc = $"  [grey50]{Markup.Escape(rows[i].Description)}[/]";
        buffer.MarkupLine(i == nav.Index ? $"[yellow]> {name}[/]{desc}" : $"  {name}{desc}");
    }
    buffer.WriteLine();
    if (nav.LastPicked is not null) buffer.MarkupLine($"  [green]Picked:[/] {Markup.Escape(nav.LastPicked)}");
    buffer.MarkupLine("  [green]Up/Down[/][grey50] navigate  [/][green]Enter[/][grey50] select  [/][green]Esc[/][grey50] back  [/][green]Q[/][grey50] quit[/]");
    buffer.MarkupLine($"  [grey35]frames written: {frames}  ·  identical frames skipped: {skipped}[/]");
    return sw.ToString();
}

void Present(string frame)
{
    if (frame == lastFrame) { skipped++; return; }
    lastFrame = frame;
    frames++;

    var sb = new StringBuilder(frame.Length + 256);
    sb.Append(SyncBegin).Append(Home);
    // Paint over each row and clear only its leftover tail, so no cell is ever blank mid-frame.
    foreach (var line in frame.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        sb.Append(line).Append(ClearEol).Append('\n');
    sb.Append(ClearBelow).Append(SyncEnd);
    Console.Out.Write(sb.ToString());
    Console.Out.Flush();
}
