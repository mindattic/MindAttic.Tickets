using System.Globalization;
using MindAttic.Tickets.Data;

namespace MindAttic.Tickets.Log;

/// <summary>The length of one period in <see cref="TicketStats"/>: a day, a week (Monday to Sunday, UTC) or a calendar month.</summary>
public enum StatsPeriod { Day, Week, Month }

/// <summary>
/// One period of <see cref="TicketMetrics"/>: tickets filed in it, closed in it (as closed, and as wont-do), and the tickets
/// open at its end (now, for the period still running). <paramref name="End"/> is exclusive.
/// </summary>
public sealed record PeriodStats(string Label, DateTime Start, DateTime End, int Opened, int Closed, int WontDo, int OpenAtEnd);

/// <summary>
/// Cycle time, filing to closing, of the tickets of <paramref name="Group"/> (every type, or one) closed in the window:
/// how many, and their median, mean and 85th percentile. The durations are null when none closed.
/// </summary>
public sealed record CycleStats(string Group, int Count, TimeSpan? Median, TimeSpan? Mean, TimeSpan? P85);

/// <summary>How many tickets of <paramref name="Name"/> (a repo or a type) were open at the end of each period, in period order.</summary>
public sealed record OpenSeries(string Name, IReadOnlyList<int> Counts);

/// <summary>Everything <c>tickets ticket stats</c> and Tickets Desktop's Stats tab show (M.TIX-T86).</summary>
public sealed record TicketMetrics(
    StatsPeriod By,
    IReadOnlyList<PeriodStats> Periods,
    IReadOnlyList<CycleStats> CycleTime,
    IReadOnlyList<OpenSeries> OpenByRepo,
    IReadOnlyList<OpenSeries> OpenByType)
{
    /// <summary>Closed tickets with no status change in <c>ticket_events</c> (imported already closed): when they closed is unknown,
    /// so they count in no throughput or cycle time and were never open as far as these numbers go.</summary>
    public int ClosedBeforeHistory { get; init; }

    /// <summary>Closings in the window of tickets filed already closed (work logged after it was done): they count as throughput,
    /// but their cycle time is unknown, so none is counted.</summary>
    public int FiledClosed { get; init; }
}

/// <summary>
/// Ticket metrics worked out from the tickets and their <c>created</c> and <c>status</c> history in <c>ticket_events</c>
/// (M.TIX-T86): throughput (filed and closed per period), cycle time (filed to closed) overall and by type, and how many
/// tickets were open at the end of each period, by repo and by type. A ticket is filed at its <c>created</c> event, or at
/// the start of its Date when it has none (it was imported); every status change after that opens or closes it. A reopen
/// takes back the closing before it (an undone close didn't deliver anything).
/// </summary>
public static class TicketStats
{
    public const string AllTypes = "all";

    /// <summary>One ticket's life: when it was filed, the spans it was open, and each time it closed.</summary>
    internal sealed record Life(string Repo, Ticket Ticket, DateTime Filed, List<(DateTime From, DateTime? To)> Open, List<Closing> Closings, bool ClosedBeforeHistory);

    /// <summary>A closing: when, as what (closed or wont-do), and how long it had been open (null when it was filed closed).</summary>
    internal sealed record Closing(DateTime At, string Status, TimeSpan? Cycle);

    /// <summary>
    /// The metrics over the <paramref name="periods"/> periods of length <paramref name="by"/> that end with the one holding
    /// <paramref name="now"/> (UTC), for <paramref name="tickets"/> (already narrowed by repo or type as wanted). Only the
    /// <c>created</c> and <c>status</c> rows of <paramref name="events"/> are read; IDs match without case.
    /// </summary>
    public static TicketMetrics Compute(IEnumerable<(string Repo, Ticket Ticket)> tickets, IEnumerable<TicketEvent> events, StatsPeriod by, int periods, DateTime now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(periods, 1);
        now = DateTime.SpecifyKind(now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : now, DateTimeKind.Utc);
        var byTicket = events.Where(e => e.Field is "created" or "status")
            .ToLookup(e => e.TicketId, StringComparer.OrdinalIgnoreCase);
        var lives = tickets.Select(x => LifeOf(x.Repo, x.Ticket, byTicket[x.Ticket.Id])).ToList();

        var starts = new List<DateTime>();
        var start = Floor(now, by);
        for (var i = 0; i < periods; i++) { starts.Insert(0, start); start = Step(start, by, -1); }
        var windowStart = starts[0];
        var windowEnd = Step(starts[^1], by, +1);

        var rows = new List<PeriodStats>();
        var instants = new List<DateTime>();
        foreach (var s in starts)
        {
            var e = Step(s, by, +1);
            var at = e > now ? now : e; // the period still running: open now
            instants.Add(at);
            var closings = lives.SelectMany(l => l.Closings).Where(c => c.At >= s && c.At < e).ToList();
            rows.Add(new PeriodStats(Label(s, by), s, e,
                lives.Count(l => l.Filed >= s && l.Filed < e),
                closings.Count(c => c.Status == TicketRules.Closed),
                closings.Count(c => c.Status == TicketRules.WontDo),
                lives.Count(l => OpenAt(l, at))));
        }

        // Cycle time: closings as 'closed' in the window that have one (wont-do is dropped work, not delivered work).
        var done = lives.SelectMany(l => l.Closings.Where(c => c.Status == TicketRules.Closed && c.At >= windowStart && c.At < windowEnd && c.Cycle is not null)
            .Select(c => (l.Ticket.Type, Cycle: c.Cycle!.Value))).ToList();
        var cycle = new List<CycleStats> { Cycle(AllTypes, done.Select(d => d.Cycle)) };
        cycle.AddRange(TypesIn(lives).Select(t => Cycle(t, done.Where(d => d.Type == t).Select(d => d.Cycle))));

        return new TicketMetrics(by, rows, cycle,
            Series(lives, l => l.Repo, instants, StringComparer.OrdinalIgnoreCase),
            Series(lives, l => l.Ticket.Type, instants, StringComparer.Ordinal))
        {
            ClosedBeforeHistory = lives.Count(l => l.ClosedBeforeHistory),
            FiledClosed = lives.Sum(l => l.Closings.Count(c => c.Cycle is null && c.At >= windowStart && c.At < windowEnd)),
        };
    }

    /// <summary>The standard types first, in their usual order, then any other type a ticket has.</summary>
    private static IEnumerable<string> TypesIn(List<Life> lives) =>
        TicketRules.Types.Concat(lives.Select(l => l.Ticket.Type).Where(t => !TicketRules.Types.Contains(t)).Distinct().Order(StringComparer.Ordinal))
            .Where(t => lives.Any(l => l.Ticket.Type == t));

    /// <summary>Open counts per <paramref name="key"/> at each instant; only names open at some point in the window, the most open
    /// now first.</summary>
    private static List<OpenSeries> Series(List<Life> lives, Func<Life, string> key, List<DateTime> instants, StringComparer comparer) =>
        [.. lives.GroupBy(key, comparer)
            .Select(g => new OpenSeries(g.Key, [.. instants.Select(at => g.Count(l => OpenAt(l, at)))]))
            .Where(s => s.Counts.Any(c => c > 0))
            .OrderByDescending(s => s.Counts[^1]).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    private static bool OpenAt(Life l, DateTime at) => l.Open.Any(o => o.From <= at && (o.To is not { } to || to > at));

    internal static Life LifeOf(string repo, Ticket t, IEnumerable<TicketEvent> history)
    {
        var events = history.ToList(); // already oldest first (by seq)
        var created = events.FirstOrDefault(e => e.Field == "created");
        var dated = DateTime.TryParseExact(t.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null;
        var changes = events.Where(e => e.Field == "status").Select(e => (At: Parse(e.At), e.Old, e.New)).Where(e => e.At is not null).ToList();
        var filed = created is not null && Parse(created.At) is { } c ? c : dated ?? changes.FirstOrDefault().At ?? DateTime.MinValue;

        // Filed as: the created event's status; else the status the first change left; else (no history) what it is now.
        var state = created?.New ?? changes.FirstOrDefault().Old ?? t.Status;
        var open = new List<(DateTime From, DateTime? To)>();
        var closings = new List<Closing>();
        DateTime? openSince = null;
        if (state == TicketRules.Open) openSince = filed;
        else if (created is not null) closings.Add(new Closing(filed, state, null)); // filed already closed

        foreach (var (at, _, to) in changes)
        {
            var when = at!.Value < filed ? filed : at.Value;
            if (to == TicketRules.Open && openSince is null)
            {
                // Reopened (an undone close, or it came back): the closing didn't stick, so it's taken back and the ticket
                // has been open since it was first opened, as if it had never closed.
                if (closings.Count > 0) closings.RemoveAt(closings.Count - 1);
                if (open.Count > 0) { openSince = open[^1].From; open.RemoveAt(open.Count - 1); }
                else openSince = when;
            }
            else if (to != TicketRules.Open && openSince is { } since)
            {
                open.Add((since, when));
                closings.Add(new Closing(when, to ?? TicketRules.Closed, when - since));
                openSince = null;
            }
            else if (to != TicketRules.Open && closings.Count > 0 && closings[^1].Status != to)
                closings[^1] = closings[^1] with { Status = to ?? TicketRules.Closed }; // closed, then corrected to wont-do: one closing
            state = to ?? state;
        }
        if (openSince is { } still) open.Add((still, null));
        var beforeHistory = t.Status != TicketRules.Open && created is null && changes.Count == 0;
        return new Life(repo, t, filed, open, closings, beforeHistory);
    }

    private static DateTime? Parse(string at) =>
        DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    private static CycleStats Cycle(string group, IEnumerable<TimeSpan> spans)
    {
        var sorted = spans.Order().ToList();
        if (sorted.Count == 0) return new CycleStats(group, 0, null, null, null);
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2);
        var mean = TimeSpan.FromTicks((long)sorted.Average(s => (double)s.Ticks));
        var p85 = sorted[Math.Max(0, (int)Math.Ceiling(0.85 * sorted.Count) - 1)]; // nearest rank
        return new CycleStats(group, sorted.Count, median, mean, p85);
    }

    /// <summary>The start of the period holding <paramref name="at"/>.</summary>
    public static DateTime Floor(DateTime at, StatsPeriod by)
    {
        var day = DateTime.SpecifyKind(at.Date, DateTimeKind.Utc);
        return by switch
        {
            StatsPeriod.Day => day,
            StatsPeriod.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            _ => new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private static DateTime Step(DateTime start, StatsPeriod by, int n) => by switch
    {
        StatsPeriod.Day => start.AddDays(n),
        StatsPeriod.Week => start.AddDays(7 * n),
        _ => start.AddMonths(n),
    };

    /// <summary>A period's name: its day (a week's Monday), or its month.</summary>
    public static string Label(DateTime start, StatsPeriod by) =>
        start.ToString(by == StatsPeriod.Month ? "yyyy-MM" : "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parses day, week or month (any case, also d/w/m); null for anything else.</summary>
    public static StatsPeriod? ParsePeriod(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "day" or "days" or "daily" or "d" => StatsPeriod.Day,
        "week" or "weeks" or "weekly" or "w" => StatsPeriod.Week,
        "month" or "months" or "monthly" or "m" => StatsPeriod.Month,
        _ => null,
    };

    /// <summary>A duration as people read it: minutes under an hour, hours under two days, else days; "—" for none.</summary>
    public static string Duration(TimeSpan? span) => span switch
    {
        null => "—",
        { TotalHours: < 1 } s => $"{Math.Max(0, (int)Math.Round(s.TotalMinutes))}m",
        { TotalHours: < 48 } s => s.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + "h",
        { } s => s.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + "d",
    };
}
