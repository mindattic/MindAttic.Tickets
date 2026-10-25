namespace MindAttic.Tickets.Log;

/// <summary>
/// A starting description per ticket type (M.TIX-T88): a fix asks for its symptom, cause and test, a feature for its
/// story and acceptance. The hints are markdown comments, so a half-filled template still renders cleanly.
/// </summary>
public static class TicketTemplates
{
    private static readonly IReadOnlyDictionary<string, string> ByType = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fix"] = Sections(
            ("Symptom", "What goes wrong, and how to see it."),
            ("Cause", "Why it happens, once known."),
            ("Test", "The test that fails before the fix and passes after.")),
        ["feature"] = Sections(
            ("Story", "As a ..., I want ... so that ..."),
            ("Acceptance", "A checklist of what must be true for it to be done.")),
        ["chore"] = Sections(
            ("Why", "What it saves or prevents."),
            ("Done when", "A checklist of what finishes it.")),
        ["docs"] = Sections(
            ("What", "The page or section, and what it should say."),
            ("Why", "Who is missing it.")),
        ["interview"] = Sections(
            ("Questions", "What to ask, one per line."),
            ("Answers", "Filled in afterwards.")),
    };

    private static string Sections(params (string Heading, string Hint)[] sections) =>
        string.Join("\n", sections.Select(s => $"## {s.Heading}\n\n<!-- {s.Hint} -->\n"));

    /// <summary>The template for <paramref name="type"/>, or "" for a type without one.</summary>
    public static string For(string type) => ByType.TryGetValue(type, out var t) ? t : "";

    /// <summary>Whether <paramref name="description"/> is empty or some type's template left untouched: nothing worth keeping.</summary>
    public static bool IsBlank(string? description)
    {
        var d = Normalize(description);
        return d.Length == 0 || ByType.Values.Any(t => Normalize(t) == d);
    }

    /// <summary>The description to store: null when it's <see cref="IsBlank"/>.</summary>
    public static string? Keep(string? description) => IsBlank(description) ? null : description;

    private static string Normalize(string? s) => (s ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
