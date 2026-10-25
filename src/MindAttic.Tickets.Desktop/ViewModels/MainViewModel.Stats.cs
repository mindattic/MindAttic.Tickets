using System.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The Stats tab (M.TIX-T86): what <c>tickets ticket stats</c> prints, as four tables a screen reader reads as tables (a
/// DataGrid each, with column headers): throughput per period, cycle time by type, and the tickets open at the end of each
/// period by repo and by type. Worked out from the snapshot's tickets and their ticket_events history, narrowed by the repo
/// filter, only while the tab is shown.
/// </summary>
public sealed partial class MainViewModel
{
    public const string StatsView = "Stats";

    /// <summary>The period lengths the Stats tab offers.</summary>
    public const string StatsDay = "Day", StatsWeek = "Week", StatsMonth = "Month";

    private string statsBy = StatsWeek, statsSummary = "";
    private TicketMetrics? metrics;
    private DataView? throughputTable, cycleTable, openByRepoTable, openByTypeTable;

    /// <summary>Now, in UTC, for the Stats tab's periods; tests pin it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public bool IsStatsView => SelectedView == StatsView;

    public IReadOnlyList<string> StatsByChoices { get; } = [StatsDay, StatsWeek, StatsMonth];

    /// <summary>The Stats tab's period: Day (the last 14), Week (the last 8) or Month (the last 6).</summary>
    public string StatsBy
    {
        get => statsBy;
        set { if (Set(ref statsBy, StatsByChoices.Contains(value) ? value : StatsWeek)) ApplyFilters(); }
    }

    /// <summary>How many periods the Stats tab shows for <paramref name="by"/>.</summary>
    public static int PeriodsFor(StatsPeriod by) => by switch { StatsPeriod.Day => 14, StatsPeriod.Week => 8, _ => 6 };

    /// <summary>The metrics shown, null until the Stats tab has been opened.</summary>
    public TicketMetrics? Metrics { get => metrics; private set => Set(ref metrics, value); }

    public DataView? ThroughputTable { get => throughputTable; private set => Set(ref throughputTable, value); }
    public DataView? CycleTable { get => cycleTable; private set => Set(ref cycleTable, value); }
    public DataView? OpenByRepoTable { get => openByRepoTable; private set => Set(ref openByRepoTable, value); }
    public DataView? OpenByTypeTable { get => openByTypeTable; private set => Set(ref openByTypeTable, value); }

    /// <summary>What the numbers cover, and what they leave out.</summary>
    public string StatsSummary { get => statsSummary; private set => Set(ref statsSummary, value); }

    /// <summary>Works the metrics out again for the repo filter and period, when the Stats tab is the one shown.</summary>
    private void ApplyStats(Snapshot s)
    {
        if (!IsStatsView) return;
        var by = TicketStats.ParsePeriod(StatsBy) ?? StatsPeriod.Week;
        var tickets = s.Tickets.Where(t => RepoFilter == AllRepos || t.Repo.Equals(RepoFilter, StringComparison.OrdinalIgnoreCase))
            .Select(t => (t.Repo, t.Ticket));
        var m = TicketStats.Compute(tickets, s.StatusHistory, by, PeriodsFor(by), Clock());
        Metrics = m;

        var first = by switch { StatsPeriod.Day => "Day", StatsPeriod.Week => "Week of", _ => "Month" };
        var throughput = new DataTable("Throughput");
        Column(throughput, first, typeof(string));
        foreach (var c in new[] { "Filed", "Closed", "Wont-do", "Open at end" }) Column(throughput, c, typeof(int));
        foreach (var p in m.Periods) throughput.Rows.Add(p.Label, p.Opened, p.Closed, p.WontDo, p.OpenAtEnd);
        ThroughputTable = throughput.DefaultView;

        var cycle = new DataTable("Cycle time");
        Column(cycle, "Type", typeof(string));
        Column(cycle, "Closed", typeof(int));
        foreach (var c in new[] { "Median", "Mean", "85th percentile" }) Column(cycle, c, typeof(string));
        foreach (var c in m.CycleTime)
            cycle.Rows.Add(c.Group, c.Count, TicketStats.Duration(c.Median), TicketStats.Duration(c.Mean), TicketStats.Duration(c.P85));
        CycleTable = cycle.DefaultView;

        OpenByRepoTable = SeriesTable("Open by repo", "Repo", m.OpenByRepo, m).DefaultView;
        OpenByTypeTable = SeriesTable("Open by type", "Type", m.OpenByType, m).DefaultView;

        var scope = RepoFilter == AllRepos ? "every repo" : RepoFilter;
        var notes = new List<string>();
        if (m.FiledClosed > 0) notes.Add($"{m.FiledClosed} closing(s) were filed already closed: counted as throughput, not in cycle time");
        if (m.ClosedBeforeHistory > 0) notes.Add($"{m.ClosedBeforeHistory} ticket(s) closed before their history was kept are in none of these numbers");
        StatsSummary = $"The last {m.Periods.Count} {StatsBy.ToLowerInvariant()}s for {scope}, in UTC, from the tickets' history (also 'tickets ticket stats')."
                       + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "");
    }

    /// <summary>A name column, then a column per period (headed MM-dd, a month yyyy-MM) of the tickets open at its end.</summary>
    internal static DataTable SeriesTable(string name, string what, IReadOnlyList<OpenSeries> series, TicketMetrics m)
    {
        var table = new DataTable(name);
        Column(table, what, typeof(string));
        foreach (var p in m.Periods) Column(table, PeriodHeading(p, m.By), typeof(int));
        foreach (var s in series) table.Rows.Add([s.Name, .. s.Counts.Cast<object>()]);
        return table;
    }

    /// <summary>
    /// Adds a column headed <paramref name="caption"/>. Its name is c0, c1, ... so the DataGrid's generated binding paths stay
    /// plain identifiers ("Open at end" or "10-18" would not bind); MainWindow heads each generated column with its caption.
    /// </summary>
    internal static void Column(DataTable table, string caption, Type type) =>
        table.Columns.Add(new DataColumn($"c{table.Columns.Count}", type) { Caption = caption });

    internal static string PeriodHeading(PeriodStats p, StatsPeriod by) =>
        by == StatsPeriod.Month ? p.Label : p.Start.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
