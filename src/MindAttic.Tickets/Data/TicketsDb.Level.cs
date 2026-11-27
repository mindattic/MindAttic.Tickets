using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>
/// Ticket level (M.TIX-T362): where a ticket sits in the tree, kept as exactly one <c>level:&lt;x&gt;</c> label. An epic also
/// carries the plain <c>epic</c> label. Setting a level replaces any other.
/// </summary>
public sealed partial class TicketsDb
{
    public const string LevelEpic = "epic";
    public const string LevelFeature = "feature";
    public const string LevelStory = "story";
    public const string LevelTask = "task";

    /// <summary>The levels, top of the tree first.</summary>
    public static readonly IReadOnlyList<string> Levels = [LevelEpic, LevelFeature, LevelStory, LevelTask];

    private const string LevelPrefix = "level:";

    /// <summary><paramref name="level"/> as one of <see cref="Levels"/> (any case, with or without <c>level:</c>), or null.</summary>
    public static string? NormalizeLevel(string? level)
    {
        var l = level?.Trim().ToLowerInvariant();
        if (l is null) return null;
        if (l.StartsWith(LevelPrefix, StringComparison.Ordinal)) l = l[LevelPrefix.Length..];
        return Levels.Contains(l, StringComparer.Ordinal) ? l : null;
    }

    /// <summary>A ticket's level from its <c>level:</c> label, or null when it has none.</summary>
    public string? Level(string id) => LevelOf(Labels(id));

    /// <summary>The IDs of every open ticket that has at least one open part-of child, in one query (M.TIX-T365).</summary>
    public HashSet<string> OpenContainerIds() =>
        new(Rows("SELECT DISTINCT p.id FROM links l JOIN tickets c ON c.id = l.from_id COLLATE NOCASE " +
                 "JOIN tickets p ON p.id = l.to_id COLLATE NOCASE WHERE l.kind = 'part-of' AND c.status = $s AND p.status = $s",
                 r => r.GetString(0), ("$s", TicketRules.Open)), StringComparer.OrdinalIgnoreCase);

    /// <summary>The level among <paramref name="labels"/>, or null.</summary>
    public static string? LevelOf(IEnumerable<string>? labels) =>
        labels?.FirstOrDefault(l => l.StartsWith(LevelPrefix, StringComparison.Ordinal)) is { } x ? NormalizeLevel(x) : null;

    /// <summary>Whether <paramref name="labels"/> make a ticket an epic (<c>level:epic</c>, or the plain <c>epic</c> label).</summary>
    public static bool IsEpic(IEnumerable<string>? labels) =>
        labels is not null && (LevelOf(labels) == LevelEpic || labels.Contains(LevelEpic, StringComparer.Ordinal));

    /// <summary>
    /// The nearest epic <paramref name="id"/> is part-of at any depth (M.TIX-T389), nearest first along part-of links; with
    /// <paramref name="repo"/>, only an epic in that repo counts. Null when there is none.
    /// </summary>
    public static string? EpicAncestor(TicketGraph graph, IReadOnlyDictionary<string, IReadOnlyList<string>> labels, string id, string? repo = null) =>
        graph.Reach(id, PartOf, down: false)
            .FirstOrDefault(a => IsEpic(labels.GetValueOrDefault(a))
                && (repo is null || string.Equals(graph.Node(a).Repo, repo, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Open tickets (optionally one repo's) that are not epics and have no epic ancestor in their own repo (M.TIX-T389): work
    /// that no epic accounts for. In repo then ID order.
    /// </summary>
    public IReadOnlyList<(string Repo, Ticket Ticket)> Orphans(string? repo = null)
    {
        var graph = Graph();
        var labels = AllLabels();
        return [.. Tickets(repo)
            .Where(x => x.Ticket.Status == TicketRules.Open && !IsEpic(labels.GetValueOrDefault(x.Ticket.Id))
                && EpicAncestor(graph, labels, x.Ticket.Id, x.Repo) is null)];
    }

    /// <summary>
    /// Sets a ticket's level: drops every other <c>level:*</c> label and adds <c>level:&lt;x&gt;</c> (and <c>epic</c> for an
    /// epic; taken off when it stops being one), in one transaction, each change in the ticket's history. Returns the level.
    /// </summary>
    public string SetLevel(string id, string level)
    {
        var lv = NormalizeLevel(level) ?? throw new ArgumentException($"\"{level}\" isn't a level: {string.Join(", ", Levels)}.");
        var want = LevelPrefix + lv;
        InWrite(() =>
        {
            var t = RequireTicket(id);
            var have = Labels(t.Id);
            foreach (var l in have)
            {
                var drop = (l.StartsWith(LevelPrefix, StringComparison.Ordinal) && l != want) || (l == LevelEpic && lv != LevelEpic);
                if (!drop) continue;
                Exec("DELETE FROM ticket_labels WHERE ticket_id = $id COLLATE NOCASE AND label = $l", null, ("$id", t.Id), ("$l", l));
                Event(t.Id, "label", l, null);
            }
            var add = lv == LevelEpic ? new[] { want, LevelEpic } : [want];
            foreach (var l in add)
            {
                if (have.Contains(l, StringComparer.Ordinal)) continue;
                Exec("INSERT INTO ticket_labels (ticket_id, label, added) VALUES ($id, $l, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))", null, ("$id", t.Id), ("$l", l));
                Event(t.Id, "label", null, l);
            }
        });
        return lv;
    }
}
