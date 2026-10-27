using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindAttic.Tickets.Desktop.Bus;

/// <summary>
/// The app bus shared with MindAttic.Orchestrator.Desktop (M.ORCH-T879, M.TIX-T158): the Orchestrator daemon's status pipes, each
/// line one envelope <c>{"kind","v","at","pid","payload"}</c> (NDJSON, UTF-8, no BOM). This is a copy of the parts of the
/// Orchestrator's contract (<c>src/MindAttic.Orchestrator.Core/Status/AppBus.cs</c>, described in its <c>docs/app-bus.md</c>) that
/// Tickets.Desktop reads and writes: the Orchestrator's Core references the MindAttic.Tickets package, so Tickets can't reference
/// Core back. It depends on nothing but System.Text.Json, so a shared relay (MAT-T60) can replace it wholesale.
/// </summary>
public static class AppBus
{
    public const string Ticket = "ticket";
    public const string OpenTicket = "open-ticket";
    public const string ShowWorker = "show-worker";
    public const string Presence = "app-present";

    /// <summary>The version of each kind's payload this build reads and writes; a later version is skipped.</summary>
    public const int Version = 1;

    public const string OrchestratorDesktop = "orchestrator-desktop";
    public const string TicketsDesktop = "tickets-desktop";

    /// <summary>The daemon's stream (daemon to subscribers): <c>mindattic-orchestrator-status-&lt;user&gt;</c>.</summary>
    public static string StreamPipeName { get; } = "mindattic-orchestrator-status-" + UserPart(Environment.UserName);

    /// <summary>The daemon's inbox (writers to daemon): <c>mindattic-orchestrator-status-in-&lt;user&gt;</c>.</summary>
    public static string InboxPipeName { get; } = "mindattic-orchestrator-status-in-" + UserPart(Environment.UserName);

    /// <summary>The user name as the pipe names carry it: every character that isn't a letter or digit becomes '-'.</summary>
    public static string UserPart(string user) => new(user.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>A stream silent this long is dead (heartbeats come every 20 seconds).</summary>
    public static readonly TimeSpan SilentFor = TimeSpan.FromSeconds(65);

    /// <summary>The longest line read: a longer one is skipped.</summary>
    public const int MaxLine = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One envelope line (no newline) of <paramref name="kind"/> carrying <paramref name="payload"/>, from this process now.</summary>
    public static string Serialize<T>(string kind, T payload, DateTime? atUtc = null, int? pid = null) =>
        JsonSerializer.Serialize(new BusEnvelope
        {
            Kind = kind, V = Version, AtUtc = atUtc ?? DateTime.UtcNow, Pid = pid ?? Environment.ProcessId,
            Payload = JsonSerializer.SerializeToElement(payload, Json),
        }, Json);

    /// <summary>The envelope on <paramref name="line"/>; null when it isn't one (not JSON, no kind). Never throws.</summary>
    public static BusEnvelope? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLine) return null;
        try { return JsonSerializer.Deserialize<BusEnvelope>(line, Json) is { Kind.Length: > 0 } e ? e : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>The payload of <paramref name="e"/> as <typeparamref name="T"/>, when it's a version this build reads; else null.</summary>
    public static T? PayloadAs<T>(BusEnvelope e) where T : class
    {
        if (e.V > Version || e.Payload.ValueKind != JsonValueKind.Object) return null;
        try { return e.Payload.Deserialize<T>(Json); }
        catch (JsonException) { return null; }
    }
}

public sealed record BusEnvelope
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("v")] public int V { get; init; } = 1;
    [JsonPropertyName("at")] public DateTime AtUtc { get; init; }
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("payload")] public JsonElement Payload { get; init; }
}

/// <summary>The <see cref="AppBus.Ticket"/> payload (the fields Tickets.Desktop uses; the rest are skipped).</summary>
public sealed record TicketBusEvent
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("repo")] public string? Repo { get; init; }
    [JsonPropertyName("seq")] public long? Seq { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
}

/// <summary>The <see cref="AppBus.OpenTicket"/> payload.</summary>
public sealed record OpenTicketRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("from")] public string? From { get; init; }
    [JsonPropertyName("to")] public string? To { get; init; }
}

/// <summary>The <see cref="AppBus.ShowWorker"/> payload: <see cref="Login"/> and <see cref="Session"/> from <c>ticket_work</c>.</summary>
public sealed record ShowWorkerRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("login")] public string? Login { get; init; }
    [JsonPropertyName("session")] public string? Session { get; init; }
    [JsonPropertyName("from")] public string? From { get; init; }
    [JsonPropertyName("to")] public string? To { get; init; }
}

/// <summary>The <see cref="AppBus.Presence"/> payload.</summary>
public sealed record AppPresence
{
    [JsonPropertyName("app")] public string App { get; init; } = "";
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("handles")] public IReadOnlyList<string> Handles { get; init; } = [];
    [JsonPropertyName("atUtc")] public DateTime AtUtc { get; init; }
}
