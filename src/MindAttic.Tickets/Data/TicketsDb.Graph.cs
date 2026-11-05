namespace MindAttic.Tickets.Data;

/// <summary>
/// The ticket node graph (M.TIX-T314): link kinds with their derived inverses and aliases, the whole graph as one
/// <see cref="TicketGraph"/>, and the checks <c>ticket verify</c> runs over it.
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>The prerequisite kind: From waits for To (To is required-by From).</summary>
    public const string RequiresKind = "requires";

    /// <summary>The child → parent kind: From is part of To (To is parent-of From). An epic is just a ticket with children.</summary>
    public const string PartOf = "part-of";

    /// <summary>Each stored kind and the name it reads by from its other end.</summary>
    public static readonly IReadOnlyDictionary<string, string> LinkInverses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["requires"] = "required-by",
        ["part-of"] = "parent-of",
        ["related"] = "related",
        ["follow-up-of"] = "followed-up-by",
        ["supersedes"] = "superseded-by",
        ["duplicates"] = "duplicated-by",
        ["implements"] = "implemented-by",
    };

    /// <summary>Words <c>ticket link</c> also takes, each the stored kind it means and whether the two ends swap.</summary>
    private static readonly IReadOnlyDictionary<string, (string Kind, bool Swap)> LinkAliases = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase)
    {
        ["child-of"] = ("part-of", false),
        ["contains"] = ("part-of", true),
    };

    /// <summary>The name <paramref name="kind"/> reads by from its other end ("requires" → "required-by"); itself when unknown.</summary>
    public static string InverseKind(string kind) => LinkInverses.TryGetValue(kind, out var inverse) ? inverse : kind;

    /// <summary>Every word <c>ticket link</c> takes: the stored kinds, their inverses and the aliases.</summary>
    public static IReadOnlyList<string> LinkKindWords =>
        [.. LinkKinds.Concat(LinkInverses.Values).Concat(LinkAliases.Keys).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The stored kind <paramref name="kind"/> means, and whether FROM and TO swap: an inverse ("B required-by A" is stored as
    /// "A requires B") or an alias ("A contains B" is "B part-of A"). Null when it's no kind at all.
    /// </summary>
    public static (string Kind, bool Swap)? ResolveLinkKind(string kind)
    {
        kind = kind.Trim();
        if (LinkKinds.Contains(kind)) return (kind, false);
        if (LinkAliases.TryGetValue(kind, out var alias)) return alias;
        foreach (var (stored, inverse) in LinkInverses)
            if (inverse.Equals(kind, StringComparison.OrdinalIgnoreCase) && !inverse.Equals(stored, StringComparison.OrdinalIgnoreCase)) return (stored, true);
        return null;
    }

    /// <summary>The whole node graph: every ticket (with its friendly name) and every link, as they are now.</summary>
    public TicketGraph Graph() => TicketGraph.Build(Tickets(), AllLinks(), Names());

    /// <summary>Whether <paramref name="id"/> names an item a link may end at: a ticket, a story, or an epic by its CODE-EPIC-KEY id.</summary>
    public bool ItemExists(string id) =>
        Ticket(id) is not null || Story(id).Count > 0 || Epics().Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What <c>ticket verify</c> warns of without failing (M.TIX-T314): a closed ticket that still has open children (closed
    /// before its parts were done), for <paramref name="repo"/>'s tickets (or every repo's).
    /// </summary>
    public IReadOnlyList<string> GraphWarnings(string? repo = null) =>
        [.. Graph().ClosedParentsWithOpenChildren()
            .Where(x => repo is null || string.Equals(x.Parent.Repo, repo, StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Parent.Id}: closed with open children ({string.Join(", ", x.OpenChildren.Select(c => c.Id))})")];
}
