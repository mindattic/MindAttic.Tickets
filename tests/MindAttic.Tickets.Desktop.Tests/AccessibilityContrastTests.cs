using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// M.TIX-T71: WCAG 2.2 AA contrast (1.4.3 text, 1.4.11 non-text) for every colour pair Tickets Desktop's views
/// actually put together, read straight out of Theme/Colors.xaml (the comment at its top names this test as the
/// reason the file's values must hold). Desktop only consumes MindAttic.Components.Wpf as a NuGet package, with no
/// Tokens package to reuse, so this reads and computes contrast itself rather than depending on Components' types,
/// following the same relative-luminance math as MindAttic.Components.Wpf.Tests.MaThemeTests and
/// MindAttic.Components.Tokens.Tests.MaPaletteTests.
/// </summary>
public sealed class AccessibilityContrastTests
{
    // The text a view ever lays over a ground (MainWindow.xaml, NewTicketWindow.xaml, PromptWindow.xaml,
    // Theme/Styles.xaml): needs 4.5:1 (1.4.3).
    private static readonly string[] TextTokens =
        ["MaText", "MaMuted", "MaRec", "StateApproved", "StateProposed", "StateRejected", "StateOpen", "StateClosed", "StateWontDo"];

    // Borders, focus rings and other marks that carry meaning without a word next to them: needs 3:1 (1.4.11).
    // MaChromeBorder is left out on purpose: Colors.xaml documents it as the one decorative separator (a faint card
    // or row edge that is never the only way a control's boundary is shown; MaControlBorder, FieldBorder and
    // MaFocus carry that job), which 1.4.11 does not apply to.
    private static readonly string[] MarkTokens = ["MaControlBorder", "FieldBorder", "MaFocus"];

    // Every background a chip, row, card, field or window itself paints behind the tokens above. MaChanged is its
    // own narrow case (a version-changed highlight that only ever carries MaText and a StateProposed edge) and is
    // covered by its own tests below instead of this general cross-product.
    private static readonly string[] Grounds = ["MaChrome", "MaPane", "MaSurface", "MaHover"];

    private static readonly Dictionary<string, (byte R, byte G, byte B)> Colors = Load();

    private static Dictionary<string, (byte, byte, byte)> Load()
    {
        var root = AppContext.BaseDirectory;
        while (!Path.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repo root above the tests");
        var xaml = File.ReadAllText(Path.Combine(root, "src", "MindAttic.Tickets.Desktop", "Theme", "Colors.xaml"));

        var map = new Dictionary<string, (byte, byte, byte)>();
        foreach (Match m in Regex.Matches(xaml, @"<SolidColorBrush x:Key=""(\w+)"" Color=""#FF([0-9A-Fa-f]{6})"" />"))
        {
            var hex = m.Groups[2].Value;
            map[m.Groups[1].Value] = (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        return map;
    }

    private static double Luminance(string key)
    {
        Assert.That(Colors, Does.ContainKey(key), $"Theme/Colors.xaml no longer defines {key}.");
        var (r, g, b) = Colors[key];
        static double Channel(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static double Ratio(string fore, string back)
    {
        var (lf, lb) = (Luminance(fore), Luminance(back));
        return (Math.Max(lf, lb) + 0.05) / (Math.Min(lf, lb) + 0.05);
    }

    private static void AtLeast(double needed, string fore, string back)
    {
        var ratio = Ratio(fore, back);
        Assert.That(ratio, Is.GreaterThanOrEqualTo(needed), $"{fore} on {back} is {ratio:F2}:1, needs {needed}:1.");
    }

    [TestCaseSource(nameof(Grounds))]
    public void Text_meets_aa_on_every_background_it_appears_on(string ground)
    {
        foreach (var text in TextTokens) AtLeast(4.5, text, ground);
    }

    [TestCaseSource(nameof(Grounds))]
    public void Borders_and_focus_rings_meet_the_non_text_minimum_on_every_background_they_draw_over(string ground)
    {
        foreach (var mark in MarkTokens) AtLeast(3.0, mark, ground);
    }

    [Test]
    public void The_version_changed_highlight_keeps_its_text_and_edge_readable()
    {
        AtLeast(4.5, "MaText", "MaChanged");
        AtLeast(3.0, "StateProposed", "MaChanged");
    }

    [Test]
    public void Text_on_a_filled_accent_button_meets_aa()
    {
        AtLeast(4.5, "MaOnAccent", "MaAccent");
        AtLeast(4.5, "MaOnAccent", "MaAccentHover");
    }
}
