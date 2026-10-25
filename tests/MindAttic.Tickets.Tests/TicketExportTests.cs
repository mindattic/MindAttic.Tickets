using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>Ticket export as CSV and markdown (M.TIX-T87).</summary>
public sealed class TicketExportTests
{
    private static ExportRow R(string id, string summary, string reference = "—", string[]? labels = null, string? approval = null) =>
        new("Alpha", new Ticket(id, "2026-10-01", "fix", "open", summary, reference), labels ?? [], approval);

    [Test]
    public void Csv_quotes_commas_quotes_and_line_breaks_and_defuses_formulas()
    {
        var csv = TicketExport.ToCsv([R("AL-T1", "a, \"b\"\nc", labels: ["ui", "component:desktop"]), R("AL-T2", "=SUM(A1)", "-x")]);
        Assert.That(csv, Is.EqualTo(
            "Repo,ID,Date,Type,Status,Summary,Ref,Labels\r\n" +
            "Alpha,AL-T1,2026-10-01,fix,open,\"a, \"\"b\"\"\nc\",—,\"ui, component:desktop\"\r\n" +
            "Alpha,AL-T2,2026-10-01,fix,open,'=SUM(A1),'-x,\r\n"));
    }

    [Test]
    public void Markdown_is_a_table_with_pipes_escaped_and_lines_folded()
    {
        var md = TicketExport.ToMarkdown([R("AL-T1", "a | b\nc", approval: "approved")]);
        Assert.That(md, Is.EqualTo(
            "| Repo | ID | Date | Type | Status | Approval | Summary | Ref | Labels |\n" +
            "|---|---|---|---|---|---|---|---|---|\n" +
            "| Alpha | AL-T1 | 2026-10-01 | fix | open | approved | a \\| b c | — |  |\n"));
    }

    [Test]
    public void Formats_are_picked_by_name_or_file_extension()
    {
        Assert.That(TicketExport.FormatOf("x.CSV"), Is.EqualTo(TicketExport.Csv));
        Assert.That(TicketExport.FormatOf("x.markdown"), Is.EqualTo(TicketExport.Markdown));
        Assert.That(TicketExport.FormatOf("x.txt"), Is.Null);
        Assert.That(TicketExport.Write("md", []), Does.StartWith("| Repo |"));
        Assert.That(() => TicketExport.Write("xml", []), Throws.ArgumentException);
    }
}
