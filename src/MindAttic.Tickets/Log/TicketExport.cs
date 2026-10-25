using System.Text;

namespace MindAttic.Tickets.Log;

/// <summary>One exported ticket: what <c>tickets ticket list</c> shows, plus its approval when known.</summary>
public sealed record ExportRow(string Repo, Ticket Ticket, IReadOnlyList<string> Labels, string? Approval = null);

/// <summary>
/// A filtered ticket list as CSV or markdown (M.TIX-T87), shared by <c>tickets ticket list --format csv|md</c> and the
/// Desktop's Export for the current filter.
/// </summary>
public static class TicketExport
{
    public const string Csv = "csv", Markdown = "md";

    /// <summary>The formats <see cref="Write"/> knows, besides the CLI's own <c>table</c> and <c>json</c>.</summary>
    public static readonly IReadOnlyList<string> Formats = [Csv, Markdown];

    /// <summary>The format a file extension names (<c>.csv</c>, <c>.md</c>, <c>.markdown</c>), or null.</summary>
    public static string? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => Csv,
        ".md" or ".markdown" => Markdown,
        _ => null,
    };

    public static string Write(string format, IEnumerable<ExportRow> rows) => format switch
    {
        Csv => ToCsv(rows),
        Markdown => ToMarkdown(rows),
        _ => throw new ArgumentException($"Unknown export format '{format}' (csv or md)."),
    };

    private static string[] Header(bool approval) =>
        approval ? ["Repo", "ID", "Date", "Type", "Status", "Approval", "Summary", "Ref", "Labels"]
                 : ["Repo", "ID", "Date", "Type", "Status", "Summary", "Ref", "Labels"];

    private static string[] Cells(ExportRow r, bool approval)
    {
        var t = r.Ticket;
        var labels = string.Join(", ", r.Labels);
        return approval ? [r.Repo, t.Id, t.Date, t.Type, t.Status, r.Approval ?? "", t.Summary, t.Ref, labels]
                        : [r.Repo, t.Id, t.Date, t.Type, t.Status, t.Summary, t.Ref, labels];
    }

    /// <summary>RFC 4180 CSV: a header row, CRLF line ends, a field quoted when it holds a comma, quote or line break.</summary>
    public static string ToCsv(IEnumerable<ExportRow> rows)
    {
        var list = rows.ToList();
        var approval = list.Any(r => r.Approval is not null);
        var sb = new StringBuilder();
        void Line(IEnumerable<string> cells) => sb.Append(string.Join(",", cells.Select(CsvField))).Append("\r\n");
        Line(Header(approval));
        foreach (var r in list) Line(Cells(r, approval));
        return sb.ToString();
    }

    private static string CsvField(string s)
    {
        // A leading = + - @ would run as a formula in a spreadsheet; a quote-led apostrophe keeps it text.
        if (s.Length > 0 && s[0] is '=' or '+' or '@' or '\t' or '\r' || (s.Length > 1 && s[0] == '-' && !char.IsDigit(s[1]) && s[1] != ' '))
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>A GitHub-flavoured markdown table, pipes in a cell escaped and line breaks folded to spaces.</summary>
    public static string ToMarkdown(IEnumerable<ExportRow> rows)
    {
        var list = rows.ToList();
        var approval = list.Any(r => r.Approval is not null);
        var header = Header(approval);
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", header)).Append(" |\n");
        sb.Append('|').Append(string.Concat(header.Select(_ => "---|"))).Append('\n');
        foreach (var r in list)
            sb.Append("| ").Append(string.Join(" | ", Cells(r, approval).Select(MdCell))).Append(" |\n");
        return sb.ToString();
    }

    private static string MdCell(string s) =>
        s.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
