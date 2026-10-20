using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using MindAttic.Workflow.Desktop.Data;
using MindAttic.Workflow.Desktop.Theme;
using MindAttic.Workflow.Desktop.ViewModels;

namespace MindAttic.Workflow.Desktop;

/// <summary>
/// Workflow Desktop: every repo's tickets from the workflow database, to browse, approve, reject, reorder and change.
/// One window per user: a second start brings the first one forward, handing it its <c>--ticket</c> if it has one.
/// <c>--root DIR</c> picks the workspace; <c>--ticket ID</c> selects that ticket and opens its detail pane (the
/// Orchestrator dashboard's ticket links start it this way); <c>--high-contrast</c> forces the high-contrast colours (to check them without changing the Windows theme).
/// </summary>
public partial class App : Application
{
    // WCAG 1.4.13: a tooltip stays up until the pointer or focus leaves or Esc is pressed, rather than vanishing after 5s.
    static App() => System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(
        typeof(DependencyObject), new FrameworkPropertyMetadata(int.MaxValue));

    private Mutex? single;
    private readonly CancellationTokenSource stopListening = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, @"Local\MindAttic.Workflow.Desktop", out var first);
        var ticket = TicketHandoff.TicketArg(e.Args);
        if (!first)
        {
            // Hand the ticket over before raising the window, so it comes forward already showing it.
            if (ticket is not null) TicketHandoff.Send(ticket, TimeSpan.FromSeconds(3));
            BringOtherForward();
            Shutdown();
            return;
        }

        WorkflowLog.Start("Desktop");
        var log = WorkflowLog.For("App");
        DispatcherUnhandledException += (_, x) => log.Fatal(x.Exception, "Unhandled on the UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, x) => log.Fatal(x.ExceptionObject as Exception, "Unhandled");
        TaskScheduler.UnobservedTaskException += (_, x) => log.Error(x.Exception, "Unobserved task exception");

        var args = e.Args;
        var at = Array.FindIndex(args, a => a.Equals("--root", StringComparison.OrdinalIgnoreCase));
        var root = at >= 0 && at + 1 < args.Length ? args[at + 1] : Workspace.DefaultRoot;
        HighContrast.Track(this, force: args.Contains("--high-contrast", StringComparer.OrdinalIgnoreCase));

        log.Information("Started {Version} on {Root}{Ticket}", typeof(App).Assembly.GetName().Version, root,
            ticket is null ? "" : $", opening {ticket}");
        var window = new MainWindow(new MainViewModel(new TicketStore(root), DesktopSettings.Load()), ticket);
        MainWindow = window;
        window.Show();

        _ = TicketHandoff.ListenAsync(id => Dispatcher.BeginInvoke(async () =>
        {
            log.Information("Handed {Ticket} by a second start", id);
            await window.OpenTicketAsync(id);
        }), stopListening.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        stopListening.Cancel();
        single?.Dispose();
        WorkflowLog.Stop();
        base.OnExit(e);
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
