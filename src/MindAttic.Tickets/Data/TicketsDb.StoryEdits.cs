using System.Globalization;
using MindAttic.Tickets.Stories;

namespace MindAttic.Tickets.Data;

/// <summary>An epic as stored: its repo, key and title, and its <c>CODE-EPIC-KEY</c> id (its repo's current code).</summary>
public sealed record StoredEpic(string Repo, string Key, string Title, string Id);

/// <summary>
/// Editing stories and epics as Tickets Desktop's Stories view does (M.TIX-T82): a story's statement, status, acceptance and
/// evidence in one save, and an epic's title. The repo's USER_STORIES.md block is rewritten to match, so the view follows.
/// </summary>
public sealed partial class TicketsDb
{
    /// <summary>Every epic (of <paramref name="repo"/>, or of every repo), in file order.</summary>
    public IReadOnlyList<StoredEpic> Epics(string? repo = null) =>
        [.. Rows("SELECT e.repo, e.key, e.title, r.code FROM epics e JOIN repos r ON r.name = e.repo " +
                 "WHERE $r IS NULL OR e.repo = $r COLLATE NOCASE ORDER BY e.repo COLLATE NOCASE, e.ordinal",
            r => new StoredEpic(r.GetString(0), r.GetString(1), r.GetString(2), $"{r.GetString(3)}-EPIC-{r.GetString(1)}"), ("$r", repo))];

    /// <summary>Renames epic <paramref name="key"/> of <paramref name="repo"/>: its row and its <c>## Epic KEY — title</c> heading (anchor kept).</summary>
    public void EditEpic(string repo, string key, string title)
    {
        title = OneLine(title);
        if (title.Length == 0) throw new ArgumentException("An epic needs a title.", nameof(title));
        var (name, _) = RepoRow(repo);
        var stored = Rows("SELECT key FROM epics WHERE repo = $r AND key = $k COLLATE NOCASE", r => r.GetString(0), ("$r", name), ("$k", key)).FirstOrDefault()
            ?? throw new KeyNotFoundException($"{name} has no epic {key}.");
        using var tx = Begin();
        Exec("UPDATE epics SET title = $t WHERE repo = $r AND key = $k", tx, ("$t", title), ("$r", name), ("$k", stored));
        var heading = Rows("SELECT text FROM blocks WHERE repo = $r AND kind = 'epic' AND ref = $k", tx, r => r.GetString(0), ("$r", name), ("$k", stored)).FirstOrDefault();
        if (heading is not null)
        {
            // Only the title's span changes: the separator the file used and any {#anchor} stay as they were.
            var m = StoryDocument.EpicHeading().Match(heading);
            var text = m.Success ? heading[..m.Groups[2].Index] + title + heading[(m.Groups[2].Index + m.Groups[2].Length)..] : $"## Epic {stored} — {title}";
            Exec("UPDATE blocks SET text = $x WHERE repo = $r AND kind = 'epic' AND ref = $k", tx, ("$x", text), ("$r", name), ("$k", stored));
        }
        tx.Commit();
    }

    /// <summary>
    /// Saves a story as the Stories view edits it: its <paramref name="statement"/>, <paramref name="acceptance"/> and
    /// <paramref name="evidence"/> (written back as <c>*Acceptance: …*</c> and <c>*(…)*</c> after the statement) and its
    /// <paramref name="status"/> (null leaves it). The markdown is rewritten only when the text really changed, so a status-only
    /// save keeps the bullet's wording and wrapping. A story table row can only change status. Returns whether anything changed.
    /// </summary>
    public bool UpdateStory(string id, string? repo, string statement, string? status, string? acceptance, string? evidence)
    {
        var s = OneStory(id, repo);
        if (status is not null) StoryDocument.GlyphOf(status); // refuse an unknown status before writing anything
        var (curStatement, curAcceptance, curEvidence) = s.Source == "table" ? (s.Text, s.Acceptance, s.Evidence) : StoryDocument.SplitStory(s.Text);
        var textChanged = Norm(statement) != Norm(curStatement) || Norm(acceptance) != Norm(curAcceptance) || Norm(evidence) != Norm(curEvidence);
        if (textChanged)
        {
            if (s.Source == "table") throw new InvalidOperationException($"{s.Id} is a table row; only its status can change here.");
            WriteStoryText(s, StoryDocument.ComposeStory(statement, acceptance, evidence));
        }
        var statusChanged = status is not null && status != s.Status;
        if (statusChanged) SetStoryStatus(s.Id, status!, s.Repo);
        return textChanged || statusChanged;
    }

    private static string? Norm(string? text) => string.IsNullOrWhiteSpace(text) ? null : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private StoredStory OneStory(string id, string? repo) =>
        Story(id).Where(x => repo is null || x.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList() switch
        {
            [var one] => one,
            [] => throw new KeyNotFoundException($"No story {id}{(repo is null ? "" : " in " + repo)}."),
            _ => throw new InvalidOperationException($"More than one story is {id}; pass --repo."),
        };

    /// <summary>Rewrites a bullet story's text as one line (ID, glyph and suffix kept), with its evidence and acceptance read from it.</summary>
    private void WriteStoryText(StoredStory s, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A story needs text.", nameof(text));
        if (s.Source == "table") throw new InvalidOperationException($"{s.Id} is a table row; edit table stories with a new row instead.");
        var line = OneLine(text);
        var (_, acceptance, evidence) = StoryDocument.SplitStory(line);
        var indent = s.Source == "sub-bullet" ? "  " : "";
        var suffix = s.Suffix.Length > 0 ? " " + s.Suffix : "";
        using var tx = Begin();
        Exec("UPDATE stories SET text = $t, evidence = $e, acceptance = $a WHERE uid = $u", tx,
            ("$t", line), ("$e", evidence), ("$a", acceptance), ("$u", s.Uid));
        Exec("UPDATE blocks SET text = $x WHERE repo = $r AND kind = 'story' AND ref = $u", tx,
            ("$x", $"{indent}- **{s.Id} {s.Glyph}{suffix}** {line}"), ("$r", s.Repo), ("$u", s.Uid.ToString(CultureInfo.InvariantCulture)));
        tx.Commit();
    }
}
