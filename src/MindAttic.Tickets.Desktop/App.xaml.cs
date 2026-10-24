using System.Windows;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.Theme;
using MindAttic.Tickets.Desktop.ViewModels;

namespace MindAttic.Tickets.Desktop;

/// <summary>
/// Tickets Desktop: every repo's tickets from the ticket database, to browse, approve, reject, reorder and change.
/// One window per user: a second start brings the first one forward, handing it its <c>--ticket</c> if it has one
/// (<see cref="Program"/> does that before this app is built).
/// <c>--root DIR</c> picks the workspace; <c>--ticket ID</c> selects that ticket and opens its detail pane (the
/// Orchestrator dashboard's ticket links start it this way); <c>--high-contrast</c> forces the high-contrast colours (to check them without changing the Windows theme).
/// </summary>
public partial class App : Application
{
    // WCAG 1.4.13: a tooltip stays up until the pointer or focus leaves or Esc is pressed, rather than vanishing after 5s.
    static App() => System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(
        typeof(DependencyObject), new FrameworkPropertyMetadata(int.MaxValue));

    private readonly CancellationTokenSource stopListening = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var ticket = TicketHandoff.TicketArg(e.Args);
        TicketsLog.Start("Desktop");
        var log = TicketsLog.For("App");
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
        TicketsLog.Stop();
        base.OnExit(e);
    }
}
