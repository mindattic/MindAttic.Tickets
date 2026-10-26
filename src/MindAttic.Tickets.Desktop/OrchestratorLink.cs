using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Desktop;

/// <summary>
/// The link back to the Orchestrator, which runs the work (M.TIX-T134): a ticket being worked shows its login and session as
/// links that start <c>MindAttic.Orchestrator.exe --show &lt;email|session|ticket&gt;</c> (its deep-link entry, M.ORCH-T763), the
/// mirror of the Orchestrator's ticket links into Tickets Desktop. Harmless when the Orchestrator isn't installed, or is a
/// version that doesn't take <c>--show</c> yet: nothing but a found exe and a well-formed target ever reaches a command line.
/// </summary>
public static partial class OrchestratorLink
{
    /// <summary>Set to the Orchestrator's exe to use it instead of the usual install places.</summary>
    public const string ExeEnvVar = "MINDATTIC_ORCHESTRATOR_EXE";

    /// <summary>Where the Orchestrator is installed: the variable, else C:\Apps, else its per-user install; the dashboard
    /// installs as MindAttic.Orchestrator.Desktop.exe (tried first), the old MindAttic.Orchestrator.exe is legacy.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(ExeEnvVar) is { Length: > 0 } env) yield return env;
        var perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MindAttic.Orchestrator");
        yield return @"C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.Desktop.exe";
        yield return Path.Combine(perUser, "MindAttic.Orchestrator.Desktop.exe");
        yield return @"C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.exe";
        yield return Path.Combine(perUser, "MindAttic.Orchestrator.exe");
    }

    /// <summary>The installed Orchestrator, or null.</summary>
    public static string? FindExe() => Candidates().FirstOrDefault(File.Exists);

    // An email, a ticket id or a session id (letters, digits, dots, dashes, underscores): never an option or anything with spaces.
    [GeneratedRegex(@"^(?:[^@\s-][^@\s]*@[^@\s]+\.[^@\s]+|[A-Za-z0-9][A-Za-z0-9._-]{0,127})$", RegexOptions.CultureInvariant)]
    private static partial Regex Target();

    public static bool IsTarget(string? target) => target is { Length: > 0 and <= 256 } && Target().IsMatch(target);

    /// <summary>What the Orchestrator is started with to show <paramref name="target"/>.</summary>
    public static IReadOnlyList<string> Arguments(string target) => ["--show", target];

    /// <summary>What the Orchestrator is started with to start a CLI in <paramref name="repo"/> (the Repos tab's Start link,
    /// M.TIX-T135); an Orchestrator that doesn't take <c>--start</c> just opens its window.</summary>
    public static IReadOnlyList<string> StartArguments(string repo) => ["--start", repo];

    /// <summary>Opens the Orchestrator on <paramref name="target"/> (a login's email, a session id or a ticket id); returns why it
    /// couldn't, or null once it's started.</summary>
    public static string? Open(string? target, string? exe = null)
    {
        if (!IsTarget(target)) return $"Can't show '{target}' in the Orchestrator.";
        return Launch(Arguments(target!), exe);
    }

    /// <summary>Starts the Orchestrator with <paramref name="args"/> (built by <see cref="Arguments"/> or
    /// <see cref="StartArguments"/>, each value a checked <see cref="IsTarget"/>); returns why it couldn't, or null.</summary>
    public static string? Launch(IReadOnlyList<string> args, string? exe = null)
    {
        if (args.Skip(1).Any(a => !IsTarget(a))) return $"Can't pass '{string.Join(' ', args)}' to the Orchestrator.";
        exe ??= FindExe();
        if (exe is null || !File.Exists(exe))
            return $"The Orchestrator isn't installed here (looked for {string.Join(", ", Candidates())}).";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var _ = Process.Start(psi);
            TicketsLog.For("Orchestrator").Information("Opened the Orchestrator: {Args:l}", string.Join(' ', args));
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Couldn't start the Orchestrator: {ex.Message}";
        }
    }
}
