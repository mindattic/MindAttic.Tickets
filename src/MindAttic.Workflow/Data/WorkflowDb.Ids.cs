using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MindAttic.Workflow.Tickets;

namespace MindAttic.Workflow.Data;

/// <summary>
/// One recorded ID change, kept forever: a ticket, story or epic that moved from <see cref="OldId"/> to
/// <see cref="NewId"/>, or (<see cref="Kind"/> "code") a repo's project code. A former ID always resolves to the current one.
/// </summary>
public sealed record IdChange(string Kind, string OldId, string NewId, string At, string Reason);

/// <summary>What <see cref="WorkflowDb.RecodeRepo"/> did.</summary>
public sealed record RecodeResult(string Repo, string OldCode, string NewCode, int Tickets, int Stories, int Epics, string BackupPath);

public sealed partial class WorkflowDb
{
    // ── ID reassignment ─────────────────────────────────────────────────────

    /// <summary>
    /// Reassigns a ticket's ID, the third sanctioned exception to append-only (alongside <see cref="CorrectType"/> and
    /// <see cref="RenameRepo"/>): the ticket keeps its date, type, summary, history, links and queue state under the new
    /// ID, the old ID is kept forever in <see cref="IdHistory"/> (and as an 'id' entry in the ticket's own history), and
    /// every lookup by the old ID still finds it. The new ID must use the repo's code and must never have been used.
    /// </summary>
    public Ticket ReassignTicketId(string id, string newId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reassignment needs a reason.", nameof(reason));
        newId = newId.Trim();
        var (repo, t) = Ticket(id) ?? throw new KeyNotFoundException($"No ticket {id}.");
        var (_, code) = RepoRow(repo);
        if (TicketRules.CodeOf(newId) is not { } newCode || TicketRules.ParseNumber(newId) is null)
            throw new ArgumentException($"'{newId}' is not CODE-T<n>.", nameof(newId));
        if (newCode != code) throw new ArgumentException($"{newId} doesn't use {repo}'s code '{code}'; change the code with 'repos recode'.", nameof(newId));
        if (IdTaken(newId)) throw new InvalidOperationException($"{newId} is (or was) already an ID; IDs are never reused.");

        using (var tx = db.BeginTransaction())
        {
            Exec("PRAGMA defer_foreign_keys = ON", tx); // ticket_events follow the ticket within this transaction
            Exec("DROP TRIGGER IF EXISTS tickets_append_only", tx);
            ReassignTicketCore(t.Id, newId, reason.Trim(), tx);
            EnsureSchema(tx); // the trigger comes straight back, before commit, so no other connection ever sees it missing
            tx.Commit();
        }
        return t with { Id = newId };
    }

    /// <summary>
    /// Changes a repo's project code, reassigning every ticket (<c>OLD-T7</c> → <c>NEW-T7</c>, numbers kept), story
    /// (<c>OLD-US-A1</c> → <c>NEW-US-A1</c>) and epic ID that used it, after backing up the database file. Links, queue state,
    /// history and the repo's page follow; every old ID (and the old code) is kept in <see cref="IdHistory"/> and still
    /// resolves. Ticket summaries and refs are history and stay as written. Never touches anything on disk.
    /// </summary>
    public RecodeResult RecodeRepo(string repo, string newCode, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A recode needs a reason.", nameof(reason));
        newCode = newCode.Trim();
        if (!TicketRules.IsCode(newCode)) throw new ArgumentException($"'{newCode}' is not a project code (like M.LNCH or PRS).", nameof(newCode));
        var (name, oldCode) = RepoRow(repo);
        if (oldCode == newCode) throw new InvalidOperationException($"{name}'s code is already {newCode}.");
        if (Scalar("SELECT name FROM repos WHERE code = $c COLLATE NOCASE", null, ("$c", newCode)) is string other)
            throw new InvalidOperationException($"{other} already uses code {newCode}.");
        if (Scalar("SELECT 1 FROM id_history WHERE kind = 'code' AND old_id = $c COLLATE NOCASE", null, ("$c", newCode)) is not null)
            throw new InvalidOperationException($"{newCode} was a code before; codes are never reused.");

        var backup = Backup();
        int tickets = 0, stories = 0, epics = 0;
        string Swap(string id) => newCode + id[oldCode.Length..];
        // The page's front matter (in its header block as well as the repos row) names the code; then its story bullets,
        // backlog items and anchors show the IDs. A ticket's own row re-renders from its new ID.
        var codeLine = new Regex(@"(?m)^(code:[ \t]*)" + Regex.Escape(oldCode) + @"(?=[ \t]*\r?$)");
        var mention = new Regex(@"(?<![A-Za-z0-9.])" + Regex.Escape(oldCode) + @"(?=-(?:T\d|US-|EPIC-))");
        string Rewrite(string text) => mention.Replace(codeLine.Replace(text, "${1}" + newCode, 1), newCode);
        using (var tx = db.BeginTransaction())
        {
            Exec("PRAGMA defer_foreign_keys = ON", tx);
            Exec("DROP TRIGGER IF EXISTS tickets_append_only", tx);
            var fm = (string)Scalar("SELECT front_matter FROM repos WHERE name = $n", tx, ("$n", name))!;
            Exec("UPDATE repos SET code = $c, front_matter = $fm WHERE name = $n", tx,
                ("$c", newCode), ("$fm", codeLine.Replace(fm, "${1}" + newCode, 1)), ("$n", name));

            foreach (var id in Rows("SELECT id FROM tickets WHERE repo = $n ORDER BY ordinal", tx, r => r.GetString(0), ("$n", name)))
                if (TicketRules.CodeOf(id) == oldCode) { ReassignTicketCore(id, Swap(id), reason.Trim(), tx); tickets++; }

            foreach (var id in Rows("SELECT DISTINCT story_id FROM stories WHERE repo = $n", tx, r => r.GetString(0), ("$n", name)))
            {
                if (!id.StartsWith(oldCode + "-", StringComparison.Ordinal)) continue;
                var to = Swap(id);
                Exec("UPDATE stories SET story_id = $t WHERE repo = $n AND story_id = $o", tx, ("$t", to), ("$n", name), ("$o", id));
                Exec("UPDATE OR IGNORE backlog_refs SET story_id = $t WHERE story_id = $o AND item_uid IN (SELECT uid FROM backlog_items WHERE repo = $n)", tx,
                    ("$t", to), ("$o", id), ("$n", name));
                RetargetLinks(id, to, tx);
                RecordId("story", id, to, reason.Trim(), tx);
                stories++;
            }

            foreach (var key in Rows("SELECT key FROM epics WHERE repo = $n", tx, r => r.GetString(0), ("$n", name)))
            {
                var (from, to) = ($"{oldCode}-EPIC-{key}", $"{newCode}-EPIC-{key}");
                RetargetLinks(from, to, tx);
                RecordId("epic", from, to, reason.Trim(), tx);
                epics++;
            }

            foreach (var (ordinal, text) in Rows("SELECT ordinal, text FROM blocks WHERE repo = $n", tx, r => (r.GetInt64(0), r.GetString(1)), ("$n", name)))
                if (Rewrite(text) is var rewritten && rewritten != text)
                    Exec("UPDATE blocks SET text = $t WHERE repo = $n AND ordinal = $o", tx, ("$t", rewritten), ("$n", name), ("$o", ordinal));
            foreach (var (uid, text) in Rows("SELECT uid, text FROM backlog_items WHERE repo = $n", tx, r => (r.GetInt64(0), r.GetString(1)), ("$n", name)))
                if (mention.Replace(text, newCode) is var rewritten && rewritten != text)
                    Exec("UPDATE backlog_items SET text = $t WHERE uid = $u", tx, ("$t", rewritten), ("$u", uid));

            RecordId("code", oldCode, newCode, reason.Trim(), tx);
            EnsureSchema(tx);
            tx.Commit();
        }
        return new RecodeResult(name, oldCode, newCode, tickets, stories, epics, backup);
    }

    /// <summary>Every recorded ID change, oldest first; optionally only those in <paramref name="id"/>'s chain (as old or new).</summary>
    public IReadOnlyList<IdChange> IdHistory(string? id = null)
    {
        var all = Rows("SELECT kind, old_id, new_id, at, reason FROM id_history ORDER BY seq",
            r => new IdChange(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        if (id is null) return all;
        var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id, CurrentId(id) };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var c in all)
                if (chain.Contains(c.OldId) || chain.Contains(c.NewId)) grew |= chain.Add(c.OldId) | chain.Add(c.NewId);
        }
        return [.. all.Where(c => c.Kind != "code" && chain.Contains(c.OldId))];
    }

    /// <summary>The ID <paramref name="id"/> is known by now: itself, or where its recorded reassignments lead.</summary>
    public string CurrentId(string id)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        var cur = id;
        while (Rows("SELECT new_id FROM id_history WHERE kind <> 'code' AND old_id = $o COLLATE NOCASE ORDER BY seq DESC LIMIT 1",
                   r => r.GetString(0), ("$o", cur)).FirstOrDefault() is { } next && seen.Add(next))
            cur = next;
        return cur;
    }

    private bool IdTaken(string id) =>
        Scalar("SELECT 1 FROM tickets WHERE id = $i COLLATE NOCASE UNION ALL SELECT 1 FROM id_history WHERE old_id = $i COLLATE NOCASE " +
               "UNION ALL SELECT 1 FROM stories WHERE story_id = $i COLLATE NOCASE LIMIT 1", null, ("$i", id)) is not null;

    private void ReassignTicketCore(string oldId, string newId, string reason, SqliteTransaction tx)
    {
        Exec("UPDATE tickets SET id = $n, number = $num, row_md = NULL WHERE id = $o", tx, ("$n", newId), ("$num", TicketRules.ParseNumber(newId)), ("$o", oldId));
        Exec("UPDATE ticket_events SET ticket_id = $n WHERE ticket_id = $o", tx, ("$n", newId), ("$o", oldId));
        Exec("UPDATE ticket_queue SET ticket_id = $n WHERE ticket_id = $o COLLATE NOCASE", tx, ("$n", newId), ("$o", oldId));
        RetargetLinks(oldId, newId, tx);
        RecordId("ticket", oldId, newId, reason, tx);
        Exec("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'id', $o, $n)", tx,
            ("$id", newId), ("$o", oldId), ("$n", $"{newId} ({reason})"));
    }

    // A link that would duplicate one already pointing at the new ID is dropped rather than kept under the old ID.
    private void RetargetLinks(string oldId, string newId, SqliteTransaction tx)
    {
        foreach (var end in new[] { "from_id", "to_id" })
        {
            Exec($"UPDATE OR IGNORE links SET {end} = $n WHERE {end} = $o COLLATE NOCASE", tx, ("$n", newId), ("$o", oldId));
            Exec($"DELETE FROM links WHERE {end} = $o COLLATE NOCASE", tx, ("$o", oldId));
        }
    }

    private void RecordId(string kind, string oldId, string newId, string reason, SqliteTransaction tx) =>
        Exec("INSERT INTO id_history (kind, old_id, new_id, at, reason) VALUES ($k, $o, $n, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), $r)", tx,
            ("$k", kind), ("$o", oldId), ("$n", newId), ("$r", reason));}
