using System.Diagnostics;
using System.Text;

namespace MindAttic.Tickets;

/// <summary>Result of one <c>git</c> invocation.</summary>
public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Non-empty output lines.</summary>
    public IReadOnlyList<string> Lines =>
        Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Runs <c>git -C &lt;repo&gt; …</c> with arguments passed as a list (no shell, no quoting bugs).</summary>
public static class Git
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static GitResult Run(string repo, params string[] args) => Run(repo, DefaultTimeout, args);

    public static GitResult Run(string repo, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Never block on a credential or editor prompt.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start git.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new GitResult(-1, "", $"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:0}s");
        }
        return new GitResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
