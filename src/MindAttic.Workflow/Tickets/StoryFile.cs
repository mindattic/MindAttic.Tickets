using System.Text;
using System.Text.RegularExpressions;

namespace MindAttic.Workflow.Tickets;

/// <summary>
/// A repo's <c>docs/USER_STORIES.md</c>, loaded for ticket work. Edits are line-level: adding a
/// ticket inserts one row after the table's last row, and closing one rewrites only that row's
/// Status/Ref cells, so the rest of the file (stories, prose) is untouched byte-for-byte. The
/// original line endings and UTF-8 BOM (or lack of one) are preserved on save.
/// </summary>
public sealed partial class StoryFile
{
    public const string RelativePath = "docs/USER_STORIES.md";
    public const string TicketsHeading = "## Tickets";

    private const string TableSeparator = "|---|---|---|---|---|---|";

    // Each line keeps its own trailing '\r' (split on '\n' only), so a file with mixed endings saves back with every
    // untouched line's ending exactly as it was; lines this class writes use the file's prevailing ending.
    private readonly List<string> lines;
    private readonly string newline;
    private readonly bool bom;
    private readonly bool trailingNewline;

    private StoryFile(string path, string text, bool bom)
    {
        Path = path;
        this.bom = bom;
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        trailingNewline = text.EndsWith('\n');
        lines = [.. text.Split('\n')];
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);
    }

    private string Cr => newline == "\r\n" ? "\r" : "";

    /// <summary>Inserts a line written by this class, with the file's prevailing ending.</summary>
    private void InsertLine(int at, string content)
    {
        if (at == lines.Count && !trailingNewline && lines.Count > 0)
        {
            // The new line becomes the last one of a file with no final newline: the old last line now needs an ending.
            lines[^1] += Cr;
            lines.Add(content);
            return;
        }
        lines.Insert(at, content + Cr);
    }

    /// <summary>Full path of the file.</summary>
    public string Path { get; }

    /// <summary>The <c>code:</c> front-matter value, or null when the file has none.</summary>
    public string? Code
    {
        get
        {
            if (lines.Count == 0 || lines[0].Trim() != "---") return null;
            for (var i = 1; i < lines.Count && lines[i].Trim() != "---"; i++)
            {
                var m = FrontMatterCode().Match(lines[i]);
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }
    }

    /// <summary>True when the file has a <c>## Tickets</c> section.</summary>
    public bool HasTicketsSection => HeadingIndex() >= 0;

    /// <summary>True when <c>## Tickets</c> is the last <c>##</c> section, as the standard requires.</summary>
    public bool TicketsSectionIsLast
    {
        get
        {
            var h = HeadingIndex();
            if (h < 0) return false;
            for (var i = h + 1; i < lines.Count; i++)
                if (lines[i].StartsWith("## ", StringComparison.Ordinal) || lines[i].StartsWith("# ", StringComparison.Ordinal))
                    return false;
            return true;
        }
    }

    /// <summary>Every row of the ticket table, in file order.</summary>
    public IReadOnlyList<Ticket> Tickets => [.. RowIndexes().Select(i => ParseRow(lines[i])!)];

    /// <summary>The file as it would be saved (with original line endings, without the BOM).</summary>
    public string Text
    {
        get
        {
            var body = string.Join("\n", lines);
            return trailingNewline ? body + "\n" : body;
        }
    }

    public static StoryFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return new StoryFile(path, text, bom);
    }

    /// <summary>Parses text that isn't on disk (e.g. the committed version from <c>git show</c>).</summary>
    public static StoryFile Parse(string text, string path = RelativePath) =>
        new(path, text.StartsWith('﻿') ? text[1..] : text, text.StartsWith('﻿'));

    public void Save() => File.WriteAllBytes(Path, [.. (bom ? (byte[])[0xEF, 0xBB, 0xBF] : []), .. new UTF8Encoding(false).GetBytes(Text)]);

    /// <summary>
    /// Appends a new ticket with the next free ID and returns it. Adds the <c>## Tickets</c>
    /// section at the end of the file first if it's missing. Refuses when the section exists but
    /// isn't last, since appending there would bury rows mid-document.
    /// </summary>
    public Ticket Add(string type, string status, string summary, string reference, string? date = null)
    {
        var code = Code ?? throw new InvalidOperationException($"{Path} has no 'code:' front-matter, so ticket IDs can't be assigned.");
        if (!TicketRules.Types.Contains(type)) throw new ArgumentException($"Unknown type '{type}' (expected {string.Join("/", TicketRules.Types)}).", nameof(type));
        if (!TicketRules.Statuses.Contains(status)) throw new ArgumentException($"Unknown status '{status}' (expected {string.Join("/", TicketRules.Statuses)}).", nameof(status));
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("A ticket needs a summary.", nameof(summary));

        if (!HasTicketsSection) AppendTicketsSection();
        else if (!TicketsSectionIsLast) throw new InvalidOperationException($"{Path}: '{TicketsHeading}' must be the last section; move it to the end first.");

        var ticket = new Ticket(TicketRules.NextId(code, Tickets), date ?? TicketRules.Today(), type, status,
            summary.Trim(), string.IsNullOrWhiteSpace(reference) ? "—" : reference.Trim());

        var rows = RowIndexes();
        var insertAt = rows.Count > 0 ? rows[^1] + 1 : SeparatorIndex() + 1;
        InsertLine(insertAt, FormatRow(ticket));
        return ticket;
    }

    /// <summary>Moves a ticket to <paramref name="status"/> (closed or wont-do), optionally setting Ref.</summary>
    public Ticket Close(string id, string status = TicketRules.Closed, string? reference = null)
    {
        if (status is not (TicketRules.Closed or TicketRules.WontDo))
            throw new ArgumentException($"Close to '{TicketRules.Closed}' or '{TicketRules.WontDo}', not '{status}'.", nameof(status));

        foreach (var i in RowIndexes())
        {
            var t = ParseRow(lines[i])!;
            if (!string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            var updated = t with { Status = status, Ref = string.IsNullOrWhiteSpace(reference) ? t.Ref : reference.Trim() };
            lines[i] = FormatRow(updated) + (lines[i].EndsWith('\r') ? "\r" : "");
            return updated;
        }
        throw new KeyNotFoundException($"{Path} has no ticket {id}.");
    }

    /// <summary>The text of a brand-new USER_STORIES.md: front-matter, an empty backlog and an empty ticket table.</summary>
    public static string NewFileText(string project, string code, string date) =>
        $"""
        ---
        codex: 1
        project: {project}
        code: {code}
        layer: stories
        status: living
        updated: {date}
        ---

        # {project} — User Stories
        > ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.

        ## Priority backlog
        _Nothing recorded yet._

        {TicketsBlock}
        """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private const string TicketsBlock =
        """
        ## Tickets
        > Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
        > delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
        > a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

        | ID | Date | Type | Status | Summary | Ref |
        |---|---|---|---|---|---|
        """;

    private void AppendTicketsSection()
    {
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        InsertLine(lines.Count, "");
        foreach (var l in TicketsBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) InsertLine(lines.Count, l);
    }

    private int HeadingIndex() => lines.FindIndex(l => l.TrimEnd() == TicketsHeading);

    private int SeparatorIndex()
    {
        var h = HeadingIndex();
        if (h < 0) return -1;
        for (var i = h + 1; i < lines.Count; i++)
            if (lines[i].TrimStart().StartsWith("|---", StringComparison.Ordinal)) return i;
        throw new InvalidOperationException($"{Path}: '{TicketsHeading}' has no table separator row ({TableSeparator}).");
    }

    /// <summary>Line indexes of the table's data rows (after the separator, until the first non-table line).</summary>
    private List<int> RowIndexes()
    {
        var result = new List<int>();
        if (HeadingIndex() < 0) return result;
        for (var i = SeparatorIndex() + 1; i < lines.Count && lines[i].TrimStart().StartsWith('|'); i++)
            if (ParseRow(lines[i]) is not null) result.Add(i);
        return result;
    }

    private static Ticket? ParseRow(string line)
    {
        var cells = SplitCells(line);
        if (cells.Count < 6) return null;
        return new Ticket(cells[0], cells[1], cells[2], cells[3], cells[4], string.Join(" | ", cells.Skip(5)));
    }

    /// <summary>Splits a markdown table row on unescaped pipes and unescapes <c>\|</c> in each cell.</summary>
    internal static List<string> SplitCells(string line)
    {
        var s = line.Trim();
        if (s.StartsWith('|')) s = s[1..];
        if (s.EndsWith('|') && !s.EndsWith("\\|", StringComparison.Ordinal)) s = s[..^1];

        var cells = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|') { sb.Append('|'); i++; }
            else if (s[i] == '|') { cells.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(s[i]);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    private static string Escape(string cell) => cell.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|", StringComparison.Ordinal);

    internal static string FormatRow(Ticket t) =>
        $"| {Escape(t.Id)} | {Escape(t.Date)} | {Escape(t.Type)} | {Escape(t.Status)} | {Escape(t.Summary)} | {Escape(t.Ref)} |";

    [GeneratedRegex(@"^code:\s*((?:[A-Za-z]\.)?[A-Za-z0-9]+)\s*$")]
    private static partial Regex FrontMatterCode();
}
