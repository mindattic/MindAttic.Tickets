using MindAttic.Tickets.Data;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Tests;

/// <summary>M.TIX-T86: <see cref="TicketStats"/>, the ticket metrics worked out from the tickets and their ticket_events history.</summary>
public sealed class TicketStatsTests
{
    // Wednesday: the current week starts Monday 2026-10-19, the one before Monday 2026-10-12.
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static Ticket T(string id, string type, string status, string date = "2026-10-12") => new(id, date, type, status, id + " summary", "—");

    private static TicketEvent Created(string id, string at, string status = TicketRules.Open) => new(id, at, "created", null, status);

    private static TicketEvent Status(string id, string at, string from, string to) => new(id, at, "status", from, to);

    private static TicketMetrics Compute(IEnumerable<(string, Ticket)> tickets, IEnumerable<TicketEvent> events, StatsPeriod by = StatsPeriod.Week, int periods = 2) =>
        TicketStats.Compute(tickets, events, by, periods, Now);

    [Test]
    public void A_ticket_filed_and_closed_counts_once_in_throughput_and_its_cycle_time_is_filed_to_closed()
    {
        var m = Compute([("A", T("A-T1", "fix", TicketRules.Closed))],
            [Created("A-T1", "2026-10-13T10:00:00Z"), Status("A-T1", "2026-10-15T10:00:00Z", TicketRules.Open, TicketRules.Closed)]);

        Assert.That(m.Periods.Select(p => p.Label), Is.EqualTo(new[] { "2026-10-12", "2026-10-19" }));
        Assert.That(m.Periods.Select(p => (p.Opened, p.Closed, p.WontDo, p.OpenAtEnd)), Is.EqualTo(new[] { (1, 1, 0, 0), (0, 0, 0, 0) }));
        var all = m.CycleTime.Single(c => c.Group == TicketStats.AllTypes);
        Assert.That((all.Count, all.Median, all.Mean, all.P85), Is.EqualTo((1, TimeSpan.FromDays(2), TimeSpan.FromDays(2), TimeSpan.FromDays(2))));
        Assert.That(m.CycleTime.Single(c => c.Group == "fix").Count, Is.EqualTo(1));
        Assert.That(m.OpenByRepo, Is.Empty, "never open at a period's end");
    }

    [Test]
    public void A_reopen_takes_back_the_closing_before_it_and_the_cycle_runs_from_filing_to_the_final_close()
    {
        var m = Compute([("A", T("A-T1", "feature", TicketRules.Closed))],
        [
            Created("A-T1", "2026-10-13T00:00:00Z"),
            Status("A-T1", "2026-10-14T00:00:00Z", TicketRules.Open, TicketRules.Closed),
            Status("A-T1", "2026-10-14T06:00:00Z", TicketRules.Closed, TicketRules.Open),
            Status("A-T1", "2026-10-20T00:00:00Z", TicketRules.Open, TicketRules.Closed),
        ]);

        Assert.That(m.Periods.Select(p => (p.Closed, p.OpenAtEnd)), Is.EqualTo(new[] { (0, 1), (1, 0) }), "open at the end of the first week, closed in the second");
        Assert.That(m.CycleTime.Single(c => c.Group == TicketStats.AllTypes).Median, Is.EqualTo(TimeSpan.FromDays(7)));
        Assert.That(m.OpenByType.Single().Name, Is.EqualTo("feature"));
        Assert.That(m.OpenByType.Single().Counts, Is.EqualTo(new[] { 1, 0 }));
    }

    [Test]
    public void Closed_then_corrected_to_wont_do_is_one_wont_do_closing_with_no_cycle_time()
    {
        var m = Compute([("A", T("A-T1", "chore", TicketRules.WontDo))],
        [
            Created("A-T1", "2026-10-13T00:00:00Z"),
            Status("A-T1", "2026-10-14T00:00:00Z", TicketRules.Open, TicketRules.Closed),
            Status("A-T1", "2026-10-15T00:00:00Z", TicketRules.Closed, TicketRules.WontDo),
        ]);

        Assert.That(m.Periods[0].Closed, Is.Zero);
        Assert.That(m.Periods[0].WontDo, Is.EqualTo(1));
        Assert.That(m.CycleTime.Single(c => c.Group == TicketStats.AllTypes).Count, Is.Zero, "wont-do is dropped work, not delivered work");
        Assert.That(m.CycleTime.Single(c => c.Group == TicketStats.AllTypes).Median, Is.Null);
    }

    [Test]
    public void A_ticket_filed_already_closed_counts_as_throughput_but_not_cycle_time()
    {
        var m = Compute([("A", T("A-T1", "fix", TicketRules.Closed, "2026-10-20"))], [Created("A-T1", "2026-10-20T09:00:00Z", TicketRules.Closed)]);

        Assert.That(m.Periods[1].Opened, Is.EqualTo(1));
        Assert.That(m.Periods[1].Closed, Is.EqualTo(1));
        Assert.That(m.Periods.Select(p => p.OpenAtEnd), Is.All.Zero);
        Assert.That(m.FiledClosed, Is.EqualTo(1));
        Assert.That(m.CycleTime.Single(c => c.Group == TicketStats.AllTypes).Count, Is.Zero);
    }

    [Test]
    public void Imported_tickets_are_filed_at_their_date_and_one_imported_closed_is_in_none_of_the_numbers()
    {
        var m = Compute(
        [
            ("A", T("A-T1", "fix", TicketRules.Closed, "2026-01-05")),
            ("B", T("B-T1", "docs", TicketRules.Open, "2026-10-12")),
        ], []);

        Assert.That(m.ClosedBeforeHistory, Is.EqualTo(1));
        Assert.That(m.Periods.Select(p => (p.Opened, p.Closed, p.OpenAtEnd)), Is.EqualTo(new[] { (1, 0, 1), (0, 0, 1) }), "B-T1 filed at the start of 2026-10-12");
        Assert.That(m.OpenByRepo.Select(s => s.Name), Is.EqualTo(new[] { "B" }));
        Assert.That(m.OpenByType.Select(s => s.Name), Is.EqualTo(new[] { "docs" }));
    }

    [Test]
    public void A_closing_exactly_at_a_periods_start_falls_in_that_period_and_the_running_one_counts_open_as_of_now()
    {
        var m = Compute([("A", T("A-T1", "fix", TicketRules.Closed)), ("A", T("A-T2", "fix", TicketRules.Open))],
        [
            Created("A-T1", "2026-10-12T00:00:00Z"),
            Status("A-T1", "2026-10-19T00:00:00Z", TicketRules.Open, TicketRules.Closed),
            Created("A-T2", "2026-10-21T13:00:00Z"), // filed after "now": in the running week, not yet open now
        ]);

        Assert.That(m.Periods.Select(p => (p.Opened, p.Closed, p.OpenAtEnd)), Is.EqualTo(new[] { (1, 0, 0), (1, 1, 0) }),
            "closed at the instant the first week ends: not open at its end, closed in the second");
        Assert.That(m.Periods[1].End, Is.EqualTo(new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Periods_floor_to_UTC_days_Monday_weeks_and_calendar_months()
    {
        var sunday = new DateTime(2026, 10, 25, 23, 59, 0, DateTimeKind.Utc);
        Assert.That(TicketStats.Floor(sunday, StatsPeriod.Day), Is.EqualTo(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(TicketStats.Floor(sunday, StatsPeriod.Week), Is.EqualTo(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(TicketStats.Floor(sunday, StatsPeriod.Month), Is.EqualTo(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));

        var months = TicketStats.Compute([], [], StatsPeriod.Month, 3, new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.That(months.Periods.Select(p => p.Label), Is.EqualTo(new[] { "2025-11", "2025-12", "2026-01" }), "across a year");
        var days = TicketStats.Compute([], [], StatsPeriod.Day, 2, Now);
        Assert.That(days.Periods.Select(p => p.Label), Is.EqualTo(new[] { "2026-10-20", "2026-10-21" }));

        Assert.That(TicketStats.ParsePeriod("W"), Is.EqualTo(StatsPeriod.Week));
        Assert.That(TicketStats.ParsePeriod("monthly"), Is.EqualTo(StatsPeriod.Month));
        Assert.That(TicketStats.ParsePeriod("year"), Is.Null);
    }

    [Test]
    public void Cycle_time_reports_median_mean_and_nearest_rank_85th_percentile_and_durations_read_as_people_say_them()
    {
        var tickets = Enumerable.Range(1, 4).Select(i => ("A", T($"A-T{i}", "fix", TicketRules.Closed))).ToList();
        var events = new List<TicketEvent>();
        foreach (var (i, hours) in new[] { (1, 1), (2, 2), (3, 3), (4, 10) })
        {
            events.Add(Created($"A-T{i}", "2026-10-13T00:00:00Z"));
            events.Add(Status($"A-T{i}", $"2026-10-13T{hours:00}:00:00Z", TicketRules.Open, TicketRules.Closed));
        }
        var all = Compute(tickets, events).CycleTime.Single(c => c.Group == TicketStats.AllTypes);

        Assert.That((all.Count, all.Median, all.Mean, all.P85), Is.EqualTo((4, TimeSpan.FromHours(2.5), TimeSpan.FromHours(4), TimeSpan.FromHours(10))));
        Assert.That(TicketStats.Duration(null), Is.EqualTo("—"));
        Assert.That(TicketStats.Duration(TimeSpan.FromMinutes(42)), Is.EqualTo("42m"));
        Assert.That(TicketStats.Duration(TimeSpan.FromHours(2.5)), Is.EqualTo("2.5h"));
        Assert.That(TicketStats.Duration(TimeSpan.FromDays(3)), Is.EqualTo("3d"));
    }

    [Test]
    public void StatusHistory_reads_only_created_and_status_rows_oldest_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-stats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = Path.Combine(root, "Alpha");
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            Directory.CreateDirectory(Path.Combine(dir, "docs"));
            File.WriteAllText(Path.Combine(dir, "docs", "USER_STORIES.md"),
                "---\ncodex: 1\nproject: Alpha\ncode: AL\nlayer: stories\nstatus: living\nupdated: 2026-10-12\n---\n\n# Alpha — User Stories\n\n" +
                "## Tickets\n> legend\n\n| ID | Date | Type | Status | Summary | Ref |\n|---|---|---|---|---|---|\n| AL-T1 | 2026-10-01 | feature | open | one | — |\n");
            using (var db = TicketsDb.Open(Path.Combine(root, "MindAttic.Tickets", "data", "Tickets.db")))
            {
                db.Import([new Repo("Alpha", dir)]);
                db.Approve("AL-T1");
                db.Add("Alpha", "fix", TicketRules.Open, "two", "—");
                db.Close("AL-T1", TicketRules.Closed, "abc123");

                var history = db.StatusHistory();
                Assert.That(history.Select(e => e.Field).Distinct(), Is.SubsetOf(new[] { "created", "status" }));
                Assert.That(history.Select(e => (e.TicketId, e.Field, e.New)), Is.EqualTo(new[] { ("AL-T2", "created", (string?)TicketRules.Open), ("AL-T1", "status", TicketRules.Closed) })
                    .Or.EqualTo(new[] { ("AL-T1", "created", (string?)TicketRules.Open), ("AL-T2", "created", TicketRules.Open), ("AL-T1", "status", TicketRules.Closed) }));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
