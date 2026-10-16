using System.Text;
using System.Text.RegularExpressions;
using MindAttic.Workflow.Tickets;

namespace MindAttic.Workflow.Stories;

/// <summary>What a run of lines in a USER_STORIES.md is.</summary>
public enum BlockKind
{
    /// <summary>Anything without structure of its own (front-matter, headings, legends, prose, table headers): kept verbatim.</summary>
    Raw,
    /// <summary>An <c>## Epic X — title</c> heading.</summary>
    Epic,
    /// <summary>One story: a bullet (with its wrapped lines), a sub-story bullet, or a story table row.</summary>
    Story,
    /// <summary>One numbered item of the priority backlog (with its wrapped lines).</summary>
    BacklogItem,
    /// <summary>The <c>## Tickets</c> heading, legend and table header; the rows are rendered from the tickets.</summary>
    Tickets,
}

/// <summary>A run of lines; <see cref="Text"/> is the lines joined with <c>\n</c>, exactly as they were.</summary>
public sealed record Block(BlockKind Kind, string Text)
{
    public Epic? Epic { get; init; }
    public StoryItem? Story { get; init; }
    public BacklogItem? Backlog { get; init; }
}

public sealed record Epic(string Key, string Title, string? Anchor);

/// <summary>A story with what the markdown says about it. <see cref="Status"/> is done/partial/planned from its glyph.</summary>
public sealed record StoryItem(string Id, string? Status, string? Glyph, string Suffix, string Text, string Source)
{
    /// <summary>Index (in the document's story list) of the story this one is nested under; null at the top level.</summary>
    public int? Parent { get; init; }
    public string? Epic { get; init; }
    public string? Section { get; init; }
    public string? SectionRef { get; init; }
    public string? Evidence { get; init; }
    public string? Acceptance { get; init; }
}

public sealed record BacklogItem(string Text, IReadOnlyList<string> StoryIds);

/// <summary>A ticket row as found, with its exact markdown so an untouched row renders byte-for-byte.</summary>
public sealed record TicketRow(Ticket Ticket, string Markdown);

/// <summary>
/// A whole <c>docs/USER_STORIES.md</c> split into ordered <see cref="Block"/>s: every line lands in exactly one
/// block, so <see cref="Render"/> gives back the file byte-for-byte, while epics, stories, backlog items and
/// tickets are also parsed into fields. Front-matter keys, the BOM and the line endings are kept too.
/// </summary>
public sealed partial class StoryDocument
{
    public IReadOnlyList<Block> Blocks { get; }
    public IReadOnlyList<TicketRow> Tickets { get; }
    public IReadOnlyDictionary<string, string> FrontMatter { get; }
    public string? Title { get; }
    public bool Bom { get; }
    public bool Crlf { get; }
    public bool TrailingNewline { get; }

    public string? Code => FrontMatter.GetValueOrDefault("code");

    /// <summary>The stories in file order (a story's <see cref="StoryItem.Parent"/> indexes this list).</summary>
    public IReadOnlyList<StoryItem> Stories => [.. Blocks.Where(b => b.Story is not null).Select(b => b.Story!)];

    private StoryDocument(List<Block> blocks, List<TicketRow> tickets, Dictionary<string, string> front, string? title, bool bom, bool crlf, bool trailing)
    {
        Blocks = blocks;
        Tickets = tickets;
        FrontMatter = front;
        Title = title;
        Bom = bom;
        Crlf = crlf;
        TrailingNewline = trailing;
    }

    public static StoryDocument Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static StoryDocument FromBytes(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Parse(new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    public static StoryDocument Parse(string text, bool bom = false)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var trailing = text.EndsWith('\n');
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (trailing) lines.RemoveAt(lines.Count - 1);
        return new Parser(lines).Run(bom, crlf, trailing);
    }

    /// <summary>The file as bytes: BOM and line endings as they were.</summary>
    public byte[] RenderBytes(IEnumerable<string>? ticketRows = null) => Encode(Render(ticketRows), Bom, Crlf);

    /// <summary>
    /// The file's text with <c>\n</c> line endings. Ticket rows come from <paramref name="ticketRows"/> when given
    /// (rendered from the database), otherwise from the rows as parsed.
    /// </summary>
    public string Render(IEnumerable<string>? ticketRows = null) =>
        Join(Blocks.Select(b => b.Text), ticketRows ?? Tickets.Select(t => t.Markdown), Blocks.Select(b => b.Kind), TrailingNewline);

    /// <summary>Joins block texts (a Tickets block followed by its rows) into a file's text.</summary>
    public static string Join(IEnumerable<string> blockTexts, IEnumerable<string> ticketRows, IEnumerable<BlockKind> kinds, bool trailingNewline)
    {
        var rows = ticketRows.ToList();
        var parts = new List<string>();
        foreach (var (text, kind) in blockTexts.Zip(kinds))
        {
            parts.Add(text);
            if (kind == BlockKind.Tickets) parts.AddRange(rows);
        }
        var body = string.Join("\n", parts);
        return trailingNewline ? body + "\n" : body;
    }

    public static byte[] Encode(string text, bool bom, bool crlf)
    {
        var body = new UTF8Encoding(false).GetBytes(crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text);
        return bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    public static string? StatusOf(string? glyph) => glyph switch
    {
        "✅" => "done",
        "🟡" => "partial",
        "⬜" => "planned",
        _ => null,
    };

    public static string GlyphOf(string status) => status switch
    {
        "done" => "✅",
        "partial" => "🟡",
        "planned" => "⬜",
        _ => throw new ArgumentException($"Unknown story status '{status}' (done, partial or planned).", nameof(status)),
    };

    [GeneratedRegex(@"^## Epic ([A-Za-z0-9]+)\s*[—–:-]\s*(.*?)(?:\s*\{#([^}]+)\})?\s*$")]
    internal static partial Regex EpicHeading();

    // - **CM-US-A3 🟡** text…   /   - **M.LOGS-US-D1 ✅ (retired)** text…   /   (indented) - **H1a ✅** text…
    [GeneratedRegex(@"^(?<indent>\s*)- \*\*(?<id>[A-Za-z0-9][A-Za-z0-9.\-]*) (?<glyph>✅|🟡|⬜)(?<suffix>[^*]*)\*\*(?<rest>.*)$")]
    internal static partial Regex StoryBullet();

    [GeneratedRegex(@"^\s*\d+\.\s")]
    private static partial Regex BacklogNumber();

    [GeneratedRegex(@"^(?:(?:[A-Z]\.)?[A-Z][A-Z0-9]*-)?US-[A-Za-z0-9]+$")]
    private static partial Regex TableStoryId();

    [GeneratedRegex(@"\b(?:[A-Z]\.)?[A-Z][A-Z0-9]*-US-[A-Za-z0-9]+(?:-[A-Za-z0-9]+)?")]
    internal static partial Regex StoryIdRef();

    [GeneratedRegex(@"\*\((?<e>[^*]*?)\)\*")]
    private static partial Regex Evidence();

    [GeneratedRegex(@"\*Acceptance:\s*(?<a>[^*]*)\*")]
    private static partial Regex Acceptance();

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*):\s*(.*?)\s*$")]
    private static partial Regex FrontMatterLine();

    private sealed class Parser(List<string> lines)
    {
        private readonly List<Block> blocks = [];
        private readonly List<TicketRow> tickets = [];
        private readonly Dictionary<string, string> front = new(StringComparer.Ordinal);
        private readonly List<string> raw = [];
        private string? title, section, epic;
        private bool inBacklog;
        private int storyCount;
        private int? lastTopStory;

        public StoryDocument Run(bool bom, bool crlf, bool trailing)
        {
            var i = 0;
            if (lines.Count > 0 && lines[0].Trim() == "---")
            {
                var end = lines.FindIndex(1, l => l.Trim() == "---");
                if (end > 0)
                {
                    for (var j = 1; j < end; j++)
                        if (FrontMatterLine().Match(lines[j]) is { Success: true } m) front[m.Groups[1].Value] = m.Groups[2].Value;
                    raw.AddRange(lines[..(end + 1)]);
                    i = end + 1;
                }
            }

            while (i < lines.Count)
            {
                var line = lines[i];
                if (line.TrimEnd() == StoryFile.TicketsHeading) { i = TicketSection(i); continue; }
                if (line.StartsWith("# ", StringComparison.Ordinal)) { title ??= line[2..].Trim(); raw.Add(line); i++; continue; }
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (EpicHeading().Match(line) is { Success: true } em)
                    {
                        epic = em.Groups[1].Value;
                        section = null;
                        inBacklog = false;
                        Add(new Block(BlockKind.Epic, line) { Epic = new Epic(epic, em.Groups[2].Value.Trim(), em.Groups[3].Success ? em.Groups[3].Value : null) });
                    }
                    else
                    {
                        section = line[3..].Trim();
                        epic = null;
                        inBacklog = section.StartsWith("Priority backlog", StringComparison.OrdinalIgnoreCase);
                        raw.Add(line);
                    }
                    i++;
                    continue;
                }
                if (StoryBullet().Match(line) is { Success: true } sm) { i = StoryBlock(i, sm); continue; }
                if (inBacklog && BacklogNumber().IsMatch(line)) { i = BacklogBlock(i); continue; }
                if (line.TrimStart().StartsWith('|') && TableStory(line) is { } row) { Add(new Block(BlockKind.Story, line) { Story = row }); i++; continue; }
                raw.Add(line);
                i++;
            }
            Flush();
            return new StoryDocument(blocks, tickets, front, title, bom, crlf, trailing);
        }

        private void Flush()
        {
            if (raw.Count == 0) return;
            blocks.Add(new Block(BlockKind.Raw, string.Join("\n", raw)));
            raw.Clear();
        }

        private void Add(Block b)
        {
            Flush();
            blocks.Add(b);
        }

        /// <summary>A story bullet and its wrapped lines (indented deeper than the bullet, up to a blank line or the next bullet).</summary>
        private int StoryBlock(int i, Match m)
        {
            var indent = m.Groups["indent"].Value.Length;
            var text = new List<string> { lines[i] };
            var j = i + 1;
            while (j < lines.Count && lines[j].Trim().Length > 0 && Indent(lines[j]) > indent && !StoryBullet().IsMatch(lines[j]))
                text.Add(lines[j++]);

            var body = string.Join("\n", text.Skip(1).Select(l => l.Trim()).Prepend(m.Groups["rest"].Value.Trim())).Trim();
            var nested = indent > 0 && lastTopStory is not null;
            var glyph = m.Groups["glyph"].Value;
            var story = new StoryItem(m.Groups["id"].Value, StatusOf(glyph), glyph, m.Groups["suffix"].Value.Trim(), body,
                nested ? "sub-bullet" : inBacklog ? "backlog" : "bullet")
            {
                Parent = nested ? lastTopStory : null,
                Epic = epic,
                Section = section,
                Evidence = LastEvidence(body),
                Acceptance = Acceptance().Match(body) is { Success: true } a ? a.Groups["a"].Value.Trim() : null,
            };
            if (!nested) lastTopStory = storyCount;
            storyCount++;
            Add(new Block(BlockKind.Story, string.Join("\n", text)) { Story = story });
            return j;
        }

        private int BacklogBlock(int i)
        {
            var text = new List<string> { lines[i] };
            var j = i + 1;
            while (j < lines.Count && lines[j].Trim().Length > 0 && char.IsWhiteSpace(lines[j][0]) && !StoryBullet().IsMatch(lines[j]))
                text.Add(lines[j++]);
            var all = string.Join("\n", text);
            var refs = StoryIdRef().Matches(all).Select(x => x.Value).Distinct(StringComparer.Ordinal).ToList();
            Add(new Block(BlockKind.BacklogItem, all) { Backlog = new BacklogItem(string.Join(" ", text.Select(l => l.Trim())), refs) });
            return j;
        }

        private StoryItem? TableStory(string line)
        {
            var cells = StoryFile.SplitCells(line);
            if (cells.Count < 4 || !TableStoryId().IsMatch(cells[0])) return null;
            var (story, sec, statusCell, evidence) = cells.Count >= 5 ? (cells[1], cells[2], cells[3], cells[4]) : (cells[1], (string?)null, cells[2], cells[3]);
            var glyph = new[] { "✅", "🟡", "⬜" }.FirstOrDefault(g => statusCell.Contains(g, StringComparison.Ordinal));
            var suffix = glyph is null ? statusCell : statusCell.Replace(glyph, "", StringComparison.Ordinal).Trim();
            storyCount++;
            lastTopStory = storyCount - 1;
            return new StoryItem(cells[0], StatusOf(glyph), glyph, suffix, story, "table")
            {
                Epic = epic, Section = section, SectionRef = string.IsNullOrWhiteSpace(sec) ? null : sec, Evidence = evidence,
            };
        }

        /// <summary>The <c>## Tickets</c> heading through the table separator becomes one block; each row a <see cref="TicketRow"/>.</summary>
        private int TicketSection(int i)
        {
            Flush();
            var head = new List<string>();
            var j = i;
            while (j < lines.Count)
            {
                head.Add(lines[j]);
                if (lines[j].TrimStart().StartsWith("|---", StringComparison.Ordinal)) { j++; break; }
                j++;
            }
            blocks.Add(new Block(BlockKind.Tickets, string.Join("\n", head)));
            while (j < lines.Count && lines[j].TrimStart().StartsWith('|'))
            {
                var cells = StoryFile.SplitCells(lines[j]);
                if (cells.Count < 6) break;
                tickets.Add(new TicketRow(new Ticket(cells[0], cells[1], cells[2], cells[3], cells[4], string.Join(" | ", cells.Skip(5))), lines[j]));
                j++;
            }
            return j;
        }

        private static int Indent(string line) => line.Length - line.TrimStart().Length;

        private static string? LastEvidence(string body) =>
            Evidence().Matches(body) is { Count: > 0 } ms ? ms[^1].Groups["e"].Value.Trim() : null;
    }
}
