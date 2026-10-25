using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The ticket query language and saved filters (M.TIX-T78).</summary>
public sealed class TicketQueryTests
{
    private static Ticket T(string id, string type = "fix", string status = "open", string summary = "something", string reference = "—") =>
        new(id, "2026-10-01", type, status, summary, reference);

    private static bool Match(string query, TicketFacts t) => TicketQuery.Parse(query).Matches(t);

    [Test]
    public void Fields_free_text_quotes_negation_and_alternatives_parse_into_terms()
    {
        var q = TicketQuery.Parse("  repo:M.TIX TYPE:Fix status:open,wont-do -label:ui 'two words' bare \"label:not a field\"  ");

        Assert.That(q.Terms.Select(t => (t.Field, string.Join("|", t.Values), t.Negated)), Is.EqualTo(new (string?, string, bool)[]
        {
            ("repo", "M.TIX", false), ("type", "fix", false), ("status", "open|wont-do", false), ("label", "ui", true),
            (null, "two words", false), (null, "bare", false), (null, "label:not a field", false),
        }));
        Assert.That(TicketQuery.Parse("label:\"needs review\"").Terms.Single().Values, Is.EqualTo(new[] { "needs review" }));
        Assert.That(TicketQuery.Parse("   ").IsEmpty, Is.True);
    }

    [TestCase("priority:high", "Unknown filter 'priority:'")]
    [TestCase("type:bugfix", "Unknown type 'bugfix'")]
    [TestCase("status:done", "Unknown status 'done'")]
    [TestCase("approval:yes", "Unknown approval 'yes'")]
    [TestCase("repo:", "'repo:' needs a value")]
    [TestCase("'unterminated", "Unterminated")]
    public void A_bad_query_is_refused_saying_why(string query, string message)
    {
        Assert.That(() => TicketQuery.Parse(query), Throws.TypeOf<FormatException>().With.Message.Contains(message));
        Assert.That(TicketQuery.TryParse(query, out var parsed, out var error), Is.False);
        Assert.That(parsed.IsEmpty, Is.True);
        Assert.That(error, Does.Contain(message));
    }

    [Test]
    public void Each_field_matches_its_part_of_the_ticket()
    {
        var t = new TicketFacts("MindAttic.Tickets", T("M.TIX-T78", "feature", "open", "Query language", "M.TIX-US-H7"), "approved", ["UI", "cli"]);

        Assert.That(Match("repo:mindattic.tickets", t), Is.True, "by name, without case");
        Assert.That(Match("repo:M.TIX", t), Is.True, "by code");
        Assert.That(Match("repo:MindAttic.*", t), Is.True, "with a wildcard");
        Assert.That(Match("repo:M.ORCH", t), Is.False);
        Assert.That(Match("type:feature status:open approval:approved", t), Is.True);
        Assert.That(Match("type:fix,feature", t), Is.True, "any alternative");
        Assert.That(Match("-type:feature", t), Is.False);
        Assert.That(Match("approval:proposed", t), Is.False);
        Assert.That(Match("label:ui label:CLI", t), Is.True, "every label term, any case");
        Assert.That(Match("label:docs", t), Is.False);
        Assert.That(Match("label:*", new TicketFacts("R", T("R-T1"))), Is.False, "an unlabelled ticket matches no label");
        Assert.That(Match("language", t), Is.True, "free text in the summary");
        Assert.That(Match("us-h7", t), Is.True, "free text in the ref");
        Assert.That(Match("t78", t), Is.True, "free text in the ID");
        Assert.That(Match("'query language' -nothing", t), Is.True);
        Assert.That(Match("query nothing", t), Is.False, "every term must match");
    }

    [Test]
    public void Saved_filters_are_kept_by_name_replaced_without_case_and_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var db = TicketsDb.Open(Path.Combine(root, "Tickets.db")))
            {
                Assert.That(db.SavedFilters(), Is.Empty);
                db.SaveFilter("Open fixes", " type:fix status:open ");
                db.SaveFilter("mine", "approval:approved");
                var replaced = db.SaveFilter("OPEN FIXES", "type:fix");

                Assert.That(replaced, Is.EqualTo(db.FindFilter("open fixes")));
                Assert.That(db.SavedFilters().Select(f => (f.Name, f.Query)), Is.EqualTo(new[] { ("mine", "approval:approved"), ("Open fixes", "type:fix") }));
                Assert.That(() => db.SaveFilter("bad", "status:nope"), Throws.TypeOf<FormatException>());
                Assert.That(() => db.SaveFilter(" ", "type:fix"), Throws.ArgumentException);
                Assert.That(() => db.SaveFilter("empty", "  "), Throws.ArgumentException);
                Assert.That(db.Dump(), Does.Contain("INSERT INTO saved_filters (name, query, updated) VALUES ('mine', 'approval:approved', '"));

                Assert.That(db.DeleteFilter("MINE"), Is.True);
                Assert.That(db.DeleteFilter("mine"), Is.False);
                Assert.That(db.FindFilter("mine"), Is.Null);
                Assert.That(db.AllLabels(), Is.Empty, "no labels yet: label: matches nothing");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
