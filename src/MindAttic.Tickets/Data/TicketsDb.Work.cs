using System.Globalization;
using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Data;

/// <summary>
/// A ticket's work attributes (M.TIX-T134): the member it's assigned to (by login email), the tokens reserved for it, and the
/// session working it now: the login it runs on and its session id, as the Orchestrator writes them while it runs the work.
/// </summary>
public sealed record TicketWork(string? Assignee, long Reserved, string? Login, string? Session, string? Since)
{
    public static readonly TicketWork None = new(null, 0, null, null, null);

    /// <summary>Whether a session is working the ticket now.</summary>
    public bool IsWorking => Login is not null || Session is not null;

    internal bool IsEmpty => Assignee is null && Reserved == 0 && !IsWorking;
}

/// <summary>A change to a ticket's assignee: who had it before and who has it now (null: nobody).</summary>
public sealed record AssignChange(string TicketId, string? From, string? To)
{
    /// <summary>What it did, in words, by the members' emails.</summary>
    public string Text => (From, To) switch
    {
        (null, null) => $"{TicketId} wasn't assigned to anyone.",
        (null, { } to) => $"{TicketId} is assigned to {to}: it's dispatched only on that login.",
        ({ } from, null) => $"{TicketId} is no longer assigned to {from}: it's handed out like any unassigned ticket.",
        ({ } from, { } to) when from == to => $"{TicketId} was already assigned to {to}.",
        ({ } from, { } to) => $"{TicketId} is reassigned from {from} to {to}: it's dispatched only on {to}'s login.",
    };
}

public sealed partial class TicketsDb
{
    /// <summary>The most tokens that can be reserved: a quadrillion, far past any limit (the Orchestrator's own bound).</summary>
    public const long MaxReserved = 1_000_000_000_000_000;

    /// <summary>A ticket's work attributes (none for a ticket never assigned, reserved or worked).</summary>
    public TicketWork Work(string id) =>
        Rows("SELECT assignee, reserved, login, session, since FROM ticket_work WHERE ticket_id = $id COLLATE NOCASE", r => ReadWork(r, 0), ("$id", CurrentId(id.Trim())))
            .DefaultIfEmpty(TicketWork.None).First();

    /// <summary>Every ticket's work attributes that has any, by ticket id (any case).</summary>
    public IReadOnlyDictionary<string, TicketWork> WorkStates() =>
        Rows("SELECT ticket_id, assignee, reserved, login, session, since FROM ticket_work", r => (r.GetString(0), ReadWork(r, 1)))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every assigned ticket → its assignee's email.</summary>
    public IReadOnlyDictionary<string, string> Assignees() =>
        WorkStates().Where(kv => kv.Value.Assignee is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Assignee!, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every ticket with tokens reserved → how many.</summary>
    public IReadOnlyDictionary<string, long> Reservations() =>
        WorkStates().Where(kv => kv.Value.Reserved > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Reserved, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Assigns a ticket to the member signed in as <paramref name="email"/>, or reassigns it from whoever had it: the Orchestrator
    /// then dispatches it only on that login. Recorded in its history. Throws <see cref="ArgumentException"/> for an address that
    /// isn't one.
    /// </summary>
    public AssignChange Assign(string id, string email)
    {
        var to = NormalizeEmail(email) ?? throw new ArgumentException($"\"{email}\" isn't an email address.", nameof(email));
        return SetAssignee(id, to);
    }

    /// <summary>Takes a ticket off whoever it was assigned to.</summary>
    public AssignChange Unassign(string id) => SetAssignee(id, null);

    /// <summary>"Redelegate now": the assignment is dropped, so the ticket is handed on like an unassigned one (another member
    /// with room, else the API key).</summary>
    public AssignChange Redelegate(string id) => SetAssignee(id, null);

    private AssignChange SetAssignee(string id, string? to)
    {
        AssignChange change = null!;
        InWrite(() =>
        {
            var t = RequireTicket(id);
            var work = Work(t.Id);
            change = new AssignChange(t.Id, work.Assignee, to);
            if (work.Assignee == to) return;
            SaveWork(t.Id, work with { Assignee = to });
            Event(t.Id, "assignee", work.Assignee, to);
        });
        return change;
    }

    /// <summary>
    /// Sets aside <paramref name="tokens"/> uncached tokens for a ticket so background work can't spend them first; 0 releases the
    /// reservation, which works even for a ticket gone since. Recorded in its history.
    /// </summary>
    public void Reserve(string id, long tokens)
    {
        if (tokens is < 0 or > MaxReserved) throw new ArgumentOutOfRangeException(nameof(tokens), $"Reserve 0 to {MaxReserved} tokens.");
        InWrite(() =>
        {
            var key = tokens > 0 || Ticket(id) is not null ? RequireTicket(id).Id : id.Trim();
            var work = Work(key);
            if (work.Reserved == tokens) return;
            SaveWork(key, work with { Reserved = tokens });
            if (Ticket(key) is not null)
                Event(key, "reserved", work.Reserved.ToString(CultureInfo.InvariantCulture), tokens.ToString(CultureInfo.InvariantCulture));
        });
    }

    /// <summary>Records that a session is working the ticket now, on <paramref name="login"/> (its account email) and/or as
    /// <paramref name="session"/>: the Orchestrator writes it as it dispatches, and Tickets Desktop links back to it.</summary>
    public void SetWorking(string id, string? login, string? session)
    {
        var who = login is { Length: > 0 } ? NormalizeEmail(login) ?? throw new ArgumentException($"\"{login}\" isn't an email address.", nameof(login)) : null;
        var what = string.IsNullOrWhiteSpace(session) ? null : session.Trim();
        if (who is null && what is null) throw new ArgumentException("Say which login or session is working it.");
        InWrite(() =>
        {
            var t = RequireTicket(id);
            var work = Work(t.Id);
            if (work.Login == who && work.Session == what) return;
            SaveWork(t.Id, work with { Login = who, Session = what, Since = Now() });
            Event(t.Id, "working", Describe(work), Describe(work with { Login = who, Session = what }));
        });
    }

    /// <summary>Clears the session working the ticket (its work finished, or was released).</summary>
    public void ClearWorking(string id) => InWrite(() =>
    {
        var t = RequireTicket(id);
        var work = Work(t.Id);
        if (!work.IsWorking) return;
        SaveWork(t.Id, work with { Login = null, Session = null, Since = null });
        Event(t.Id, "working", Describe(work), null);
    });

    private static string? Describe(TicketWork w) => (w.Login, w.Session) switch
    {
        (null, null) => null,
        ({ } l, null) => l,
        (null, { } s) => s,
        ({ } l, { } s) => $"{l} ({s})",
    };

    private void SaveWork(string id, TicketWork w)
    {
        if (w.IsEmpty)
        {
            Exec("DELETE FROM ticket_work WHERE ticket_id = $id", null, ("$id", id));
            return;
        }
        Exec(server is null
                ? "INSERT INTO ticket_work (ticket_id, assignee, reserved, login, session, since, updated) VALUES ($id, $a, $r, $l, $s, $since, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')) " +
                  "ON CONFLICT (ticket_id) DO UPDATE SET assignee = excluded.assignee, reserved = excluded.reserved, login = excluded.login, " +
                  "session = excluded.session, since = excluded.since, updated = excluded.updated"
                : "UPDATE ticket_work SET assignee = $a, reserved = $r, login = $l, session = $s, since = $since, updated = strftime('%Y-%m-%dT%H:%M:%SZ', 'now') WHERE ticket_id = $id; " +
                  "IF @@ROWCOUNT = 0 INSERT INTO ticket_work (ticket_id, assignee, reserved, login, session, since, updated) VALUES ($id, $a, $r, $l, $s, $since, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))",
            null, ("$id", id), ("$a", w.Assignee), ("$r", w.Reserved), ("$l", w.Login), ("$s", w.Session), ("$since", w.Since));
    }

    private static TicketWork ReadWork(System.Data.Common.DbDataReader r, int at) => new(
        r.IsDBNull(at) ? null : r.GetString(at), Convert.ToInt64(r.GetValue(at + 1), CultureInfo.InvariantCulture),
        r.IsDBNull(at + 2) ? null : r.GetString(at + 2), r.IsDBNull(at + 3) ? null : r.GetString(at + 3), r.IsDBNull(at + 4) ? null : r.GetString(at + 4));

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>An email address trimmed and lower-cased, or null when <paramref name="email"/> isn't one.</summary>
    public static string? NormalizeEmail(string? email) =>
        email?.Trim() is { Length: > 0 } e && Email().IsMatch(e) ? e.ToLowerInvariant() : null;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();

    // ── Blocking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes <paramref name="id"/> wait for <paramref name="by"/> (each a <c>requires</c> link); <paramref name="clear"/> first drops
    /// what it required, so with <paramref name="by"/> it replaces them. Everything is checked before anything changes (each
    /// ticket exists, none is itself, none would close a cycle), and it's one transaction. Returns what it requires now.
    /// </summary>
    public IReadOnlyList<string> Block(string id, IEnumerable<string> by, bool clear = false)
    {
        var wanted = by.Select(b => b.Trim()).Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<string> now = [];
        InWrite(() =>
        {
            var t = RequireTicket(id);
            var missing = wanted.Where(b => Ticket(b) is null).ToList();
            if (missing.Count > 0) throw new KeyNotFoundException($"There's no ticket {string.Join(", ", missing)}. Nothing changed.");
            var targets = wanted.Select(b => RequireTicket(b).Id).ToList();
            if (targets.Contains(t.Id, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{t.Id} can't require itself. Nothing changed.");
            foreach (var b in targets)
                if (Requires(b, transitive: true).Contains(t.Id, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{b} already (indirectly) requires {t.Id}; that would be a cycle. Nothing changed.");
            if (clear)
                foreach (var to in Requires(t.Id)) RemoveLink(t.Id, to, "requires");
            foreach (var b in targets) AddLinkCore(t.Id, b, "requires", "blocked by hand");
            now = Requires(t.Id);
        });
        return now;
    }
}
