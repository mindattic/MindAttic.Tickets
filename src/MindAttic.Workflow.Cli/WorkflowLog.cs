using MindAttic.Log;
using MindAttic.Log.Extensions;
using MindAttic.Vault.Paths;
using Serilog;
using Serilog.Core;

namespace MindAttic.Workflow.Cli;

/// <summary>
/// MindAttic.Log, the workspace's one logging schema: every workflow process writes to the standalone
/// <c>MindAttic.Log.db</c> Vault assigns MindAttic.Workflow, tagged with its <c>Component</c> (Cli, Desktop).
/// Browse it with MindAttic.Log.Reader.
/// </summary>
internal static class WorkflowLog
{
    public const string Application = "MindAttic.Workflow";

    /// <summary>Overrides the log database (tests point it at a temp file so their runs stay out of the real log).</summary>
    public const string DbEnvVar = "MINDATTIC_WORKFLOW_LOG_DB";

    public static string DbPath => Environment.GetEnvironmentVariable(DbEnvVar) is { Length: > 0 } p ? p : VaultPaths.LoggingDbPath(Application);

    public static void Start(string component) =>
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Component", component)
            .WriteToMindAtticLog(new MindAtticLogOptions { Application = Application, DbPath = DbPath })
            .CreateLogger();

    /// <summary>A logger whose entries land in the log's Category column as <paramref name="category"/>.</summary>
    public static ILogger For(string category) => Serilog.Log.ForContext(Constants.SourceContextPropertyName, category);

    public static void Stop() => Serilog.Log.CloseAndFlush();
}
