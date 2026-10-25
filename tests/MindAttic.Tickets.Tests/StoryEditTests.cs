using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Stories;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>M.TIX-T82: editing stories and epics as Tickets Desktop's Stories view does, and the markdown following.</summary>
public sealed class StoryEditTests
{
    private string root = "";
    private TicketsDb db = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-story-edit-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Epic A — Core {#core}\n- **AL-US-A1 ⬜** As a user, I can start. *Acceptance: it starts* *(no test yet.)*\n" +
            "- **AL-US-A2 ✅** Done thing,\n  wrapped onto a second line. *(verified by `T`.)*\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | build AL-US-A1 | — |\n");
        db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        db.Import([new Repo("Alpha", dir)]);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private string Export() => System.Text.Encoding.UTF8.GetString(db.Export("Alpha")!);

    private StoredStory Story(string id) => db.Story(id).Single();

    [Test]
    public void A_story_splits_into_statement_acceptance_and_evidence_and_composes_back()
    {
        var (statement, acceptance, evidence) = StoryDocument.SplitStory("As a user,   I can start. *Acceptance: it starts* *(no test yet.)*");
        Assert.That((statement, acceptance, evidence), Is.EqualTo(("As a user, I can start.", "it starts", "no test yet.")));
        Assert.That(StoryDocument.ComposeStory(statement, acceptance, evidence), Is.EqualTo("As a user, I can start. *Acceptance: it starts* *(no test yet.)*"));
        Assert.That(StoryDocument.ComposeStory("Just this.", " ", null), Is.EqualTo("Just this."), "empty parts are left out");
        Assert.That(StoryDocument.SplitStory("Plain."), Is.EqualTo(("Plain.", (string?)null, (string?)null)));
        Assert.That(() => StoryDocument.ComposeStory("x", "a*b", null), Throws.ArgumentException);
        Assert.That(() => StoryDocument.ComposeStory("  ", null, null), Throws.ArgumentException);
    }

    [Test]
    public void Saving_a_story_writes_its_statement_status_acceptance_and_evidence_and_the_view_reads_them_back()
    {
        Assert.That(db.UpdateStory("AL-US-A1", "Alpha", "As a user, I can start quickly.", "partial", "it starts in a second", "verified by `StartTests`."), Is.True);

        var s = Story("AL-US-A1");
        Assert.That((s.Status, s.Acceptance, s.Evidence), Is.EqualTo(("partial", "it starts in a second", "verified by `StartTests`.")));
        var text = Export();
        Assert.That(text, Does.Contain("- **AL-US-A1 🟡** As a user, I can start quickly. *Acceptance: it starts in a second* *(verified by `StartTests`.)*\n"));

        var reparsed = StoryDocument.Parse(text).Stories.Single(x => x.Id == "AL-US-A1");
        Assert.That((reparsed.Acceptance, reparsed.Evidence), Is.EqualTo(("it starts in a second", "verified by `StartTests`.")));
    }

    [Test]
    public void A_status_only_save_keeps_the_wording_and_wrapping_and_an_unchanged_save_changes_nothing()
    {
        var before = Story("AL-US-A2");
        var (statement, acceptance, evidence) = StoryDocument.SplitStory(before.Text);
        Assert.That(db.UpdateStory("AL-US-A2", null, statement, null, acceptance, evidence), Is.False);

        Assert.That(db.UpdateStory("AL-US-A2", null, statement, "planned", acceptance, evidence), Is.True);
        Assert.That(Export(), Does.Contain("- **AL-US-A2 ⬜** Done thing,\n  wrapped onto a second line. *(verified by `T`.)*\n"), "the bullet keeps its two lines");
        Assert.That(() => db.UpdateStory("AL-US-A2", null, statement, "finished", acceptance, evidence), Throws.ArgumentException);
        Assert.That(() => db.UpdateStory("AL-US-ZZ", null, "x", null, null, null), Throws.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void Editing_a_story_reads_its_evidence_and_acceptance_from_the_new_text_instead_of_wiping_them()
    {
        db.EditStory("AL-US-A2", "Done thing, better. *(verified by `T2`.)*");
        var s = Story("AL-US-A2");
        Assert.That((s.Evidence, s.Acceptance), Is.EqualTo(("verified by `T2`.", (string?)null)));
    }

    [Test]
    public void Epics_are_listed_with_their_ids_and_renaming_one_keeps_its_anchor()
    {
        Assert.That(db.Epics("Alpha"), Is.EqualTo(new[] { new StoredEpic("Alpha", "A", "Core", "AL-EPIC-A") }));
        db.EditEpic("alpha", "a", "Core flows");
        Assert.That(db.Epics().Single().Title, Is.EqualTo("Core flows"));
        Assert.That(Export(), Does.Contain("## Epic A — Core flows {#core}\n"));
        Assert.That(() => db.EditEpic("Alpha", "Z", "x"), Throws.InstanceOf<KeyNotFoundException>());
        Assert.That(() => db.EditEpic("Alpha", "A", " "), Throws.ArgumentException);
    }
}
