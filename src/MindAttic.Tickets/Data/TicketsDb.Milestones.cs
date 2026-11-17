using System.Globalization;
using System.Text.RegularExpressions;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>The states a <see cref="Milestone"/> moves through: planned → active → accepting → reached, or dropped on the way.</summary>
public static class MilestoneStates
{
    public const string Planned = "planned";
    public const string Active = "active";
    public const string Accepting = "accepting";
    public const string Reached = "reached";
    public const string Dropped = "dropped";

    public static readonly IReadOnlyList<string> All = [Planned, Active, Accepting, Reached, Dropped];
}

/// <summary>What a <see cref="MilestoneCheck"/> asks for: a build that passes on main in named repos, a file or package that
/// exists, a harness run that reaches a named state, or the owner's sign-off.</summary>
public static class MilestoneCheckKinds
{
    public const string Build = "build";
    public const string Exists = "exists";
    public const string Harness = "harness";
    public const string Signoff = "signoff";

    public static readonly IReadOnlyList<string> All = [Build, Exists, Harness, Signoff];
}

/// <summary>What a <see cref="MilestoneCheck"/> last came to.</summary>
public static class MilestoneCheckResults
{
    public const string Unknown = "unknown";
    public const string Pass = "pass";
    public const string Fail = "fail";

    public static readonly IReadOnlyList<string> All = [Unknown, Pass, Fail];
}

/// <summary>
/// A milestone (M.TIX-T335): a checkable point on the way to a goal, across one or more repos. <see cref="Order"/> sorts them
/// (lower first); <see cref="Activated"/> is when it first became active and <see cref="Reached"/> / <see cref="ReachedBy"/> when
/// and by whom it was accepted as reached.
/// </summary>
public sealed record Milestone(string Id, string Name, string Goal, IReadOnlyList<string> Repos, long Order, string State,
    string Created, string Updated, string? Activated, string? Reached, string? ReachedBy)
{
    public bool IsActive => State == MilestoneStates.Active;

    /// <summary>Neither reached nor dropped: it can still take members and checks.</summary>
    public bool IsOpen => State is not (MilestoneStates.Reached or MilestoneStates.Dropped);

    /// <summary>"ID · name".</summary>
    public string Caption => $"{Id} · {Name}";
}

/// <summary>
/// One acceptance check of a milestone: evidence it's reached, not a ticket count. <see cref="Repos"/> and <see cref="Target"/>
/// say what it looks at (a build's repos; an exists check's path or package; a harness check's state). Only stored here: the
/// Orchestrator runs it and records the <see cref="Result"/>, when (<see cref="Checked"/>) and a short <see cref="Evidence"/> note.
/// </summary>
public sealed record MilestoneCheck(string MilestoneId, long Number, string Kind, string Description, IReadOnlyList<string> Repos,
    string? Target, string Result, string? Checked, string? Evidence)
{
    public bool Passed => Result == MilestoneCheckResults.Pass;
}

/// <summary>An item on a milestone: linked to it itself (<see cref="Direct"/>) or part of something that is, at any depth.
/// <see cref="Node"/> is its ticket, or a bare node for an epic or story.</summary>
public sealed record MilestoneMember(GraphNode Node, bool Direct)
{
    public string Id => Node.Id;
    public bool IsTicket => Node.IsTicket;

    /// <summary>A ticket that isn't wont-do: what a milestone's total counts.</summary>
    public bool Counts => Node.IsTicket && Node.Status != TicketRules.WontDo;

    public bool IsDone => Node.Status == TicketRules.Closed;
}

/// <summary>One change to a milestone: its creation, a state, name, goal, order or repo change, a member or a check.</summary>
public sealed record MilestoneEvent(string MilestoneId, string At, string Field, string? Old, string? New);

/// <summary>
/// Milestones (M.TIX-T335): "this set of work, across these repos, adds up to a finished thing". Kept in <c>milestones</c>,
/// <c>milestone_repos</c> and <c>milestone_checks</c>, with every change a <c>milestone_events</c> row. Tickets and epics join one
/// with an ordinary <c>part-of</c> link to its <c>MS-n</c> id, so a milestone is the top of a part-of tree and
/// <c>ticket link X part-of MS-1</c> works as it does between tickets.
/// </summary>
public sealed partial class TicketsDb
{
    private const string SqlNow = "strftime('%Y-%m-%dT%H:%M:%SZ', 'now')";

    [GeneratedRegex(@"^MS-[1-9]\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex MilestoneIdShape();

    /// <summary>Whether <paramref name="id"/> has a milestone's form, <c>MS-n</c>: never a ticket (<c>CODE-Tn</c>), story
    /// (<c>CODE-US-…</c>) or epic (<c>CODE-EPIC-…</c>) ID, whatever a repo's code.</summary>
    public static bool IsMilestoneId(string? id) => id is not null && MilestoneIdShape().IsMatch(id.Trim());

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>Every milestone (or those in <paramref name="state"/>), by order then by ID.</summary>
    public IReadOnlyList<Milestone> Milestones(string? state = null)
    {
        var repos = Rows("SELECT milestone_id, repo FROM milestone_repos ORDER BY repo", r => (Id: r.GetString(0), Repo: r.GetString(1)))
            .ToLookup(x => x.Id, x => x.Repo, StringComparer.OrdinalIgnoreCase);
        return [.. Rows("SELECT id, name, goal, ordinal, state, created, updated, activated, reached, reached_by FROM milestones " +
                        "WHERE $s IS NULL OR state = $s ORDER BY ordinal, number",
            r => new Milestone(r.GetString(0), r.GetString(1), r.GetString(2), [], Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture), r.GetString(4),
                r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)),
            ("$s", state)).Select(m => m with { Repos = [.. repos[m.Id]] })];
    }

    /// <summary>The milestone with this <c>MS-n</c> ID, or with exactly this name (any case); null if neither.</summary>
    public Milestone? Milestone(string idOrName)
    {
        var key = idOrName.Trim();
        return MilestoneRow(key) ?? Milestones().FirstOrDefault(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The milestone with this ID; null for anything that isn't a milestone's ID, without a query.</summary>
    private Milestone? MilestoneRow(string id) =>
        IsMilestoneId(id) ? Milestones().FirstOrDefault(m => m.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase)) : null;

    private Milestone RequireMilestone(string idOrName) => Milestone(idOrName) ?? throw new KeyNotFoundException($"No milestone {idOrName}.");

    private Milestone RequireOpenMilestone(string idOrName)
    {
        var m = RequireMilestone(idOrName);
        return m.IsOpen ? m : throw new InvalidOperationException($"{m.Id} is {m.State}; it no longer changes.");
    }

    /// <summary>A milestone's history, oldest first.</summary>
    public IReadOnlyList<MilestoneEvent> MilestoneHistory(string idOrName) =>
        [.. Rows("SELECT milestone_id, at, field, old, new FROM milestone_events WHERE milestone_id = $id COLLATE NOCASE ORDER BY seq",
            r => new MilestoneEvent(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)),
            ("$id", RequireMilestone(idOrName).Id))];

    private void MilestoneEvent(string id, string field, string? old, string? value) =>
        Exec($"INSERT INTO milestone_events (milestone_id, at, field, old, new) VALUES ($id, {SqlNow}, $f, $o, $n)",
            null, ("$id", id), ("$f", field), ("$o", old), ("$n", value));

    // ── Creating and editing ────────────────────────────────────────────────

    /// <summary>
    /// Adds a planned milestone with the next <c>MS-n</c> ID (never reused). It spans at least one repo, each one the ticket
    /// database knows; <paramref name="order"/> defaults to after every milestone there is.
    /// </summary>
    public Milestone AddMilestone(string name, string? goal, IEnumerable<string> repos, long? order = null)
    {
        var title = MilestoneName(name);
        var id = "";
        InWrite(() =>
        {
            var spans = MilestoneRepos(repos);
            if (spans.Count == 0) throw new ArgumentException("A milestone spans at least one repo.", nameof(repos));
            RefuseTakenName(title, null);
            var next = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(number), 0) + 1 FROM milestones"), CultureInfo.InvariantCulture);
            var ordinal = order ?? Convert.ToInt64(Scalar("SELECT COALESCE(MAX(ordinal), 0) + 1 FROM milestones"), CultureInfo.InvariantCulture);
            id = "MS-" + next.ToString(CultureInfo.InvariantCulture);
            Exec($"INSERT INTO milestones (id, number, name, goal, ordinal, state, created, updated) VALUES ($id, $n, $name, $goal, $o, 'planned', {SqlNow}, {SqlNow})", null,
                ("$id", id), ("$n", next), ("$name", title), ("$goal", (goal ?? "").Trim()), ("$o", ordinal));
            foreach (var repo in spans)
                Exec("INSERT INTO milestone_repos (milestone_id, repo) VALUES ($id, $r)", null, ("$id", id), ("$r", repo));
            MilestoneEvent(id, "created", null, MilestoneStates.Planned);
        });
        return RequireMilestone(id);
    }

    /// <summary>
    /// Changes a milestone's name, goal, order or repos (each left as it is when null), every change recorded in its history.
    /// A reached or dropped milestone no longer changes.
    /// </summary>
    public Milestone EditMilestone(string idOrName, string? name = null, string? goal = null, long? order = null, IEnumerable<string>? repos = null)
    {
        var id = "";
        InWrite(() =>
        {
            var m = RequireOpenMilestone(idOrName);
            id = m.Id;
            if (name is not null && MilestoneName(name) is var title && title != m.Name)
            {
                RefuseTakenName(title, m.Id);
                Exec("UPDATE milestones SET name = $v WHERE id = $id", null, ("$v", title), ("$id", m.Id));
                MilestoneEvent(m.Id, "name", m.Name, title);
            }
            if (goal is not null && goal.Trim() is var text && text != m.Goal)
            {
                Exec("UPDATE milestones SET goal = $v WHERE id = $id", null, ("$v", text), ("$id", m.Id));
                MilestoneEvent(m.Id, "goal", m.Goal, text);
            }
            if (order is { } o && o != m.Order)
            {
                Exec("UPDATE milestones SET ordinal = $v WHERE id = $id", null, ("$v", o), ("$id", m.Id));
                MilestoneEvent(m.Id, "order", m.Order.ToString(CultureInfo.InvariantCulture), o.ToString(CultureInfo.InvariantCulture));
            }
            if (repos is not null)
            {
                var spans = MilestoneRepos(repos);
                if (spans.Count == 0) throw new ArgumentException("A milestone spans at least one repo.", nameof(repos));
                foreach (var gone in m.Repos.Except(spans, StringComparer.OrdinalIgnoreCase))
                {
                    Exec("DELETE FROM milestone_repos WHERE milestone_id = $id AND repo = $r", null, ("$id", m.Id), ("$r", gone));
                    MilestoneEvent(m.Id, "repo", gone, null);
                }
                foreach (var added in spans.Except(m.Repos, StringComparer.OrdinalIgnoreCase))
                {
                    Exec("INSERT INTO milestone_repos (milestone_id, repo) VALUES ($id, $r)", null, ("$id", m.Id), ("$r", added));
                    MilestoneEvent(m.Id, "repo", null, added);
                }
            }
            Exec($"UPDATE milestones SET updated = {SqlNow} WHERE id = $id", null, ("$id", m.Id));
        });
        return RequireMilestone(id);
    }

    private static string MilestoneName(string? name)
    {
        var title = OneLine(name ?? "");
        if (title.Length == 0) throw new ArgumentException("A milestone needs a name.", nameof(name));
        if (IsMilestoneId(title)) throw new ArgumentException($"'{title}' reads as a milestone's ID; name it something else.", nameof(name));
        return title;
    }

    private void RefuseTakenName(string name, string? but)
    {
        if (Milestones().FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Id != but) is { } taken)
            throw new InvalidOperationException($"{taken.Id} is already named '{taken.Name}'.");
    }

    /// <summary>Each repo by the name the database keys it by now (a former name resolves), once each.</summary>
    private List<string> MilestoneRepos(IEnumerable<string> repos) =>
        [.. repos.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => RepoRow(r.Trim()).Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    // ── State ───────────────────────────────────────────────────────────────

    /// <summary>Moves a planned milestone to active (or sends one in acceptance back to active). <see cref="Milestone.Activated"/>
    /// keeps the first time it became active, which is what its growth since activation is measured from.</summary>
    public Milestone ActivateMilestone(string idOrName)
    {
        var id = "";
        InWrite(() =>
        {
            var m = RequireMilestone(idOrName);
            id = m.Id;
            if (m.State is not (MilestoneStates.Planned or MilestoneStates.Accepting))
                throw new InvalidOperationException($"{m.Id} is {m.State}; only a planned milestone (or one in acceptance) can be made active.");
            Exec($"UPDATE milestones SET state = 'active', activated = COALESCE(activated, {SqlNow}), updated = {SqlNow} WHERE id = $id", null, ("$id", m.Id));
            MilestoneEvent(m.Id, "state", m.State, MilestoneStates.Active);
        });
        return RequireMilestone(id);
    }

    /// <summary>
    /// Moves an active milestone into acceptance, or with <paramref name="reached"/> accepts an active or accepting one as
    /// reached, recording when and <paramref name="by"/> whom. Reaching is refused while any acceptance check is fail or unknown,
    /// unless <paramref name="force"/> (the checks passed over are then named in its history).
    /// </summary>
    public Milestone AcceptMilestone(string idOrName, bool reached = false, string? by = null, bool force = false)
    {
        var id = "";
        InWrite(() =>
        {
            var m = RequireMilestone(idOrName);
            id = m.Id;
            if (!reached)
            {
                if (m.State != MilestoneStates.Active)
                    throw new InvalidOperationException($"{m.Id} is {m.State}; only an active milestone moves into acceptance.");
                Exec($"UPDATE milestones SET state = 'accepting', updated = {SqlNow} WHERE id = $id", null, ("$id", m.Id));
                MilestoneEvent(m.Id, "state", m.State, MilestoneStates.Accepting);
                return;
            }
            if (m.State is not (MilestoneStates.Active or MilestoneStates.Accepting))
                throw new InvalidOperationException($"{m.Id} is {m.State}; only an active or accepting milestone can be reached.");
            if (string.IsNullOrWhiteSpace(by)) throw new ArgumentException("Say who accepts the milestone as reached.", nameof(by));
            var unmet = MilestoneChecks(m.Id).Where(c => !c.Passed).ToList();
            if (unmet.Count > 0 && !force)
                throw new InvalidOperationException($"{m.Id} isn't reached: {unmet.Count} acceptance check(s) haven't passed (" +
                    string.Join("; ", unmet.Select(c => $"#{c.Number} {c.Result}: {c.Description}")) + "). Record their results, or force it.");
            Exec($"UPDATE milestones SET state = 'reached', reached = {SqlNow}, reached_by = $by, updated = {SqlNow} WHERE id = $id", null, ("$by", by.Trim()), ("$id", m.Id));
            MilestoneEvent(m.Id, "state", m.State, MilestoneStates.Reached);
            MilestoneEvent(m.Id, "accepted", null, unmet.Count == 0 ? by.Trim()
                : $"{by.Trim()} (forced past {string.Join(", ", unmet.Select(c => $"#{c.Number} {c.Result}"))})");
        });
        return RequireMilestone(id);
    }

    /// <summary>Drops a milestone that isn't reached: it stays on record, with <paramref name="reason"/> in its history.</summary>
    public Milestone DropMilestone(string idOrName, string? reason = null)
    {
        var id = "";
        InWrite(() =>
        {
            var m = RequireMilestone(idOrName);
            id = m.Id;
            if (!m.IsOpen) throw new InvalidOperationException($"{m.Id} is already {m.State}.");
            Exec($"UPDATE milestones SET state = 'dropped', updated = {SqlNow} WHERE id = $id", null, ("$id", m.Id));
            MilestoneEvent(m.Id, "state", m.State, MilestoneStates.Dropped);
            if (!string.IsNullOrWhiteSpace(reason)) MilestoneEvent(m.Id, "dropped", null, OneLine(reason));
        });
        return RequireMilestone(id);
    }

    // ── Members ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts tickets or epics on a milestone: each gets a <c>part-of</c> link to it (one already there is passed over), recorded
    /// in the ticket's history and the milestone's. All or nothing. Returns its members now.
    /// </summary>
    public IReadOnlyList<MilestoneMember> AddToMilestone(string idOrName, IEnumerable<string> items)
    {
        var id = "";
        InWrite(() =>
        {
            id = RequireOpenMilestone(idOrName).Id;
            foreach (var item in items) AddLink(StoredId(item), id, PartOf);
        });
        return MilestoneMembers(id);
    }

    /// <summary>Takes tickets or epics off a milestone (ones not linked to it itself are passed over). Returns its members now.</summary>
    public IReadOnlyList<MilestoneMember> RemoveFromMilestone(string idOrName, IEnumerable<string> items)
    {
        var id = "";
        InWrite(() =>
        {
            id = RequireMilestone(idOrName).Id;
            foreach (var item in items) RemoveLink(StoredId(item), id, PartOf);
        });
        return MilestoneMembers(id);
    }

    /// <summary>An item's ID as the database spells it (a ticket's, an epic's), so its link reads the same however it was typed.</summary>
    private string StoredId(string id)
    {
        id = id.Trim();
        return Ticket(id)?.Ticket.Id ?? Epics().FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Id ?? id;
    }

    /// <summary>
    /// What's on a milestone: the items linked to it and everything part of those at any depth (an epic's tickets), the directly
    /// linked first. Pass <paramref name="graph"/> to read several milestones from one snapshot.
    /// </summary>
    public IReadOnlyList<MilestoneMember> MilestoneMembers(string idOrName, TicketGraph? graph = null)
    {
        var m = RequireMilestone(idOrName);
        graph ??= Graph();
        var direct = graph.In(m.Id, PartOf).Select(e => e.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. graph.Reach(m.Id, PartOf, down: true).Select(x => new MilestoneMember(graph.Node(x), direct.Contains(x)))
            .OrderBy(x => x.Direct ? 0 : 1).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>How far along <paramref name="members"/> are: tickets closed, of the tickets that aren't wont-do.</summary>
    public static (int Done, int Total) MilestoneProgress(IEnumerable<MilestoneMember> members)
    {
        var counted = members.Where(x => x.Counts).ToList();
        return (counted.Count(x => x.IsDone), counted.Count);
    }

    /// <summary>The milestones a ticket (or epic) is on, in milestone order: linked to it itself (<c>Direct</c>) or through what
    /// it's part of.</summary>
    public IReadOnlyList<(Milestone Milestone, bool Direct)> MilestonesOf(string id, TicketGraph? graph = null)
    {
        var all = Milestones();
        if (all.Count == 0) return [];
        graph ??= Graph();
        var self = StoredId(CurrentId(id.Trim()));
        var direct = graph.Out(self, PartOf).Select(e => e.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var above = graph.Reach(self, PartOf, down: false).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. all.Where(m => above.Contains(m.Id)).Select(m => (m, direct.Contains(m.Id)))];
    }

    /// <summary>Whether a ticket is on a milestone that's active now, itself or through what it's part of.</summary>
    public bool IsOnActiveMilestone(string id) => MilestonesOf(id).Any(x => x.Milestone.IsActive);

    /// <summary>Every ticket on an active milestone → the active milestones it's on (in milestone order), by ticket ID (any
    /// case): what a scheduler reads once to put milestone work first.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ActiveMilestoneTickets()
    {
        var on = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var active = Milestones(MilestoneStates.Active);
        if (active.Count == 0) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var graph = Graph();
        foreach (var m in active)
            foreach (var member in MilestoneMembers(m.Id, graph).Where(x => x.IsTicket))
            {
                if (!on.TryGetValue(member.Id, out var list)) on[member.Id] = list = [];
                list.Add(m.Id);
            }
        return on.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ── Acceptance checks ───────────────────────────────────────────────────

    /// <summary>A milestone's acceptance checks, by number.</summary>
    public IReadOnlyList<MilestoneCheck> MilestoneChecks(string idOrName)
    {
        var id = RequireMilestone(idOrName).Id;
        var stored = Rows("SELECT number, kind, description, repos, target, result, checked, evidence FROM milestone_checks WHERE milestone_id = $id COLLATE NOCASE ORDER BY number",
            r => (Number: Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Kind: r.GetString(1), Description: r.GetString(2),
                Repos: r.IsDBNull(3) ? "" : r.GetString(3), Target: r.IsDBNull(4) ? null : r.GetString(4), Result: r.GetString(5),
                Checked: r.IsDBNull(6) ? null : r.GetString(6), Evidence: r.IsDBNull(7) ? null : r.GetString(7)), ("$id", id));
        // Its repos by the names they go by now: one renamed since the check was written still reads right.
        return [.. stored.Select(c => new MilestoneCheck(id, c.Number, c.Kind, c.Description,
            [.. c.Repos.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => FindRepoRow(r)?.Name ?? r)], c.Target, c.Result, c.Checked, c.Evidence))];
    }

    /// <summary>
    /// Adds an acceptance check to a milestone, its result unknown: a <c>build</c> names the repos that must build on main, an
    /// <c>exists</c> the file or package (<paramref name="target"/>, in <paramref name="repos"/> when it's a path there), a
    /// <c>harness</c> the state its run must reach (<paramref name="target"/>), a <c>signoff</c> nothing more.
    /// </summary>
    public MilestoneCheck AddMilestoneCheck(string idOrName, string kind, string description, IEnumerable<string>? repos = null, string? target = null)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (!MilestoneCheckKinds.All.Contains(kind))
            throw new ArgumentException($"Unknown check kind '{kind}' (expected {string.Join("/", MilestoneCheckKinds.All)}).", nameof(kind));
        var text = OneLine(description ?? "");
        if (text.Length == 0) throw new ArgumentException("A check needs a description.", nameof(description));
        var what = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        var id = "";
        long number = 0;
        InWrite(() =>
        {
            id = RequireOpenMilestone(idOrName).Id;
            var where = MilestoneRepos(repos ?? []);
            if (kind == MilestoneCheckKinds.Build && where.Count == 0) throw new ArgumentException("A build check names the repos that must build.", nameof(repos));
            if (kind is MilestoneCheckKinds.Exists or MilestoneCheckKinds.Harness && what is null)
                throw new ArgumentException(kind == MilestoneCheckKinds.Exists ? "An exists check names the file or package (its target)." : "A harness check names the state its run must reach (its target).", nameof(target));
            number = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(number), 0) + 1 FROM milestone_checks WHERE milestone_id = $id COLLATE NOCASE", null, ("$id", id)), CultureInfo.InvariantCulture);
            Exec("INSERT INTO milestone_checks (milestone_id, number, kind, description, repos, target, result) VALUES ($id, $n, $k, $d, $r, $t, 'unknown')", null,
                ("$id", id), ("$n", number), ("$k", kind), ("$d", text), ("$r", where.Count == 0 ? null : string.Join(",", where)), ("$t", what));
            MilestoneEvent(id, "check", null, $"#{number} {kind}: {text}");
        });
        return MilestoneChecks(id).Single(c => c.Number == number);
    }

    /// <summary>Records what check <paramref name="number"/> came to (pass, fail or unknown), now, with a short
    /// <paramref name="evidence"/> note (a commit, a build log line, who signed off).</summary>
    public MilestoneCheck SetMilestoneCheckResult(string idOrName, long number, string result, string? evidence = null)
    {
        result = result.Trim().ToLowerInvariant();
        if (!MilestoneCheckResults.All.Contains(result))
            throw new ArgumentException($"Unknown result '{result}' (expected {string.Join("/", MilestoneCheckResults.All)}).", nameof(result));
        var note = string.IsNullOrWhiteSpace(evidence) ? null : OneLine(evidence);
        var id = "";
        InWrite(() =>
        {
            id = RequireOpenMilestone(idOrName).Id;
            var check = MilestoneChecks(id).FirstOrDefault(c => c.Number == number) ?? throw new KeyNotFoundException($"{id} has no check #{number}.");
            Exec($"UPDATE milestone_checks SET result = $r, checked = {SqlNow}, evidence = $e WHERE milestone_id = $id AND number = $n", null,
                ("$r", result), ("$e", note), ("$id", id), ("$n", number));
            MilestoneEvent(id, "check-result", $"#{number} {check.Result}", $"#{number} {result}" + (note is null ? "" : $" ({note})"));
        });
        return MilestoneChecks(id).Single(c => c.Number == number);
    }

    /// <summary>Takes a check off a milestone (one written by mistake); false when it has none by that number.</summary>
    public bool RemoveMilestoneCheck(string idOrName, long number)
    {
        var removed = false;
        InWrite(() =>
        {
            var id = RequireOpenMilestone(idOrName).Id;
            if (MilestoneChecks(id).FirstOrDefault(c => c.Number == number) is not { } check) return;
            Exec("DELETE FROM milestone_checks WHERE milestone_id = $id AND number = $n", null, ("$id", id), ("$n", number));
            MilestoneEvent(id, "check", $"#{number} {check.Kind}: {check.Description}", null);
            removed = true;
        });
        return removed;
    }
}
