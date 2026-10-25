using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>Ticket labels and components (M.TIX-T74), kept in the ticket database's <c>ticket_labels</c>.</summary>
public sealed class LabelTests
{
    private string root = "";
    private TicketsDb db = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-labels-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | one | — |\n| AL-T2 | 2026-10-02 | fix | open | two | — |\n");
        db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        db.Import([new Repo("Alpha", alpha)]);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [TestCase("UI", "ui")]
    [TestCase(" component:Desktop ", "component:desktop")]
    [TestCase("v2.1/api_x-y", "v2.1/api_x-y")]
    [TestCase("", null)]
    [TestCase("-lead", null)]
    [TestCase("has space", null)]
    [TestCase("semi;colon", null)]
    public void A_label_is_trimmed_and_lower_cased_or_refused(string raw, string? expected) =>
        Assert.That(TicketsDb.NormalizeLabel(raw), Is.EqualTo(expected));

    [Test]
    public void A_label_longer_than_the_limit_is_refused() =>
        Assert.That(TicketsDb.NormalizeLabel(new string('a', TicketsDb.MaxLabelLength + 1)), Is.Null);

    [Test]
    public void Labels_are_added_and_removed_once_each_with_history()
    {
        Assert.That(db.AddLabels("al-t1", ["UI", "component:desktop", "ui"]), Is.EqualTo(new[] { "component:desktop", "ui" }));
        Assert.That(db.AddLabels("AL-T1", ["ui"]), Is.EqualTo(new[] { "component:desktop", "ui" }), "one it has already is passed over");
        Assert.That(db.Labels("AL-T1"), Is.EqualTo(new[] { "component:desktop", "ui" }));
        Assert.That(db.RemoveLabels("AL-T1", ["UI", "never-had"]), Is.EqualTo(new[] { "component:desktop" }));

        var history = db.History("AL-T1").Where(e => e.Field == "label").Select(e => (e.Old, e.New)).ToList();
        Assert.That(history, Is.EqualTo(new (string?, string?)[] { (null, "ui"), (null, "component:desktop"), ("ui", null) }));
    }

    [Test]
    public void A_bad_label_or_a_missing_ticket_changes_nothing()
    {
        Assert.That(() => db.AddLabels("AL-T1", ["ok", "not ok"]), Throws.ArgumentException.With.Message.Contains("not ok"));
        Assert.That(() => db.AddLabels("AL-T1", []), Throws.ArgumentException);
        Assert.That(() => db.AddLabels("AL-T99", ["ui"]), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(db.Labels("AL-T1"), Is.Empty);
        Assert.That(db.AllLabels(), Is.Empty);
    }

    [Test]
    public void Every_label_in_use_is_counted_and_listed_by_ticket()
    {
        db.AddLabels("AL-T1", ["ui", "component:desktop"]);
        db.AddLabels("AL-T2", ["ui"]);

        Assert.That(db.LabelCounts(), Is.EqualTo(new[] { ("component:desktop", 1L), ("ui", 2L) }));
        var all = db.AllLabels();
        Assert.That(all["al-t1"], Is.EqualTo(new[] { "component:desktop", "ui" }), "keyed without case");
        Assert.That(all["AL-T2"], Is.EqualTo(new[] { "ui" }));
    }

    [Test]
    public void Labels_follow_a_reassigned_ticket_and_are_in_the_dump()
    {
        db.AddLabels("AL-T1", ["ui"]);
        var moved = db.ReassignTicketId("AL-T1", "AL-T9", "test");

        Assert.That(db.Labels(moved.Id), Is.EqualTo(new[] { "ui" }));
        Assert.That(db.Labels("AL-T1"), Is.EqualTo(new[] { "ui" }), "the old ID still resolves");
        Assert.That(db.Dump(), Does.Contain("INSERT INTO ticket_labels (ticket_id, label, added) VALUES ('AL-T9', 'ui', '"));
    }
}
