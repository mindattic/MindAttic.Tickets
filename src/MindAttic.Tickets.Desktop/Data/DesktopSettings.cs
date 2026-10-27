using System.IO;
using System.Text.Json;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>Per-user conveniences only (never ticket state): the last view, zoom and new-ticket choices (3.3.7 Redundant Entry).</summary>
public sealed class DesktopSettings
{
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Tickets", "desktop.json");

    // Where the settings lived before the rename to MindAttic.Tickets (M.TIX-T120): read once when there's nothing new yet.
    private static string LegacyPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Workflow", "desktop.json");

    public string View { get; set; } = "Queue";
    public double Zoom { get; set; } = 1;
    public string? LastRepo { get; set; }
    public string LastType { get; set; } = "feature";
    public bool GroupByRepo { get; set; } = true;
    public bool GroupStoriesByEpic { get; set; } = true;

    /// <summary>Epic group keys ("Repo|Key", upper-invariant) collapsed in the Stories view (M.TIX-T182); absent means expanded.</summary>
    public HashSet<string> CollapsedEpics { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Path { get; private set; }

    public static DesktopSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var read = !File.Exists(path) && path == DefaultPath && File.Exists(LegacyPath) ? LegacyPath : path;
            var s = File.Exists(read) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(read)) ?? new() : new();
            s.Path = path;
            return s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopSettings { Path = path };
        }
    }

    public void Save()
    {
        if (Path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
