using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using MindAttic.Workflow.Desktop.Data;
using MindAttic.Workflow.Desktop.Theme;
using MindAttic.Workflow.Desktop.ViewModels;

namespace MindAttic.Workflow.Desktop;

/// <summary>
/// Workflow Desktop: every repo's tickets from the workflow database, to browse, approve, reject, reorder and change.
/// One window per user: a second start brings the first one forward. <c>--root DIR</c> picks the workspace;
/// <c>--high-contrast</c> forces the high-contrast colours (to check them without changing the Windows theme).
/// </summary>
public partial class App : Application
{
    // WCAG 1.4.13: a tooltip stays up until the pointer or focus leaves or Esc is pressed, rather than vanishing after 5s.
    static App() => System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(
        typeof(DependencyObject), new FrameworkPropertyMetadata(int.MaxValue));

    private Mutex? single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, @"Local\MindAttic.Workflow.Desktop", out var first);
        if (!first)
        {
            BringOtherForward();
            Shutdown();
            return;
        }

        var args = e.Args;
        var at = Array.FindIndex(args, a => a.Equals("--root", StringComparison.OrdinalIgnoreCase));
        var root = at >= 0 && at + 1 < args.Length ? args[at + 1] : Workspace.DefaultRoot;
        HighContrast.Track(this, force: args.Contains("--high-contrast", StringComparer.OrdinalIgnoreCase));

        var window = new MainWindow(new MainViewModel(new TicketStore(root), DesktopSettings.Load()));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        single?.Dispose();
        base.OnExit(e);
    }

    private static void BringOtherForward()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
            SetForegroundWindow(p.MainWindowHandle);
            return;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
