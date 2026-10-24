using System.Windows;
using System.Windows.Media;

namespace MindAttic.Tickets.Desktop.Theme;

/// <summary>
/// Windows high-contrast themes: every colour token from Colors.xaml is replaced, at the application root (which wins
/// over the merged dictionaries), by the user's system colour for that role, and put back when high contrast is turned
/// off. Views use DynamicResource, so this happens live, with no restart.
/// </summary>
public static class HighContrast
{
    private static readonly string[] Backgrounds = ["MaChrome", "MaPane", "MaSurface", "MaHover", "MaChanged"];
    private static readonly string[] Foregrounds =
    [
        "MaText", "MaMuted", "MaRec", "MaChromeBorder", "MaControlBorder", "FieldBorder",
        "StateApproved", "StateProposed", "StateRejected", "StateOpen", "StateClosed", "StateWontDo",
    ];
    private static readonly string[] Accents = ["MaAccent", "MaAccentHover", "MaFocus"];

    public static IEnumerable<string> Keys => [.. Backgrounds, .. Foregrounds, .. Accents, "MaOnAccent"];

    /// <summary>Applies the current system setting (or <paramref name="force"/>) and follows later changes.</summary>
    public static void Track(Application app, bool force = false)
    {
        Apply(app, force || SystemParameters.HighContrast);
        if (force) return;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                app.Dispatcher.Invoke(() => Apply(app, SystemParameters.HighContrast));
        };
    }

    public static void Apply(Application app, bool on)
    {
        var r = app.Resources;
        if (!on)
        {
            foreach (var k in Keys) r.Remove(k);
            return;
        }
        foreach (var k in Backgrounds) r[k] = SystemColors.WindowBrush;
        foreach (var k in Foregrounds) r[k] = SystemColors.WindowTextBrush;
        foreach (var k in Accents) r[k] = SystemColors.HighlightBrush;
        r["MaOnAccent"] = SystemColors.HighlightTextBrush;
    }

    /// <summary>A brush token's current value, for code that can't use a resource reference.</summary>
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
