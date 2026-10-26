using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Desktop.Bus;

/// <summary>
/// Tickets.Desktop on the app bus (M.TIX-T158): subscribes to the Orchestrator daemon's status stream, reconnecting with a
/// backoff (1s doubling to 10s) while the daemon is down, says it's present (<c>app-present</c>, handling <c>open-ticket</c>)
/// after every connect, and raises <see cref="TicketChanged"/> and <see cref="OpenTicketRequested"/> for the lines it reads. It
/// writes requests to the daemon's inbox, best effort (100 ms to connect). Nothing a pipe or a writer does reaches the caller:
/// every failure is contained and logged. Events are raised on the thread pool: marshal to the UI thread yourself.
/// </summary>
public sealed partial class AppBusClient : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly string streamPipe, inboxPipe;
    private readonly CancellationTokenSource stop = new();
    private readonly Lock gate = new();
    private readonly Dictionary<string, AppPresence> presences = new(StringComparer.OrdinalIgnoreCase);
    private Task? run;
    private bool connected;

    public AppBusClient(string? streamPipe = null, string? inboxPipe = null)
    {
        this.streamPipe = streamPipe ?? AppBus.StreamPipeName;
        this.inboxPipe = inboxPipe ?? AppBus.InboxPipeName;
    }

    /// <summary>The stream connected (true) or dropped (false).</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>A ticket changed (a <c>ticket</c> line).</summary>
    public event Action<TicketBusEvent>? TicketChanged;

    /// <summary>The dashboard asks to show a ticket (an <c>open-ticket</c> line meant for Tickets.Desktop), by its upper-cased id.</summary>
    public event Action<string>? OpenTicketRequested;

    public bool IsConnected
    {
        get { lock (gate) return connected; }
    }

    /// <summary>Starts the subscription (once).</summary>
    public void Start()
    {
        lock (gate) run ??= Task.Run(() => RunAsync(stop.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var log = TicketsLog.For("Bus");
        var backoff = FirstBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", streamPipe, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(1000, ct);
                backoff = FirstBackoff;
                SetConnected(true);
                log.Information("Connected to the Orchestrator's app bus");
                for (var attempt = 1; attempt <= 3 && !SendPresence(); attempt++) // the daemon's inbox may be busy for a moment
                    if (attempt < 3) await Task.Delay(250, ct);
                using var reader = new StreamReader(pipe, Utf8);
                while (true)
                {
                    using var silent = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    silent.CancelAfter(AppBus.SilentFor);
                    string? line;
                    try { line = await reader.ReadLineAsync(silent.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        log.Warning("The Orchestrator's app bus was silent for {Seconds}s: reconnecting", AppBus.SilentFor.TotalSeconds);
                        break;
                    }
                    if (line is null) break; // the daemon closed it
                    Handle(line);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (TimeoutException) { } // no daemon: the pipe doesn't exist
            catch (Exception ex)
            {
                log.Warning(ex, "The Orchestrator's app bus failed");
            }
            finally
            {
                if (IsConnected) log.Information("Disconnected from the Orchestrator's app bus");
                SetConnected(false);
            }
            if (ct.IsCancellationRequested) return;
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private void SetConnected(bool value)
    {
        lock (gate)
        {
            if (connected == value) return;
            connected = value;
            if (!value) presences.Clear(); // the next connection sends them all again
        }
        Raise(() => ConnectionChanged?.Invoke(value));
    }

    /// <summary>Acts on one line from the stream: kinds and versions it doesn't read are skipped. Never throws.</summary>
    internal void Handle(string line)
    {
        if (AppBus.Parse(line) is not { } e) return;
        switch (e.Kind)
        {
            case AppBus.Ticket when AppBus.PayloadAs<TicketBusEvent>(e) is { Id.Length: > 0 } t:
                Raise(() => TicketChanged?.Invoke(t));
                break;
            case AppBus.Presence when AppBus.PayloadAs<AppPresence>(e) is { App.Length: > 0 } p:
                lock (gate) presences[p.App] = p;
                break;
            case AppBus.OpenTicket when AppBus.PayloadAs<OpenTicketRequest>(e) is { } r && IsForUs(r.To) && IsTicketId(r.Id):
                var id = r.Id.Trim().ToUpperInvariant();
                Raise(() => OpenTicketRequested?.Invoke(id));
                break;
        }
    }

    private static bool IsForUs(string? to) => string.IsNullOrWhiteSpace(to) || to.Equals(AppBus.TicketsDesktop, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*[A-Za-z0-9][A-Za-z0-9._-]{0,63}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TicketId();

    internal static bool IsTicketId(string? id) => id is not null && TicketId().IsMatch(id);

    private static void Raise(Action raise)
    {
        try { raise(); }
        catch (Exception ex) { TicketsLog.For("Bus").Error(ex, "An app bus handler failed"); }
    }

    /// <summary>The app's presence when it's listening (its process alive) and handles <paramref name="kind"/>; else null.</summary>
    public AppPresence? Present(string app, string kind)
    {
        AppPresence? p;
        lock (gate) p = presences.GetValueOrDefault(app);
        if (p is null || !p.Handles.Contains(kind, StringComparer.OrdinalIgnoreCase) || !IsAlive(p)) return null;
        return p;
    }

    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(2);

    /// <summary>Whether the process that said <paramref name="presence"/> still runs: its pid does, and not as a process started
    /// after it was said (a reused pid; M.TIX-T168, as M.ORCH-T1010). A presence with no time is judged by its pid alone.</summary>
    private static bool IsAlive(AppPresence presence)
    {
        if (presence.Pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(presence.Pid);
            if (proc.HasExited) return false;
            return presence.AtUtc == default || proc.StartTime.ToUniversalTime() <= presence.AtUtc.ToUniversalTime() + StartSlack;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return ex is System.ComponentModel.Win32Exception; // access denied: it exists (its start time can't be read)
        }
    }

    /// <summary>Says Tickets.Desktop is listening and handles <c>open-ticket</c>.</summary>
    public bool SendPresence() => Send(AppBus.Presence, new AppPresence
    {
        App = AppBus.TicketsDesktop, Pid = Environment.ProcessId, Handles = [AppBus.OpenTicket], AtUtc = DateTime.UtcNow,
    });

    /// <summary>
    /// Asks the dashboard to show who works <paramref name="id"/>, when it's present and handles <c>show-worker</c>; false when it
    /// isn't or the send failed (the caller falls back to starting the Orchestrator).
    /// </summary>
    public bool TryShowWorker(string id, string? login, string? session)
    {
        if (Present(AppBus.OrchestratorDesktop, AppBus.ShowWorker) is not { } dashboard) return false;
        AllowSetForegroundWindow(dashboard.Pid);
        return Send(AppBus.ShowWorker, new ShowWorkerRequest
        {
            Id = id, Login = login, Session = session, From = AppBus.TicketsDesktop, To = AppBus.OrchestratorDesktop,
        });
    }

    /// <summary>Writes one envelope to the daemon's inbox; false when it couldn't (no daemon, every listener busy). Never throws.</summary>
    public bool Send<T>(string kind, T payload)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", inboxPipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(100);
            var bytes = Utf8.GetBytes(AppBus.Serialize(kind, payload) + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex)
        {
            if (ex is not TimeoutException) TicketsLog.For("Bus").Warning(ex, "Couldn't send {Kind:l} on the app bus", kind);
            return false;
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        ConnectionChanged = null;
        TicketChanged = null;
        OpenTicketRequested = null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
