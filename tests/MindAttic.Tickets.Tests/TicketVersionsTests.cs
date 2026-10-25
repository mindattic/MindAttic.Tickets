using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>A ticket's versions (M.TIX-T137) where the store doesn't keep every save (the SQLite file): worked back from its
/// history events, oldest first, the last the ticket as it is now. (On SQL Server, see TicketsServerTests.)</summary>
public sealed class TicketVersionsTests
{
    private string root = "";

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("tickets-versions-").FullName;

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private TicketsDb Alpha()
    {
        var repo = Path.Combine(root, "Alpha");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        File.WriteAllText(Path.Combine(repo, "docs", "USER_STORIES.md"), StoryFile.NewFileText("Alpha", "AL", "2026-10-23"));
        var db = TicketsDb.Open(Path.Combine(root, "Tickets.db"));
        db.Import([new Repo("Alpha", repo)]);
        return db;
    }

    [Test]
    public void Each_save_in_a_tickets_history_is_a_version_the_last_one_now()
    {
        using var db = Alpha();
        var a = db.Add("Alpha", "fix", "open", "first", "—");
        var b = db.Add("Alpha", "feature", "open", "second", "—");
        db.Close(a.Id, reference: "abc123");

        var versions = db.Versions(a.Id);
        Assert.That(versions.Select(v => (v.Number, v.Ticket.Status, v.Ticket.Ref)), Is.EqualTo(new[] { (1, "open", "—"), (2, "closed", "abc123") }));
        Assert.That(versions[0].Changed, Is.Empty, "the first is as filed");
        Assert.That(versions[1].Changed, Is.EqualTo(new[] { "status", "ref" }));
        Assert.That(versions.All(v => v.Recorded == db.IsVersioned()), "worked back from the history on the file; recorded on SQL Server (the suite's server run)");
        Assert.That(versions[1].At, Is.GreaterThan(versions[0].At));
        Assert.That(db.Versions(b.Id), Has.Count.EqualTo(1), "never changed: one version");
        Assert.That(db.CheckVersions(), Is.Empty);
    }

    [Test]
    public void A_reworded_summary_is_a_version_and_the_one_before_reads_the_old_wording()
    {
        using var db = Alpha();
        var a = db.Add("Alpha", "fix", "open", "old wording", "—");
        db.CorrectSummary(a.Id, "new wording", "clearer");

        var versions = db.Versions(a.Id);
        Assert.That(versions.Select(v => v.Ticket.Summary), Is.EqualTo(new[] { "old wording", "new wording" }));
        Assert.That(versions[1].Changed, Is.EqualTo(new[] { "summary" }));
        Assert.That(db.CheckVersions(), Is.Empty);
    }

    [Test]
    public void Verify_says_when_a_tickets_history_no_longer_matches_it()
    {
        using var db = Alpha();
        var a = db.Add("Alpha", "fix", "open", "first", "—");
        db.Plant("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, '2026-10-23T10:00:00Z', 'status', 'open', 'closed')", ("$id", a.Id));

        Assert.That(db.CheckVersions("Alpha"), Is.EqualTo(new[] { $"Alpha: {a.Id}'s history last set its status to 'closed', but it's 'open'" }));
    }

    [Test]
    public void A_timeline_is_worked_back_from_the_events_old_values_one_save_per_moment()
    {
        static TicketEvent E(string at, string field, string? old, string? now) => new("X-T9", at, field, old, now);
        var events = new[]
        {
            E("2026-10-01T09:00:00Z", "created", null, "open"),
            E("2026-10-02T09:00:00Z", "repo", "Alpha", "Beta"),
            E("2026-10-02T09:00:00Z", "approval", "proposed", "approved"),
            E("2026-10-02T09:00:00Z", "priority", "0", "5"),
            E("2026-10-03T09:00:00Z", "status", "open", "closed"),
            E("2026-10-03T09:00:00Z", "ref", "—", "r1"),
            E("2026-10-04T09:00:00Z", "ref", "r1", "r2"),
            E("2026-10-05T09:00:00Z", "id", "X-T1", "X-T9 (renumbered)"),
        };
        var now = new TicketsDb.Shape("X-T9", "Beta", "fix", "closed", "r2", "approved", 5);
        var line = TicketsDb.Timeline(now, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), events,
            new HashSet<string> { "status", "ref", "type", "repo", "id", "approval", "priority" }, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(line.Select(x => x.State), Is.EqualTo(new[]
        {
            new TicketsDb.Shape("X-T1", "Alpha", "fix", "open", "—", "proposed", 0),
            new TicketsDb.Shape("X-T1", "Beta", "fix", "open", "—", "approved", 5),
            new TicketsDb.Shape("X-T1", "Beta", "fix", "closed", "r1", "approved", 5),
            new TicketsDb.Shape("X-T1", "Beta", "fix", "closed", "r2", "approved", 5),
            now,
        }));
        Assert.That(line.Select(x => x.At.Day), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void A_timeline_never_runs_past_its_ceiling_and_always_rises()
    {
        var events = new[] { new TicketEvent("X-T1", "2030-01-01T00:00:00Z", "status", "open", "closed"), new TicketEvent("X-T1", "2031-01-01T00:00:00Z", "ref", "—", "r") };
        var ceiling = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        var line = TicketsDb.Timeline(new TicketsDb.Shape("X-T1", "A", "fix", "closed", "r", "proposed", 0), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            events, new HashSet<string> { "status", "ref" }, ceiling);

        Assert.That(line, Has.Count.EqualTo(3));
        Assert.That(line[1].At, Is.EqualTo(ceiling));
        Assert.That(line[2].At, Is.GreaterThan(line[1].At));
    }
}
