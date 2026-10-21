using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace MindAttic.Tickets.Desktop;

/// <summary>
/// <c>--ticket ID</c> on the command line, and how a second start hands that id to the window already open: the first
/// instance listens on a named pipe (one per Windows session, like the single-instance mutex); a later start writes the
/// id to it and exits. The Orchestrator dashboard's ticket links (M.ORCH-T500) start Tickets Desktop this way.
/// </summary>
public static class TicketHandoff
{
    /// <summary>The pipe the window open in this Windows session listens on.</summary>
    public static string PipeName { get; } = $"MindAttic.Tickets.Desktop.{Process.GetCurrentProcess().SessionId}";

    /// <summary>The id after <c>--ticket</c> (or in <c>--ticket=ID</c>), trimmed and upper-cased; null when there isn't one.</summary>
    public static string? TicketArg(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? value = null;
            if (a.Equals("--ticket", StringComparison.OrdinalIgnoreCase)) value = i + 1 < args.Count ? args[i + 1] : null;
            else if (a.StartsWith("--ticket=", StringComparison.OrdinalIgnoreCase)) value = a["--ticket=".Length..];
            if (value is null) continue;
            value = value.Trim();
            return value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal) ? null : value.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>Hands <paramref name="id"/> to the open window. False when nothing is listening within <paramref name="timeout"/>.</summary>
    public static bool Send(string id, TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out);
            pipe.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(id);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens until <paramref name="stop"/> is cancelled, calling <paramref name="received"/> with each id a later start
    /// sends (on the thread pool: marshal to the UI thread yourself).
    /// </summary>
    public static async Task ListenAsync(Action<string> received, CancellationToken stop, string? pipeName = null)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var id = (await reader.ReadToEndAsync(stop)).Trim();
                if (id.Length > 0 && id.Length <= 64) received(id.ToUpperInvariant());
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex)
            {
                TicketsLog.For("Handoff").Warning(ex, "A ticket handoff failed");
            }
        }
    }
}
