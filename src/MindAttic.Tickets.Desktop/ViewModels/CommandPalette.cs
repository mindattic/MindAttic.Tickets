using System.Collections.ObjectModel;
using MindAttic.Components.Wpf.Mvvm;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>What a command palette entry stands for.</summary>
public enum PaletteKind { Command, Ticket, Story }

/// <summary>
/// One entry in the command palette (M.TIX-T81): a command to run, or a ticket or story to jump to. <paramref name="Key"/> is
/// what an exact match is judged on (a ticket's or story's ID, a command's name); <paramref name="Title"/> and
/// <paramref name="Detail"/> are shown and searched.
/// </summary>
public sealed record PaletteItem(PaletteKind Kind, string Key, string Title, string Detail, Func<Task> Run, string? Shortcut = null)
{
    public string KindText => Kind switch { PaletteKind.Ticket => "Ticket", PaletteKind.Story => "Story", _ => "Command" };

    /// <summary>The line shown in the list: the ID before a ticket's or story's text, a command's name alone.</summary>
    public string Heading => Kind == PaletteKind.Command ? Title : $"{Key}  {Title}";

    /// <summary>What a screen reader says for the entry: its kind, its name, its detail and its shortcut (no glyphs).</summary>
    public string SpokenName =>
        $"{KindText} {(Kind == PaletteKind.Command ? Title : $"{Key}: {Title}")}" +
        (Detail.Length > 0 ? $", {Detail}" : "") + (Shortcut is { Length: > 0 } s ? $", shortcut {s}" : "");

    internal string Haystack => Kind == PaletteKind.Command ? Title : $"{Key} {Title}";
}

/// <summary>
/// The fuzzy match behind the palette: a query matches when each of its words appears in the text in order, letters may be
/// skipped ("apr" finds "Approve"); a whole-word, prefix or contiguous match, and letters at word starts, score higher.
/// </summary>
public static class Fuzzy
{
    /// <summary>The score of <paramref name="query"/> against <paramref name="text"/>, higher is better; null when it doesn't match.</summary>
    public static int? Score(string query, string text)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return 0;
        var total = 0;
        foreach (var w in words)
        {
            if (Word(w, text) is not { } s) return null;
            total += s;
        }
        return total;
    }

    private static int? Word(string word, string text)
    {
        var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            // Contiguous: best at the very start, then at a word start, then anywhere; a match that is the whole word scores more.
            var score = 1000 + word.Length * 20;
            if (at == 0) score += 400;
            else if (IsWordStart(text, at)) score += 200;
            var end = at + word.Length;
            if (end == text.Length || !char.IsLetterOrDigit(text[end])) score += 150;
            return score - Math.Min(at, 100);
        }

        // Letters in order with gaps: each letter scores, more when it follows the last one or starts a word.
        var total = 0;
        var last = -2;
        var i = 0;
        foreach (var c in word)
        {
            while (i < text.Length && char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(c)) i++;
            if (i >= text.Length) return null;
            total += 10;
            if (i == last + 1) total += 15;
            if (IsWordStart(text, i)) total += 20;
            else if (last >= 0) total -= Math.Min(i - last - 1, 10);
            last = i;
            i++;
        }
        return total;
    }

    private static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}

/// <summary>
/// The command palette (Ctrl+K, M.TIX-T81): one search box over every command, ticket and story. Typing filters and ranks them
/// fuzzily; Up/Down move the selection without leaving the box; Enter runs the selected entry. A query starting with
/// <c>&gt;</c> lists commands only, <c>#</c> tickets and stories only. With nothing typed it lists the commands.
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    /// <summary>The most entries listed at once (thousands of tickets would only slow the list and the screen reader).</summary>
    public const int MaxResults = 50;

    private readonly IReadOnlyList<PaletteItem> items;
    private string query = "";
    private PaletteItem? selected;
    private string summary = "";

    public CommandPaletteViewModel(IReadOnlyList<PaletteItem> items)
    {
        this.items = items;
        Filter();
    }

    public ObservableCollection<PaletteItem> Results { get; } = [];

    public string Query { get => query; set { if (Set(ref query, value ?? "")) Filter(); } }

    public PaletteItem? Selected
    {
        get => selected;
        set { if (Set(ref selected, value)) Raise(nameof(SelectedSpoken)); }
    }

    /// <summary>How many entries match, said as the list changes ("12 results. Approve AL-T2, selected").</summary>
    public string Summary { get => summary; private set => Set(ref summary, value); }

    /// <summary>The selected entry as a screen reader should hear it while focus stays in the search box.</summary>
    public string SelectedSpoken => Selected is { } s ? $"{s.SpokenName}, {Results.IndexOf(s) + 1} of {Results.Count}" : "";

    /// <summary>Moves the selection by <paramref name="delta"/> (Up/Down, Page Up/Down), stopping at either end.</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is { } s ? Results.IndexOf(s) : -1;
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }

    public void MoveToEnd(bool last)
    {
        if (Results.Count > 0) Selected = last ? Results[^1] : Results[0];
    }

    private void Filter()
    {
        var q = Query.Trim();
        IEnumerable<PaletteItem> pool = items;
        if (q.StartsWith('>')) { pool = pool.Where(i => i.Kind == PaletteKind.Command); q = q[1..].Trim(); }
        else if (q.StartsWith('#')) { pool = pool.Where(i => i.Kind != PaletteKind.Command); q = q[1..].Trim(); }
        else if (q.Length == 0) pool = pool.Where(i => i.Kind == PaletteKind.Command);

        var ranked = pool
            .Select((item, order) => (item, order, score: Rank(q, item)))
            .Where(x => x.score is not null)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.order)
            .Take(MaxResults)
            .Select(x => x.item)
            .ToList();

        Results.Clear();
        foreach (var r in ranked) Results.Add(r);
        Selected = Results.FirstOrDefault();
        Summary = Results.Count switch
        {
            0 => "No matches.",
            1 => "1 result.",
            MaxResults => $"First {MaxResults} results; type more to narrow them.",
            var n => $"{n} results.",
        };
    }

    /// <summary>An exact ID or command name ranks first; otherwise the fuzzy score over the ID and text.</summary>
    private static int? Rank(string q, PaletteItem item)
    {
        if (q.Length == 0) return 0;
        if (item.Key.Equals(q, StringComparison.OrdinalIgnoreCase)) return 100_000;
        return Fuzzy.Score(q, item.Haystack);
    }
}
