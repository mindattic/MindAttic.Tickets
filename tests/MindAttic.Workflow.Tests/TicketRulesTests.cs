using MindAttic.Workflow.Tickets;
using NUnit.Framework;

namespace MindAttic.Workflow.Tests;

[TestFixture]
public sealed class TicketRulesTests
{
    private static Ticket T(string id, string status = "open", string summary = "s", string type = "fix", string date = "2026-10-12", string reference = "—") =>
        new(id, date, type, status, summary, reference);

    [Test]
    public void NextId_starts_at_one_and_follows_the_highest_number()
    {
        Assert.That(TicketRules.NextId("WF", []), Is.EqualTo("WF-T1"));
        Assert.That(TicketRules.NextId("WF", [T("WF-T2"), T("WF-T9"), T("WF-T3")]), Is.EqualTo("WF-T10"));
    }

    [Test]
    public void Validate_accepts_a_well_formed_table()
    {
        Assert.That(TicketRules.Validate("WF", [T("WF-T1", "closed"), T("WF-T2", "wont-do", type: "docs")]), Is.Empty);
    }

    [Test]
    public void A_family_code_with_a_dot_numbers_validates_and_parses()
    {
        Assert.That(TicketRules.NextId("M.AUTH", [T("M.AUTH-T7")]), Is.EqualTo("M.AUTH-T8"));
        Assert.That(TicketRules.ParseNumber("M.AUTH-T7"), Is.EqualTo(7));
        Assert.That(TicketRules.Validate("M.AUTH", [T("M.AUTH-T1"), T("M.AUTH-T2", "closed")]), Is.Empty);
        Assert.That(TicketRules.Validate("M.AUTH", [T("AUTH-T3")]), Has.Some.Contains("ID prefix doesn't match"));
        Assert.That(TicketRules.ParseNumber("MA.AUTH-T1"), Is.Null, "only a single-letter family goes before the dot");
    }

    [Test]
    public void Validate_flags_bad_ids_duplicates_wrong_prefix_vocabulary_and_dates()
    {
        var problems = TicketRules.Validate("WF",
        [
            T("WF-1"), T("WF-T2"), T("WF-T2"), T("XX-T3"), T("WF-T4", status: "done"), T("WF-T5", type: "bug"), T("WF-T6", date: "12/10/2026"),
        ]);

        Assert.That(problems, Has.Some.Contains("WF-1: ID is not CODE-T<n>"));
        Assert.That(problems, Has.Some.Contains("WF-T2: duplicate ID"));
        Assert.That(problems, Has.Some.Contains("XX-T3: ID prefix doesn't match"));
        Assert.That(problems, Has.Some.Contains("unknown status 'done'"));
        Assert.That(problems, Has.Some.Contains("unknown type 'bug'"));
        Assert.That(problems, Has.Some.Contains("is not YYYY-MM-DD"));
    }

    [Test]
    public void CheckAppendOnly_allows_status_and_ref_changes_and_new_rows()
    {
        var before = new[] { T("WF-T1") };
        var after = new[] { T("WF-T1", status: "closed", reference: "abc123"), T("WF-T2") };

        Assert.That(TicketRules.CheckAppendOnly(before, after), Is.Empty);
    }

    [Test]
    public void CheckAppendOnly_rejects_a_deleted_or_reworded_row()
    {
        var before = new[] { T("WF-T1"), T("WF-T2", summary: "original") };
        var after = new[] { T("WF-T2", summary: "rewritten") };

        var problems = TicketRules.CheckAppendOnly(before, after);

        Assert.That(problems, Is.EquivalentTo(new[] { "WF-T1: row was deleted", "WF-T2: row was reworded (only Status and Ref may change)" }));
    }
}
