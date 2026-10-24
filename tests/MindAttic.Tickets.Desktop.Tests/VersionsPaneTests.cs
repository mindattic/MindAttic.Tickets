using MindAttic.Tickets.Data;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Tickets.Desktop.ViewModels;
using MindAttic.Tickets.Log;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The detail pane's versions (M.TIX-T137): it opens on the current version, the arrows (Left / Right) step through the
/// older ones, each field shows that version, what it changed is marked, and actions keep acting on the ticket as it is now.
/// </summary>
public sealed class VersionsPaneTests
{
    private static readonly Link Waits = new("AL-T2", "AL-T1", "requires", null);

    private static TicketDetail Detail()
    {
        var filed = new Ticket("AL-T2", "2026-10-02", "fix", "open", "alpha two", "—");
        var now = filed with { Status = "closed", Ref = "abc123" };
        var t0 = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        TicketVersion[] versions =
        [
            new(1, t0, "Alpha", filed, TicketsDb.Proposed, 0, [], [], true),
            new(2, t0.AddHours(1), "Alpha", filed, TicketsDb.Approved, 3, [Waits], ["approval", "priority", "links"], true),
            new(3, t0.AddHours(2), "Alpha", now, TicketsDb.Approved, 3, [Waits], ["status", "ref"], true),
        ];
        return new TicketDetail(new TicketInfo("Alpha", now, TicketsDb.Approved, 3), [], [Waits], []) { Versions = versions };
    }

    [Test]
    public void It_opens_on_the_current_version_and_steps_back_to_the_first()
    {
        var d = new DetailViewModel(Detail());
        Assert.That((d.VersionNumber, d.VersionCount, d.IsCurrentVersion, d.CanGoNewer), Is.EqualTo((3, 3, true, false)));
        Assert.That(d.VersionText, Is.EqualTo("Version 3 of 3 (current)"));
        Assert.That((d.StatusChanged, d.RefChanged, d.ApprovalChanged), Is.EqualTo((true, true, false)));
        Assert.That(d.ChangesText, Is.EqualTo("Changed: Status, Ref."));

        Assert.That(d.Step(-1), Is.True);
        Assert.That((d.Status, d.Ref, d.Approval, d.Priority), Is.EqualTo(("open", "—", TicketsDb.Approved, 3L)));
        Assert.That((d.ApprovalChanged, d.PriorityChanged, d.LinksChanged, d.StatusChanged), Is.EqualTo((true, true, true, false)));

        Assert.That(d.Step(-1), Is.True);
        Assert.That((d.Approval, d.Priority, d.Links.Count, d.ChangesText), Is.EqualTo((TicketsDb.Proposed, 0L, 0, "As filed.")));
        Assert.That(d.StatusChanged || d.ApprovalChanged, Is.False, "nothing is marked on the version as filed");
        Assert.That(d.Step(-1), Is.False, "the first is the leftmost");

        Assert.That(d.IsOpen, Is.False, "actions act on the ticket now, closed, not the open version shown");
        Assert.That(d.Id, Is.EqualTo("AL-T2"));
    }

    [Test]
    public void A_refresh_keeps_an_older_version_on_screen()
    {
        var d = new DetailViewModel(Detail(), version: 1);
        Assert.That((d.VersionNumber, d.IsCurrentVersion, d.CanGoOlder, d.CanGoNewer), Is.EqualTo((1, false, false, true)));
        Assert.That(d.Step(+1) && d.Step(+1), Is.True);
        Assert.That(d.Step(+1), Is.False, "the rightmost is the current one");
    }

    [Test]
    public void A_ticket_with_one_version_shows_no_arrows()
    {
        var info = new TicketInfo("Alpha", new Ticket("AL-T1", "2026-10-01", "feature", "open", "alpha one", "—"), TicketsDb.Proposed, 0);
        var d = new DetailViewModel(new TicketDetail(info, [], [], []) { Versions = [new TicketVersion(1, DateTime.UtcNow, "Alpha", info.Ticket, TicketsDb.Proposed, 0, [], [], false)] });
        Assert.That((d.HasVersions, d.CanGoOlder, d.CanGoNewer), Is.EqualTo((false, false, false)));
    }
}
