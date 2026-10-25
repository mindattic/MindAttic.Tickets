using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using MindAttic.Tickets.Repos;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>The workspace activity feed (M.TIX-T84): <see cref="TicketsDb.Activity"/> over ticket_events in every repo.</summary>
public sealed class ActivityTests
{
    private string root = "";
    private TicketsDb db = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-activity-" + Guid.NewGuid().ToString("N"));
        Repo Make(string name, string code, string rows)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            Directory.CreateDirectory(Path.Combine(dir, "docs"));
            File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
                $"---\ncodex: 1\nproject: {name}\ncode: {code}\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# {name} — User Stories\n\n" +
                "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n" + rows);
            return new Repo(name, dir);
        }
        var alpha = Make("Alpha", "AL", "| AL-T1 | 2026-10-01 | feature | open | alpha one | — |\n| AL-T2 | 2026-10-02 | fix | open | alpha two | — |\n");
        var beta = Make("Beta", "BE", "| BE-T1 | 2026-10-01 | fix | open | beta one | — |\n");
        db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db"));
        db.Import([alpha, beta]);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private long Latest() => db.Activity(0, 1) is [var e] ? e.Seq : 0;

    [Test]
    public void The_feed_lists_every_repos_changes_newest_first_and_only_whats_new_after_a_seq()
    {
        var start = Latest();
        db.Approve("AL-T1");
        db.Close("BE-T1", TicketRules.Closed, "abc123");
        db.Add("Alpha", "fix", TicketRules.Open, "alpha three", "—");

        var feed = db.Activity(start);
        Assert.That(feed.Select(e => (e.TicketId, e.Repo, e.Field)), Is.EqualTo(new[]
        {
            ("AL-T3", "Alpha", "created"),
            ("BE-T1", "Beta", "ref"),
            ("BE-T1", "Beta", "status"),
            ("AL-T1", "Alpha", "approval"),
        }).Or.EqualTo(new[]
        {
            ("AL-T3", "Alpha", "created"),
            ("BE-T1", "Beta", "status"),
            ("BE-T1", "Beta", "ref"),
            ("AL-T1", "Alpha", "approval"),
        }));
        Assert.That(feed.Select(e => e.Seq), Is.Ordered.Descending);
        var close = feed.Single(e => e.Field == "status");
        Assert.That((close.IsClose, close.Text, close.Summary, close.Worker), Is.EqualTo((true, "closed", "beta one", (string?)null)));
        Assert.That(feed.Single(e => e.Field == "approval").Text, Is.EqualTo("approval: proposed → approved"));

        Assert.That(db.Activity(feed[0].Seq), Is.Empty, "nothing new after the newest");
        Assert.That(db.Activity(start, limit: 2).Select(e => e.Seq), Is.EqualTo(feed.Take(2).Select(e => e.Seq)));
        Assert.That(db.Activity(start, repo: "beta").Select(e => e.Repo).Distinct(), Is.EqualTo(new[] { "Beta" }));
    }

    [Test]
    public void A_close_carries_the_session_that_was_working_the_ticket_then()
    {
        var start = Latest();
        db.SetWorking("AL-T2", "agent@example.com", "sess-9");
        db.Close("AL-T2", TicketRules.Closed, "def456");
        db.ClearWorking("AL-T2"); // the Orchestrator lets go after it lands
        db.Close("AL-T1", TicketRules.WontDo, "not needed"); // never worked: a hand close

        var closes = db.Activity(start).Where(e => e.IsClose).ToDictionary(e => e.TicketId);
        Assert.That(closes["AL-T2"].Worker, Is.EqualTo("agent@example.com (sess-9)"));
        Assert.That(closes["AL-T2"].WorkerName, Is.EqualTo("agent@example.com"));
        Assert.That(closes["AL-T1"].Worker, Is.Null);
        Assert.That(closes["AL-T1"].Text, Is.EqualTo("closed as wont-do"));
        Assert.That(db.Activity(start).Where(e => e.Field == "working").Select(e => e.Text),
            Is.EqualTo(new[] { "no longer worked (agent@example.com (sess-9))", "being worked by agent@example.com (sess-9)" }));
    }
}
