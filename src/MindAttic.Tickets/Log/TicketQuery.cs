using System.Text;
using System.Text.RegularExpressions;

namespace MindAttic.Tickets.Log;

/// <summary>What a <see cref="TicketQuery"/> is matched against: a ticket, its repo, its approval and its labels.</summary>
public sealed record TicketFacts(string Repo, Ticket Ticket, string Approval = "proposed", IReadOnlyCollection<string>? Labels = null);

/// <summary>
/// One part of a query: <c>field:value</c> (values split on commas, any of them matching), or free text when
/// <see cref="Field"/> is null. <see cref="Negated"/> (a leading <c>-</c>) keeps only tickets it doesn't match.
/// </summary>
public sealed record QueryTerm(string? Field, IReadOnlyList<string> Values, bool Negated);

/// <summary>
/// The ticket query language (M.TIX-T78): <c>repo:M.TIX type:fix status:open label:ui approval:approved 'some text'</c>.
/// Terms are separated by spaces and all must match. A field takes one value or several split by commas (any of them
/// matches: <c>status:open,wont-do</c>); a leading <c>-</c> negates a term (<c>-type:chore</c>); <c>*</c> in a repo or
/// label value is a wildcard (<c>repo:MindAttic.*</c>). A repo matches by its name or its ticket code. Anything that isn't
/// a field is free text, matched without case against the ID, summary, ref and repo; quote it ('two words' or "two words")
/// to keep its spaces or to search for text with a colon in it. Unknown fields and unknown type, status or approval values
/// are refused with a message saying what's allowed, so a typo never silently matches nothing.
/// </summary>
public sealed partial class TicketQuery
{
    public const string RepoField = "repo", TypeField = "type", StatusField = "status", LabelField = "label", ApprovalField = "approval";

    /// <summary>Every field the language knows.</summary>
    public static readonly IReadOnlyList<string> Fields = [RepoField, TypeField, StatusField, LabelField, ApprovalField];

    /// <summary>The approval states (as <c>TicketsDb.Approvals</c>).</summary>
    private static readonly IReadOnlyList<string> Approvals = ["proposed", "approved", "rejected"];

    private TicketQuery(string text, IReadOnlyList<QueryTerm> terms)
    {
        Text = text;
        Terms = terms;
    }

    /// <summary>The query as written (trimmed).</summary>
    public string Text { get; }

    public IReadOnlyList<QueryTerm> Terms { get; }

    /// <summary>No terms: matches every ticket.</summary>
    public bool IsEmpty => Terms.Count == 0;

    /// <summary>The query that matches everything.</summary>
    public static readonly TicketQuery All = new("", []);

    /// <summary>Parses <paramref name="text"/>; throws <see cref="FormatException"/> saying what's wrong with it.</summary>
    public static TicketQuery Parse(string? text)
    {
        text = text?.Trim() ?? "";
        var terms = new List<QueryTerm>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var negated = false;
            if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                negated = true;
                i++;
            }
            if (text[i] is '"' or '\'')
            {
                terms.Add(new QueryTerm(null, [ReadQuoted(text, ref i)], negated));
                continue;
            }
            if (FieldPrefix().Match(text, i) is { Success: true } m && m.Index == i)
            {
                var field = m.Groups[1].Value.ToLowerInvariant();
                if (!Fields.Contains(field))
                    throw new FormatException($"Unknown filter '{m.Groups[1].Value}:' (known: {string.Join(", ", Fields.Select(f => f + ":"))}); quote it to search for the text.");
                i += m.Length;
                var raw = i < text.Length && text[i] is '"' or '\'' ? ReadQuoted(text, ref i) : ReadWord(text, ref i);
                var values = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (values.Length == 0) throw new FormatException($"'{field}:' needs a value, e.g. {Example(field)}.");
                terms.Add(new QueryTerm(field, [.. values.Select(v => Checked(field, v))], negated));
                continue;
            }
            terms.Add(new QueryTerm(null, [ReadWord(text, ref i)], negated));
        }
        return new TicketQuery(text, terms);
    }

    /// <summary>Parses <paramref name="text"/>, or says why it can't (<paramref name="error"/>) and returns false.</summary>
    public static bool TryParse(string? text, out TicketQuery query, out string? error)
    {
        try
        {
            query = Parse(text);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            query = All;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Whether <paramref name="t"/> matches every term.</summary>
    public bool Matches(TicketFacts t) => Terms.All(term => Matches(term, t) != term.Negated);

    /// <summary>Whether the ticket matches every term (approval proposed and no labels unless given).</summary>
    public bool Matches(string repo, Ticket ticket, string approval = "proposed", IReadOnlyCollection<string>? labels = null) =>
        Matches(new TicketFacts(repo, ticket, approval, labels));

    public override string ToString() => Text;

    private static bool Matches(QueryTerm term, TicketFacts t) => term.Field switch
    {
        null => Contains(t.Ticket.Id, term.Values[0]) || Contains(t.Ticket.Summary, term.Values[0]) || Contains(t.Ticket.Ref, term.Values[0]) || Contains(t.Repo, term.Values[0]),
        RepoField => term.Values.Any(v => Like(t.Repo, v) || Like(CodeOf(t.Ticket.Id), v)),
        TypeField => term.Values.Any(v => v.Equals(t.Ticket.Type, StringComparison.OrdinalIgnoreCase)),
        StatusField => term.Values.Any(v => v.Equals(t.Ticket.Status, StringComparison.OrdinalIgnoreCase)),
        ApprovalField => term.Values.Any(v => v.Equals(t.Approval, StringComparison.OrdinalIgnoreCase)),
        LabelField => t.Labels is { Count: > 0 } labels && term.Values.Any(v => labels.Any(l => Like(l, v))),
        _ => false,
    };

    private static bool Contains(string? haystack, string needle) => haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The project code a ticket ID starts with (<c>M.TIX</c> of <c>M.TIX-T78</c>).</summary>
    private static string CodeOf(string id)
    {
        var at = id.LastIndexOf("-T", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? id[..at] : id;
    }

    /// <summary>Equal without case, with <c>*</c> standing for any run of characters.</summary>
    private static bool Like(string value, string pattern) =>
        pattern.Contains('*', StringComparison.Ordinal)
            ? Regex.IsMatch(value, "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : value.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>A field's value, checked against its vocabulary (type, status, approval) and lower-cased there.</summary>
    private static string Checked(string field, string value)
    {
        var allowed = field switch
        {
            TypeField => TicketRules.Types,
            StatusField => TicketRules.Statuses,
            ApprovalField => Approvals,
            _ => null,
        };
        if (allowed is null) return value;
        return allowed.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException($"Unknown {field} '{value}' (expected {string.Join(", ", allowed)}).");
    }

    private static string Example(string field) => field switch
    {
        RepoField => "repo:M.TIX",
        TypeField => "type:fix",
        StatusField => "status:open",
        LabelField => "label:ui",
        _ => "approval:approved",
    };

    private static string ReadWord(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        return text[start..i];
    }

    private static string ReadQuoted(string text, ref int i)
    {
        var quote = text[i];
        var sb = new StringBuilder();
        for (i++; i < text.Length; i++)
        {
            if (text[i] == quote)
            {
                i++;
                return sb.ToString();
            }
            sb.Append(text[i]);
        }
        throw new FormatException($"Unterminated {quote}…{quote} in the query.");
    }

    [GeneratedRegex(@"\G([A-Za-z]+):")]
    private static partial Regex FieldPrefix();
}
