using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MindAttic.Tickets.Desktop;

/// <summary>
/// The entry point. One window per user: a second start (the Orchestrator's ticket links run <c>--ticket ID</c>) hands
/// its ticket to the window already open and brings it forward, before any WPF or theme loads, so a link opens fast;
/// only the first start builds the app.
/// </summary>
public static class Program
{
    private const string SingleInstance = @"Local\MindAttic.Tickets.Desktop";

    [STAThread]
    public static int Main(string[] args)
    {
        using var single = new Mutex(true, SingleInstance, out var first);
        if (!first)
        {
            HandToOpenWindow(TicketHandoff.TicketArg(args));
            return 0;
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static void HandToOpenWindow(string? ticket)
    {
        // This start has the foreground (it was just launched by a click); let the open window take it, so its own
        // Activate succeeds as it shows the ticket. Hand the ticket over before raising it, so it comes forward showing it.
        AllowSetForegroundWindow(AsfwAny);
        if (ticket is not null) TicketHandoff.Send(ticket, TimeSpan.FromSeconds(3));
        BringOtherForward();
    }

    private static void BringOtherForward()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE, only when minimized: not un-maximize it
            SetForegroundWindow(p.MainWindowHandle);
            return;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
