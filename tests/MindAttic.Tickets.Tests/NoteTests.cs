using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Repos;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>Append-only ticket comments (M.TIX-T72): ticket_notes, <see cref="TicketsDb.AddNote"/> and <see cref="TicketsDb.Notes"/>.</summary>
public sealed class NoteTests
{
    private string root = "";
    private string alpha = "";
    private string path = "";
    private TicketsDb db = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-notes-" + Guid.NewGuid().ToString("N"));
        alpha = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(alpha, ".git"));
        Directory.CreateDirectory(Path.Combine(alpha, "docs"));
        File.WriteAllText(Path.Combine(alpha, "docs", "USER_STORIES.md"),
            "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
            "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" +
            "| AL-T1 | 2026-10-01 | feature | open | one | — |\n| AL-T2 | 2026-10-02 | fix | open | two | — |\n");
        path = Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db");
        db = TicketsDb.Open(path);
        db.Import([new Repo("Alpha", alpha)]);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [Test]
    public void Notes_are_appended_in_order_with_author_time_and_markdown()
    {
        var first = db.AddNote("al-t1", " ryan ", "Found the cause:\n\n- **one**\n- two\n");
        db.AddNote("AL-T1", "kim", "second");
        db.AddNote("AL-T2", "kim", "elsewhere");

        Assert.That((first.TicketId, first.Author, first.Markdown), Is.EqualTo(("AL-T1", "ryan", "Found the cause:\n\n- **one**\n- two")));
        Assert.That(first.At, Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"));
        var notes = db.Notes("al-t1");
        Assert.That(notes.Select(n => (n.Author, n.Markdown)), Is.EqualTo(new[] { ("ryan", first.Markdown), ("kim", "second") }));
        Assert.That(notes[0].Seq, Is.LessThan(notes[1].Seq));
        Assert.That(db.Notes("AL-T2"), Has.Count.EqualTo(1));
        Assert.That(db.Notes("AL-T99"), Is.Empty);
    }

    [Test]
    public void A_note_needs_a_ticket_an_author_and_text()
    {
        Assert.That(() => db.AddNote("AL-T99", "ryan", "x"), Throws.TypeOf<KeyNotFoundException>());
        Assert.That(() => db.AddNote("AL-T1", " ", "x"), Throws.ArgumentException);
        Assert.That(() => db.AddNote("AL-T1", "ryan", " \n "), Throws.ArgumentException);
        Assert.That(db.Notes("AL-T1"), Is.Empty);
    }

    [Test]
    public void Notes_can_never_be_changed_or_deleted()
    {
        db.AddNote("AL-T1", "ryan", "kept");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        foreach (var sql in new[] { "UPDATE ticket_notes SET markdown = 'edited'", "UPDATE ticket_notes SET author = 'x'", "UPDATE ticket_notes SET at = 'x'", "DELETE FROM ticket_notes" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.TypeOf<SqliteException>().With.Message.Contains("ticket notes"), sql);
        }
        Assert.That(db.Notes("AL-T1").Single().Markdown, Is.EqualTo("kept"));
    }

    [Test]
    public void Notes_follow_a_reassigned_id_and_survive_a_reimport()
    {
        db.AddNote("AL-T1", "ryan", "before the move");
        db.ReassignTicketId("AL-T1", "AL-T9", "test");
        Assert.That(db.Notes("AL-T9").Single().TicketId, Is.EqualTo("AL-T9"));
        Assert.That(db.Notes("AL-T1").Single().Markdown, Is.EqualTo("before the move"), "the former ID still finds them");

        db.AddNote("AL-T2", "kim", "kept across imports");
        db.Import([new Repo("Alpha", alpha)]);
        Assert.That(db.Notes("AL-T2").Single().Markdown, Is.EqualTo("kept across imports"));
        Assert.That(db.Dump(), Does.Contain("-- ticket_notes\nINSERT INTO ticket_notes"));
    }
}
