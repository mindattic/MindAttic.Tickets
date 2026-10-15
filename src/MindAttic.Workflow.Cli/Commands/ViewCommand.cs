using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MindAttic.Workflow.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MindAttic.Workflow.Cli.Commands;

/// <summary>
/// Writes a read-only snapshot of the database as a local site (index.html + data.js) and opens it in
/// MindAttic.Browser, which serves the folder at https://workflow.local/. See docs/VIEWER.md.
/// </summary>
public sealed class ViewCommand : Command<ViewCommand.Settings>
{
    public const string Host = "workflow.local";
    public static readonly string BrowserExe = @"C:\Apps\MindAttic.Browser\MindAttic.Browser.exe";

    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--repo <NAME>")]
        [Description("Open with this repo selected (the export always holds every repo).")]
        public string? Repo { get; init; }

        [CommandOption("--out <DIR>")]
        [Description(@"Where to write the site. Default: %LOCALAPPDATA%\MindAttic\Workflow\viewer.")]
        public string? Out { get; init; }

        [CommandOption("--no-open")]
        [Description("Write the files and print the folder; don't open anything.")]
        public bool NoOpen { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var db = Db.Open(settings);
        if (settings.Repo is { } repo && !db.Repos().Any(r => r.Name.Equals(repo, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"No repo named '{repo}' in the workflow database.");

        var dir = settings.Out ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Workflow", "viewer");
        Write(db, dir, settings.Repo);
        if (settings.NoOpen) { Console.WriteLine(dir); return 0; }

        if (File.Exists(BrowserExe))
            Process.Start(new ProcessStartInfo(BrowserExe) { ArgumentList = { dir, "--host", Host }, UseShellExecute = false });
        else
        {
            AnsiConsole.MarkupLine($"[grey]MindAttic.Browser isn't installed ({Output.Esc(BrowserExe)}); opening with the default browser.[/]");
            Process.Start(new ProcessStartInfo(Path.Combine(dir, "index.html")) { UseShellExecute = true });
        }
        AnsiConsole.MarkupLine($"Opened {Output.Esc(dir)}");
        return 0;
    }

    /// <summary>Writes index.html and data.js into <paramref name="dir"/>. Rewrites both every time.</summary>
    public static void Write(WorkflowDb db, string dir, string? selectedRepo = null)
    {
        Directory.CreateDirectory(dir);
        using (var page = typeof(ViewCommand).Assembly.GetManifestResourceStream("viewer/index.html")
            ?? throw new InvalidOperationException("viewer/index.html isn't embedded in the workflow CLI."))
        using (var file = File.Create(Path.Combine(dir, "index.html")))
            page.CopyTo(file);

        var data = new
        {
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            selectedRepo,
            repos = db.Repos().Select(r => new { name = r.Name, code = r.Code }),
            tickets = db.Tickets().Select(t => new
            {
                repo = t.Repo, id = t.Ticket.Id, date = t.Ticket.Date, type = t.Ticket.Type,
                status = t.Ticket.Status, summary = t.Ticket.Summary, @ref = t.Ticket.Ref,
            }),
            stories = db.Stories().Select(s => new
            {
                repo = s.Repo, id = s.Id, status = s.Status, glyph = s.Glyph, epic = s.Epic ?? s.Section,
                text = s.Text, evidence = s.Evidence,
            }),
        };
        // The default encoder escapes < > & ' so no ticket text can close the script or inject markup.
        File.WriteAllText(Path.Combine(dir, "data.js"), "window.WORKFLOW = " + JsonSerializer.Serialize(data) + ";\n");
    }
}
