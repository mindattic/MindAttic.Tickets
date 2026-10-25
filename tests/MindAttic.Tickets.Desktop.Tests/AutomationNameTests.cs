using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using MindAttic.Tickets.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// M.TIX-T71: a screen reader hears a name for every control that has no visible text of its own (WCAG 4.1.2,
/// 2.5.3), using the project's real Theme/Styles.xaml (which merges MindAttic.Components.Wpf's Theme.xaml itself,
/// M.TIX-T91) so a regression there (the AutomationProperties.Name setter on RowItem/GridRow, or the HelpText
/// setter on MaButton/MaToolButton) fails this test rather than only showing up under Narrator. Follows the
/// STA-thread harness and UIElementAutomationPeer/peer.GetName() pattern from MindAttic.Components.Wpf.Tests.MaThemeTests.
/// </summary>
public sealed class AutomationNameTests
{
    private static ResourceDictionary LoadTheme(string relative)
    {
        var root = AppContext.BaseDirectory;
        while (!Path.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repo root above the tests");
        using var stream = File.OpenRead(Path.Combine(root, "src", "MindAttic.Tickets.Desktop", "Theme", relative));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    /// <summary>A real window that never appears (far off every screen, invisible, never activated), carrying the
    /// app's own Colors.xaml and Styles.xaml so styled controls look and behave as they do in the real app.</summary>
    private static Window OffScreenWindow(object? content = null)
    {
        var window = new Window
        {
            Content = content, Width = 300, Height = 200, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Opacity = 0,
            ShowInTaskbar = false, ShowActivated = false, Title = "MindAttic.Tickets.Desktop.Tests (off-screen)",
        };
        window.Resources.MergedDictionaries.Add(LoadTheme("Colors.xaml"));
        window.Resources.MergedDictionaries.Add(LoadTheme("Styles.xaml"));
        return window;
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });

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

    [Test]
    public void A_queue_row_is_named_by_its_spoken_sentence_not_left_to_read_its_fields_one_by_one() => Sta(() =>
    {
        // The same shape MainWindow.xaml uses for the Queue and Board lists: a ListBox styled RowList (which sets
        // ItemContainerStyle to RowItem), bound to real TicketRowViewModel rows.
        var row = new TicketRowViewModel("AL-T1") { Rank = 1 };
        var list = new ListBox { ItemsSource = new[] { row } };
        var window = OffScreenWindow(list);
        try
        {
            list.Style = (Style)window.FindResource("RowList");
            window.Show();
            Pump();

            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
            var peer = UIElementAutomationPeer.CreatePeerForElement(container);
            Assert.That(peer, Is.Not.Null);
            Assert.That(peer!.GetName(), Is.EqualTo(row.SpokenName));
            Assert.That(peer.GetName(), Does.Contain("AL-T1"));

            row.Rank = 2;
            Pump();
            Assert.That(peer.GetName(), Is.EqualTo(row.SpokenName), "the row's spoken name re-raises so a live re-rank is heard");
        }
        finally { window.Close(); }
    });

    [Test]
    public void A_grid_row_is_also_named_by_its_spoken_sentence() => Sta(() =>
    {
        // The same shape MainWindow.xaml uses for the Tickets grid: a ListView with a GridView view and
        // ItemContainerStyle="{StaticResource GridRow}".
        var row = new TicketRowViewModel("BE-T2");
        var list = new ListView
        {
            ItemsSource = new[] { row },
            View = new GridView { Columns = { new GridViewColumn { Header = "ID" } } },
        };
        var window = OffScreenWindow(list);
        try
        {
            list.ItemContainerStyle = (Style)window.FindResource("GridRow");
            window.Show();
            Pump();

            var container = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(row);
            var peer = UIElementAutomationPeer.CreatePeerForElement(container);
            Assert.That(peer!.GetName(), Is.EqualTo(row.SpokenName));
        }
        finally { window.Close(); }
    });

    [TestCase("MaButton")]
    [TestCase("MaToolButton")]
    [TestCase("MaPrimaryButton")]
    public void A_glyph_only_button_still_announces_a_name_and_its_role(string styleKey) => Sta(() =>
    {
        var button = new Button { Content = "✔" };
        AutomationProperties.SetName(button, "Approve AL-T1");
        var window = OffScreenWindow(button);
        try
        {
            button.Style = (Style)window.FindResource(styleKey);
            window.Show();
            Pump();

            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            Assert.That(peer, Is.Not.Null);
            Assert.That(peer!.GetName(), Is.EqualTo("Approve AL-T1"));
            Assert.That(peer.GetAutomationControlType(), Is.EqualTo(AutomationControlType.Button));
        }
        finally { window.Close(); }
    });

    [Test]
    public void The_live_announcer_is_a_named_polite_live_region() => Sta(() =>
    {
        var announcer = new TextBlock
        {
            Width = 1, Height = 1, Opacity = 0, Focusable = false,
            Text = "BE-T1 approved.",
        };
        AutomationProperties.SetName(announcer, "Announcement");
        AutomationProperties.SetLiveSetting(announcer, AutomationLiveSetting.Polite);
        var window = OffScreenWindow(announcer);
        try
        {
            window.Show();
            Pump();

            var peer = UIElementAutomationPeer.CreatePeerForElement(announcer);
            Assert.That(peer, Is.Not.Null);
            Assert.That(peer!.GetName(), Is.EqualTo("Announcement"));
            Assert.That(peer.GetLiveSetting(), Is.EqualTo(AutomationLiveSetting.Polite));
        }
        finally { window.Close(); }
    });
}
