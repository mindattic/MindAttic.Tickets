using System.Text;
using MindAttic.Workflow.Tickets;
using NUnit.Framework;

namespace MindAttic.Workflow.Tests;

[TestFixture]
public sealed class StoryFileTests
{
    private const string Sample =
        "---\n" +
        "codex: 1\n" +
        "project: Demo\n" +
        "code: DEM\n" +
        "layer: stories\n" +
        "---\n" +
        "\n" +
        "# Demo — User Stories\n" +
        "\n" +
        "## Epic A — Things\n" +
        "- **DEM-US-A1 ✅** As a Dev, I can do a thing. *(verified by `Does_the_thing`.)*\n" +
        "\n" +
        "## Tickets\n" +
        "> Permanent log.\n" +
        "\n" +
        "| ID | Date | Type | Status | Summary | Ref |\n" +
        "|---|---|---|---|---|---|\n" +
        "| DEM-T1 | 2026-10-01 | docs | closed | Start the log | — |\n" +
        "| DEM-T4 | 2026-10-02 | fix | open | Fix the a\\|b split | DEM-US-A1 |\n";

    [Test]
    public void A_lowercase_code_still_numbers_new_tickets_past_the_existing_ones()
    {
        // 'code: flow' is accepted by the front-matter but isn't the canonical upper-case form; its tickets' numbers must
        // still parse, or every Add sees them all as 0 and hands out flow-T1 again.
        var file = StoryFile.Parse(Sample.Replace("code: DEM", "code: flow").Replace("DEM-T", "flow-T"));
        Assert.That(file.Add("fix", "open", "one more", "").Id, Is.EqualTo("flow-T5"));
        Assert.That(file.Add("fix", "open", "and another", "").Id, Is.EqualTo("flow-T6"));
    }

    [Test]
    public void Add_and_close_keep_every_other_lines_ending_in_a_file_with_mixed_endings()
    {
        // One CRLF line (a Windows paste) among LF ones: only the rows Add/Close write may change.
        var mixed = Sample.Replace("## Epic A — Things\n", "## Epic A — Things\r\n");
        var file = StoryFile.Parse(mixed);
        file.Close("DEM-T4", reference: "done");
        file.Add("fix", "open", "one more", "", date: "2026-10-18");

        var expected = mixed
            .Replace("| DEM-T4 | 2026-10-02 | fix | open | Fix the a\\|b split | DEM-US-A1 |\n",
                     "| DEM-T4 | 2026-10-02 | fix | closed | Fix the a\\|b split | done |\n" +
                     "| DEM-T5 | 2026-10-18 | fix | open | one more | — |\r\n");
        Assert.That(file.Text, Is.EqualTo(expected));
    }

    private string dir = "";

    [SetUp]
    public void SetUp() => dir = Directory.CreateTempSubdirectory("wf-story-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(dir, recursive: true);

    private string Write(string text, bool bom = false)
    {
        var path = Path.Combine(dir, "USER_STORIES.md");
        File.WriteAllBytes(path, [.. (bom ? (byte[])[0xEF, 0xBB, 0xBF] : []), .. Encoding.UTF8.GetBytes(text)]);
        return path;
    }

    [Test]
    public void Parses_code_and_rows_and_unescapes_pipes()
    {
        var file = StoryFile.Parse(Sample);

        Assert.That(file.Code, Is.EqualTo("DEM"));
        Assert.That(file.Tickets.Select(t => t.Id), Is.EqualTo(new[] { "DEM-T1", "DEM-T4" }));
        Assert.That(file.Tickets[1].Summary, Is.EqualTo("Fix the a|b split"));
        Assert.That(file.TicketsSectionIsLast, Is.True);
    }

    [Test]
    public void Parses_a_family_code_with_a_dot()
    {
        var file = StoryFile.Parse(Sample.Replace("DEM", "M.DEM"));

        Assert.That(file.Code, Is.EqualTo("M.DEM"));
        Assert.That(file.Tickets.Select(t => t.Id), Is.EqualTo(new[] { "M.DEM-T1", "M.DEM-T4" }));
    }

    [Test]
    public void Add_takes_the_number_after_the_highest_existing_id_never_filling_a_gap()
    {
        var file = StoryFile.Parse(Sample);

        var t = file.Add("feature", TicketRules.Open, "New thing", "", "2026-10-12");

        Assert.That(t.Id, Is.EqualTo("DEM-T5"));
        Assert.That(file.Tickets[^1], Is.EqualTo(t));
        Assert.That(t.Ref, Is.EqualTo("—"));
    }

    [Test]
    public void Add_inserts_only_one_line_and_leaves_the_rest_of_the_file_byte_identical()
    {
        var file = StoryFile.Parse(Sample);
        file.Add("fix", TicketRules.Closed, "Pipes | in summary", "T", "2026-10-12");

        var expected = Sample + "| DEM-T5 | 2026-10-12 | fix | closed | Pipes \\| in summary | T |\n";
        Assert.That(file.Text, Is.EqualTo(expected));
    }

    [Test]
    public void Save_preserves_crlf_line_endings_and_the_bom()
    {
        var path = Write(Sample.Replace("\n", "\r\n"), bom: true);

        var file = StoryFile.Load(path);
        file.Add("chore", TicketRules.Closed, "Tidy", "", "2026-10-12");
        file.Save();

        var bytes = File.ReadAllBytes(path);
        Assert.That(bytes[..3], Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.That(text, Does.EndWith("| DEM-T5 | 2026-10-12 | chore | closed | Tidy | — |\r\n"));
        Assert.That(text.Replace("\r\n", ""), Does.Not.Contain("\n"), "every line ending stays CRLF");
    }

    [Test]
    public void Add_creates_the_tickets_section_at_the_end_when_missing()
    {
        var noTickets = Sample[..Sample.IndexOf("## Tickets", StringComparison.Ordinal)];
        var file = StoryFile.Parse(noTickets);

        var t = file.Add("docs", TicketRules.Closed, "Start the log", "", "2026-10-12");

        Assert.That(t.Id, Is.EqualTo("DEM-T1"));
        Assert.That(file.HasTicketsSection && file.TicketsSectionIsLast, Is.True);
        Assert.That(file.Text, Does.Contain("`Does_the_thing`.)*\n\n## Tickets\n"));
    }

    [Test]
    public void Add_refuses_when_the_tickets_section_is_not_last()
    {
        var file = StoryFile.Parse(Sample + "\n## Later section\nText\n");

        Assert.Throws<InvalidOperationException>(() => file.Add("fix", TicketRules.Open, "x", ""));
    }

    [Test]
    public void Add_refuses_a_file_without_a_code()
    {
        var file = StoryFile.Parse("# No front matter\n");

        Assert.Throws<InvalidOperationException>(() => file.Add("fix", TicketRules.Open, "x", ""));
    }

    [Test]
    public void Close_changes_only_status_and_ref_of_that_row()
    {
        var file = StoryFile.Parse(Sample);

        var t = file.Close("dem-t4", TicketRules.Closed, "abc1234");

        Assert.That(t, Is.EqualTo(new Ticket("DEM-T4", "2026-10-02", "fix", "closed", "Fix the a|b split", "abc1234")));
        Assert.That(file.Text, Is.EqualTo(Sample.Replace("| fix | open | Fix the a\\|b split | DEM-US-A1 |", "| fix | closed | Fix the a\\|b split | abc1234 |")));
    }

    [Test]
    public void Close_keeps_the_existing_ref_when_none_is_given_and_supports_wont_do()
    {
        var file = StoryFile.Parse(Sample);

        var t = file.Close("DEM-T4", TicketRules.WontDo);

        Assert.That(t.Status, Is.EqualTo("wont-do"));
        Assert.That(t.Ref, Is.EqualTo("DEM-US-A1"));
    }

    [Test]
    public void Close_throws_for_an_unknown_id()
    {
        Assert.Throws<KeyNotFoundException>(() => StoryFile.Parse(Sample).Close("DEM-T99"));
    }

    [Test]
    public void New_file_text_has_front_matter_and_an_empty_ticket_table_that_accepts_a_first_ticket()
    {
        var file = StoryFile.Parse(StoryFile.NewFileText("Demo", "DEM", "2026-10-12"));

        Assert.That(file.Code, Is.EqualTo("DEM"));
        Assert.That(file.Tickets, Is.Empty);
        Assert.That(file.Add("docs", TicketRules.Closed, "Start", "", "2026-10-12").Id, Is.EqualTo("DEM-T1"));
        Assert.That(file.TicketsSectionIsLast, Is.True);
    }
}
