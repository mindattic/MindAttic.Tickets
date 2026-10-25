using System.Data;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>M.TIX-T86: the Stats tab shows throughput, cycle time and open tickets by repo and type as real tables.</summary>
public sealed class StatsViewTests
{
    private string root = "";
    private MainViewModel model = null!;

    [SetUp]
    public async Task SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "wf-desktop-stats-" + Guid.NewGuid().ToString("N"));
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
        var alpha = Make("Alpha", "AL", "| AL-T1 | 2026-01-05 | fix | open | alpha one | — |\n| AL-T2 | 2026-01-05 | feature | open | alpha two | — |\n");
        var beta = Make("Beta", "BE", "| BE-T1 | 2026-01-05 | fix | open | beta one | — |\n");
        using (var db = TicketsDb.Open(TicketsDb.DefaultPath(root)))
        {
            db.Import([alpha, beta]);
            db.Close("AL-T1", TicketRules.Closed, "abc123");
        }
        model = new MainViewModel(new TicketStore(root), new DesktopSettings());
        await model.RefreshAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        for (var attempt = 1; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50); }
        }
    }

    private static IEnumerable<string> Headers(DataView view) => view.Table!.Columns.Cast<DataColumn>().Select(c => c.Caption);

    [Test]
    public async Task The_stats_tab_fills_four_captioned_tables_for_the_period_and_repo_filter_only_while_shown()
    {
        Assert.That(model.Views, Does.Contain(MainViewModel.StatsView).And.Contain(MainViewModel.ActivityView));
        Assert.That(model.Metrics, Is.Null, "nothing is worked out until the tab is shown");

        model.SelectedView = MainViewModel.StatsView;
        await model.RefreshAsync();
        Assert.That(model.ShowsTicketFilters, Is.False);

        Assert.That(Headers(model.ThroughputTable!), Is.EqualTo(new[] { "Week of", "Filed", "Closed", "Wont-do", "Open at end" }));
        Assert.That(model.ThroughputTable!.Count, Is.EqualTo(8));
        Assert.That(model.ThroughputTable.Table!.Rows.Cast<DataRow>().Sum(r => (int)r[2]), Is.EqualTo(1), "AL-T1 closed this week");
        Assert.That((int)model.ThroughputTable.Table.Rows[^1][4], Is.EqualTo(2));
        Assert.That(Headers(model.CycleTable!), Is.EqualTo(new[] { "Type", "Closed", "Median", "Mean", "85th percentile" }));
        Assert.That(model.CycleTable!.Table!.Rows[0][0], Is.EqualTo(TicketStats.AllTypes));
        Assert.That(model.OpenByRepoTable!.Table!.Rows.Cast<DataRow>().Select(r => (string)r[0]), Is.EquivalentTo(new[] { "Alpha", "Beta" }));
        Assert.That(Headers(model.OpenByTypeTable!).First(), Is.EqualTo("Type"));
        Assert.That(model.OpenByTypeTable!.Table!.Columns, Has.Count.EqualTo(9), "a column per week");
        Assert.That(model.OpenByTypeTable.Table.Columns.Cast<DataColumn>().Select(c => c.ColumnName), Is.All.Match("^c[0-9]+$"), "plain names the grid can bind");
        Assert.That(model.StatusMessage, Does.StartWith("Stats: 8 weeks, 1 closed, 2 open now."));

        model.StatsBy = MainViewModel.StatsMonth;
        Assert.That(Headers(model.ThroughputTable!).First(), Is.EqualTo("Month"));
        Assert.That(model.ThroughputTable!.Count, Is.EqualTo(6));

        model.RepoFilter = "Beta";
        Assert.That(model.OpenByRepoTable!.Table!.Rows.Cast<DataRow>().Select(r => (string)r[0]), Is.EqualTo(new[] { "Beta" }));
        Assert.That(model.StatsSummary, Does.Contain("for Beta"));
    }

    [Test]
    public void Every_static_resource_main_window_uses_exists_so_the_window_loads() => Sta(() =>
    {
        var src = RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(src, "MainWindow.xaml"));
        var local = Regex.Matches(xaml, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var styles = LoadTheme("Styles.xaml");
        var missing = Regex.Matches(xaml, @"StaticResource ([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !local.Contains(k) && !styles.Contains(k)).ToList();
        Assert.That(missing, Is.Empty);
    });

    [Test]
    public void A_stats_table_is_a_read_only_grid_headed_by_its_captions_with_numbers_right_aligned() => Sta(() =>
    {
        var table = new DataTable("Throughput");
        MainViewModel.Column(table, "Week of", typeof(string));
        MainViewModel.Column(table, "Open at end", typeof(int));
        table.Rows.Add("2026-10-19", 3);
        var grid = new DataGrid { ItemsSource = table.DefaultView };
        grid.AutoGeneratingColumn += (s, e) => MainWindow.OnStatsColumn(s!, e);
        var window = new Window
        {
            Content = grid, Width = 400, Height = 200, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Resources.MergedDictionaries.Add(LoadTheme("Styles.xaml"));
        try
        {
            grid.Style = (Style)window.FindResource("StatsTable");
            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });

            Assert.That(grid.IsReadOnly, Is.True);
            Assert.That(grid.HeadersVisibility, Is.EqualTo(DataGridHeadersVisibility.Column));
            Assert.That(grid.Columns.Select(c => c.Header), Is.EqualTo(new object[] { "Week of", "Open at end" }));
            var number = (DataGridTextColumn)grid.Columns[1];
            Assert.That(number.ElementStyle?.Setters.OfType<Setter>().Any(s => s.Property == TextBlock.TextAlignmentProperty && Equals(s.Value, TextAlignment.Right)), Is.True);
            Assert.That(UIElementAutomationPeer.CreatePeerForElement(grid)?.GetAutomationControlType(), Is.EqualTo(AutomationControlType.DataGrid),
                "read by a screen reader as a table, with its column headers");
        }
        finally { window.Close(); }
    });

    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!Path.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repo root above the tests");
        return Path.Combine(root, "src", "MindAttic.Tickets.Desktop");
    }

    private static ResourceDictionary LoadTheme(string relative)
    {
        using var stream = File.OpenRead(Path.Combine(RepoRoot(), "Theme", relative));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static void Sta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
