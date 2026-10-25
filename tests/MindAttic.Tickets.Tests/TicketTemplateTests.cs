using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>Ticket templates per type and duplicate detection on summary similarity (M.TIX-T88).</summary>
public sealed class TicketTemplateTests
{
    [Test]
    public void Every_type_has_a_template_and_fix_and_feature_ask_for_what_the_story_names()
    {
        Assert.That(TicketRules.Types.Select(TicketTemplates.For), Has.All.Not.Empty);
        Assert.That(TicketTemplates.For("fix"), Does.Contain("## Symptom").And.Contain("## Cause").And.Contain("## Test"));
        Assert.That(TicketTemplates.For("feature"), Does.Contain("## Story").And.Contain("## Acceptance"));
        Assert.That(TicketTemplates.For("nonsense"), Is.Empty);
    }

    [Test]
    public void An_untouched_template_is_blank_and_not_kept_but_a_filled_one_is()
    {
        Assert.That(TicketTemplates.IsBlank(null) && TicketTemplates.IsBlank("  \n"), Is.True);
        Assert.That(TicketTemplates.Keep(TicketTemplates.For("fix").Replace("\n", "\r\n") + "\r\n"), Is.Null, "line endings and trailing space don't count");
        var filled = TicketTemplates.For("fix").Replace("## Cause\n", "## Cause\n\nA race on save.\n");
        Assert.That(TicketTemplates.Keep(filled), Is.EqualTo(filled));
    }

    [Test]
    public void Score_ignores_case_plurals_and_common_words()
    {
        Assert.That(TicketSimilarity.Score("The Desktop crashes on save", "desktop crashes when save"), Is.EqualTo(1.0));
        Assert.That(TicketSimilarity.Score("Labels for tickets", "label ticket"), Is.EqualTo(1.0));
        Assert.That(TicketSimilarity.Score("crashes, classes, boxes", "crash class box"), Is.EqualTo(1.0));
        Assert.That(TicketSimilarity.Score("export to csv", "import from json"), Is.Zero);
        Assert.That(TicketSimilarity.Score("the", "a"), Is.Zero, "nothing significant to compare");
    }

    [Test]
    public void Similar_finds_open_alike_tickets_most_alike_first()
    {
        Ticket T(string id, string status, string summary) => new(id, "2026-10-01", "fix", status, summary, "");
        var candidates = new[]
        {
            ("A", T("AA-T1", TicketRules.Open, "Desktop crashes saving a ticket description")),
            ("A", T("AA-T2", TicketRules.Open, "Desktop crashes saving a ticket")),
            ("A", T("AA-T3", TicketRules.Closed, "Desktop crashes saving a ticket")),
            ("A", T("AA-T4", TicketRules.Open, "Add a dark theme to the board")),
        };

        var found = TicketSimilarity.Similar("desktop crash when saving tickets", candidates);

        Assert.That(found.Select(s => s.Ticket.Id), Is.EqualTo(new[] { "AA-T2", "AA-T1" }), "closed and unrelated ones are left out");
        Assert.That(found[0].Score, Is.GreaterThan(found[1].Score));
        Assert.That(TicketSimilarity.Similar("", candidates), Is.Empty);
    }
}
