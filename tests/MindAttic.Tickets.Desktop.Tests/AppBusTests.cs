using System.Text.Json;
using MindAttic.Tickets.Desktop.Bus;
using NUnit.Framework;

namespace MindAttic.Tickets.Desktop.Tests;

/// <summary>
/// The app bus client (M.TIX-T158): the envelope lines it writes match the Orchestrator's contract (docs/app-bus.md), and the
/// lines it reads raise the right events, skipping kinds, versions and targets that aren't its own. No pipe is touched.
/// </summary>
public sealed class AppBusTests
{
    [Test]
    public void The_pipe_names_follow_the_contract()
    {
        Assert.That(AppBus.UserPart("ryan.d bra_al"), Is.EqualTo("ryan-d-bra-al"));
        Assert.That(AppBus.StreamPipeName, Does.StartWith("mindattic-orchestrator-status-"));
        Assert.That(AppBus.InboxPipeName, Is.EqualTo(AppBus.StreamPipeName.Replace("-status-", "-status-in-")));
    }

    [Test]
    public void Presence_is_written_as_an_envelope_v1()
    {
        var at = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var line = AppBus.Serialize(AppBus.Presence, new AppPresence { App = AppBus.TicketsDesktop, Pid = 42, Handles = [AppBus.OpenTicket], AtUtc = at }, at, 42);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo("app-present"));
        Assert.That(root.GetProperty("v").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("pid").GetInt32(), Is.EqualTo(42));
        Assert.That(root.GetProperty("at").GetDateTime(), Is.EqualTo(at));
        var payload = root.GetProperty("payload");
        Assert.That(payload.GetProperty("app").GetString(), Is.EqualTo("tickets-desktop"));
        Assert.That(payload.GetProperty("handles")[0].GetString(), Is.EqualTo("open-ticket"));
        Assert.That(line, Does.Not.Contain("\n"));
    }

    [Test]
    public void Show_worker_leaves_out_what_it_does_not_know()
    {
        var line = AppBus.Serialize(AppBus.ShowWorker, new ShowWorkerRequest { Id = "M.TIX-T158", Login = "a@b.com", From = AppBus.TicketsDesktop, To = AppBus.OrchestratorDesktop });
        Assert.That(line, Does.Contain("\"id\":\"M.TIX-T158\"").And.Contain("\"login\":\"a@b.com\"").And.Not.Contain("session"));
    }

    [Test]
    public void A_ticket_line_raises_a_ticket_change()
    {
        using var bus = new AppBusClient("unused-stream", "unused-inbox");
        var seen = new List<string>();
        bus.TicketChanged += t => seen.Add(t.Id + ":" + t.Event);
        bus.Handle("""{"kind":"ticket","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"ID":"M.ORCH-T879","event":"approved","seq":7,"source":"tickets-db"}}""");
        bus.Handle("""{"kind":"ticket","v":2,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"id":"M.ORCH-T1","event":"approved"}}""");
        bus.Handle("""{"kind":"heartbeat","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{}}""");
        bus.Handle("not json");
        bus.Handle("");
        Assert.That(seen, Is.EqualTo(new[] { "M.ORCH-T879:approved" }), "a later version, another kind and junk are skipped");
    }

    [Test]
    public void Open_ticket_is_taken_only_when_it_is_meant_for_tickets_desktop()
    {
        using var bus = new AppBusClient("unused-stream", "unused-inbox");
        var opened = new List<string>();
        bus.OpenTicketRequested += opened.Add;
        bus.Handle("""{"kind":"open-ticket","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"id":"m.orch-t879","from":"orchestrator-desktop","to":"tickets-desktop"}}""");
        bus.Handle("""{"kind":"open-ticket","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"id":"M.TIX-T1"}}""");
        bus.Handle("""{"kind":"open-ticket","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"id":"M.TIX-T2","to":"someone-else"}}""");
        bus.Handle("""{"kind":"open-ticket","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"id":"--evil arg"}}""");
        Assert.That(opened, Is.EqualTo(new[] { "M.ORCH-T879", "M.TIX-T1" }));
    }

    [Test]
    public void A_presence_counts_only_while_its_process_lives_and_handles_the_kind()
    {
        using var bus = new AppBusClient("unused-stream", "unused-inbox");
        bus.Handle($$$"""{"kind":"app-present","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"app":"orchestrator-desktop","pid":{{{Environment.ProcessId}}},"handles":["show-worker"]}}""");
        Assert.That(bus.Present(AppBus.OrchestratorDesktop, AppBus.ShowWorker), Is.Not.Null);
        Assert.That(bus.Present(AppBus.OrchestratorDesktop, AppBus.OpenTicket), Is.Null, "it doesn't handle that");

        bus.Handle("""{"kind":"app-present","v":1,"at":"2026-10-25T12:00:00Z","pid":1,"payload":{"app":"orchestrator-desktop","pid":0,"handles":["show-worker"]}}""");
        Assert.That(bus.Present(AppBus.OrchestratorDesktop, AppBus.ShowWorker), Is.Null, "no live process");
    }

    [Test]
    public void Sending_with_no_daemon_fails_quietly()
    {
        using var bus = new AppBusClient("unused-stream", "mindattic-tickets-test-no-such-inbox-" + Guid.NewGuid().ToString("N"));
        Assert.That(bus.SendPresence(), Is.False);
        Assert.That(bus.TryShowWorker("M.TIX-T1", null, null), Is.False, "the dashboard isn't present");
    }
}
