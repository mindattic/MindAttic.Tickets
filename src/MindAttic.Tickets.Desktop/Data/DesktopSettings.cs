using System.IO;
using System.Text.Json;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Tickets.Desktop.Data;

/// <summary>
/// Per-user conveniences only (never ticket state): the last view, zoom and new-ticket choices (3.3.7 Redundant Entry).
/// A per-machine MindAttic Vault settings app in <c>%LOCALAPPDATA%\MindAttic\Tickets\settings.json</c>, so the
/// defaults can be set in MindAttic.Vault.Desktop; the app keeps remembering your last choices into it.
/// </summary>
[SettingsApp(AppId, "Tickets Desktop", Local = true, Icon = "ticket",
    Description = "Where Tickets Desktop starts and what a new ticket defaults to. It also remembers your last choices here.")]
public sealed class DesktopSettings
{
    public const string AppId = "Tickets";

    /// <summary>The pre-Vault file, imported once.</summary>
    public static string LegacyPath => System.IO.Path.Combine(VaultPaths.LocalApp(AppId), "desktop.json");

    // Where the settings lived before the rename to MindAttic.Tickets (M.TIX-T120).
    private static string OlderLegacyPath => System.IO.Path.Combine(VaultPaths.LocalApp("Workflow"), "desktop.json");

    [Setting("Start in view", Group = "Window", Order = 1, Help = "The view Tickets Desktop opens on. It updates to the last view you used.")]
    [SettingChoices("Queue", "Repos", "Board", "Stories", "Activity", "Stats", "Audit")]
    public string View { get; set; } = "Queue";

    [Setting("Zoom", Group = "Window", Order = 2, Help = "Text and layout scale, 1 = 100%.")]
    [SettingRange(0.5, 3)]
    public double Zoom { get; set; } = 1;

    [Setting("Group tickets by repo", Group = "Lists", Order = 1)]
    public bool GroupByRepo { get; set; } = true;

    [Setting("Group stories by epic", Group = "Lists", Order = 2)]
    public bool GroupStoriesByEpic { get; set; } = true;

    [Setting("New ticket repo", Group = "New tickets", Order = 1, Help = "Repo a new ticket starts on. It updates to the last one you used.")]
    public string? LastRepo { get; set; }

    [Setting("New ticket type", Group = "New tickets", Order = 2, Help = "Type a new ticket starts as. It updates to the last one you used.")]
    [SettingChoices("feature", "fix", "chore", "docs", "interview")]
    public string LastType { get; set; } = "feature";

    /// <summary>Epic group keys ("Repo|Key", upper-invariant) collapsed in the Stories view (M.TIX-T182); absent means expanded.</summary>
    [SettingIgnore]
    public HashSet<string> CollapsedEpics { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Path => store?.FilePath;

    private JsonSettingsStore<DesktopSettings>? store;

    /// <summary>
    /// The settings. With no <paramref name="path"/>: the Vault file, importing <c>desktop.json</c> once (from the
    /// old Workflow folder if that's all there is). With a path: that file (tests, portable runs).
    /// </summary>
    public static DesktopSettings Load(string? path = null)
    {
        var target = path is null
            ? JsonSettingsStore<DesktopSettings>.ForSettingsApp()
            : new JsonSettingsStore<DesktopSettings>(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, System.IO.Path.GetFileName(path));
        if (path is null && !target.Exists())
        {
            var legacy = File.Exists(LegacyPath) ? LegacyPath : File.Exists(OlderLegacyPath) ? OlderLegacyPath : null;
            if (ReadLegacy(legacy) is { } imported)
            {
                try { target.Save(imported); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        var settings = path is null ? target.LoadOrSeed() : target.Load();
        settings.store = target;
        return settings;
    }

    private static DesktopSettings? ReadLegacy(string? path)
    {
        if (path is null) return null;
        try { return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save()
    {
        if (store is null) return;
        try { store.Save(this); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
