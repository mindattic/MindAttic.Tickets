namespace Spike.Shared;

/// <summary>A node in the demo menu tree. Leaves are "actions"; nodes with children are submenus.</summary>
public sealed record MenuNode(string Name, string Description, IReadOnlyList<MenuNode>? Children = null)
{
    public bool IsLeaf => Children is null || Children.Count == 0;
    public override string ToString() => Name;
}

/// <summary>
/// The same menu every spike renders, shaped like the Launcher's: a main menu, a long
/// project list (to exercise scrolling), and per-project actions. Two levels deep so
/// every spike has to handle Enter-into / Esc-out-of navigation.
/// </summary>
public static class DemoMenu
{
    public const string Title = "MindAttic Launcher (spike)";

    public static MenuNode Root { get; } = Build();

    /// <summary>Live status line. Every spike shows it and refreshes it once a second,
    /// standing in for the Launcher's busy/idle glyphs and 60-second redraw timer.</summary>
    public static string Status()
    {
        var spin = "|/-\\"[DateTime.Now.Second % 4];
        return $"{spin} {DateTime.Now:HH:mm:ss}  ·  3 agents busy  ·  12 repos clean";
    }

    private static MenuNode Build()
    {
        string[] repos =
        [
            "MindAttic.Launcher", "MindAttic.Workflow", "MindAttic.Browser", "MindAttic.Capture",
            "MindAttic.Deploy", "MindAttic.Vault", "MindAttic.Log", "MindAttic.Mobile",
            "MindAttic.Automata", "UnityEve", "KdpPublish", "HyperspaceGallery",
            "MindAttic.Codex", "MindAttic.HouseRules", "MindAttic.Site", "MindAttic.Notes",
            "MindAttic.Scratch", "MindAttic.Archive", "MindAttic.Tools", "MindAttic.Lab",
        ];

        var projects = repos.Select(r => new MenuNode(r, $@"D:\Projects\MindAttic\{r}",
        [
            new MenuNode("Launch agent", "open a host tab running claude"),
            new MenuNode("Commit", "stage, commit and push"),
            new MenuNode("Pull", "git pull --rebase"),
            new MenuNode("Open folder", "explorer.exe"),
        ])).ToList();

        return new MenuNode("Main", "",
        [
            new MenuNode("Open Project", $"{projects.Count} projects in the roster", projects),
            new MenuNode("Commit All", "commit and push every dirty repo"),
            new MenuNode("Pull All", "pull every repo"),
            new MenuNode("Backup", "robocopy + sqlcmd BACKUP DATABASE"),
            new MenuNode("Workflow", "tickets across the workspace"),
            new MenuNode("Settings", "providers, roster, schemes"),
        ]);
    }
}
