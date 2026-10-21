using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MindAttic.Tickets.Stories;
using MindAttic.Tickets.Log;

namespace MindAttic.Tickets.Data;

/// <summary>A link between two items (tickets or stories, by ID).</summary>
public sealed record Link(string From, string To, string Kind, string? Note);

/// <summary>One change to a ticket: its creation, or a status or ref change.</summary>
public sealed record TicketEvent(string TicketId, string At, string Field, string? Old, string? New);

/// <summary>One repo rename, kept forever so a repo (and its tickets) can be traced back to its original name.</summary>
public sealed record RepoRename(string OldName, string NewName, string At, string Reason);

/// <summary>What <see cref="TicketsDb.RenameRepo"/> did.</summary>
public sealed record RenameResult(string NewName, int TicketsMoved, string BackupPath);

/// <summary>A story row as stored.</summary>
public sealed record StoredStory(long Uid, string Repo, string Id, string? Status, string? Glyph, string Suffix, string Text,
    string Source, long? ParentUid, string? Epic, string? Section, string? Evidence, string? Acceptance);

/// <summary>What <see cref="TicketsDb.Import"/> found in one repo.</summary>
public sealed record ImportReport(string Repo, string Code, int Epics, int Stories, int BacklogItems, int Tickets, int Links, bool RoundTrips);

/// <summary>
/// The one Ticket database: every repo's stories (front-matter, epics, stories, backlog) and tickets in a
/// structured schema, with links between any of them (prerequisites included) and a history of every ticket
/// change. Each repo's file is also kept as ordered blocks, so <see cref="Export"/> rebuilds its
/// USER_STORIES.md byte-for-byte. Tickets are append-only (enforced by triggers): only status and ref change.
/// </summary>
public sealed partial class TicketsDb : IDisposable
{
    public const int SchemaVersion = 4;
    public const string RelativePath = "data/Tickets.db";
    public const string DumpRelativePath = "data/Tickets.sql";
    public const string EnvVar = "MINDATTIC_TICKETS_DB";

    /// <summary>The variable's name before the rename to MindAttic.Tickets (M.TIX-T120); still honoured when the new one isn't set.</summary>
    public const string LegacyEnvVar = "MINDATTIC_WORKFLOW_DB";

    /// <summary>This repo's folder name under the workspace root.</summary>
    public const string RepoName = "MindAttic.Tickets";

    /// <summary>Its folder name before the rename (M.TIX-T120), used while that folder hasn't been renamed yet.</summary>
    public const string LegacyRepoName = "MindAttic.Workflow";

    /// <summary>Kinds a <see cref="Link"/> may have; <c>requires</c> is a prerequisite (From waits for To).</summary>
    public static readonly IReadOnlyList<string> LinkKinds = ["requires", "related", "follow-up-of", "supersedes", "duplicates", "implements", "part-of"];

    private readonly SqliteConnection db;

    public string Path { get; }

    private TicketsDb(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        // Pooling off: a pooled connection keeps the file open after Dispose, and git (or a delete) on Windows needs it closed.
        db = new SqliteConnection($"Data Source={Path};Pooling=False;Foreign Keys=True");
        try
        {
            db.Open();
            Exec("PRAGMA busy_timeout = 15000; PRAGMA journal_mode = DELETE;");
            EnsureSchema();
        }
        catch
        {
            // Schema setup (or even Open) can throw on a corrupt or locked file; with pooling off, an undisposed
            // connection holds the file handle until a finalizer eventually runs, so a caller that retries each
            // refresh leaks handles and git (or a restore) can't replace the file in the meantime.
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Where the database lives: <c>$MINDATTIC_TICKETS_DB</c> (or the legacy <c>$MINDATTIC_WORKFLOW_DB</c>) if set, else
    /// <c>&lt;root&gt;\MindAttic.Tickets\data\Tickets.db</c> (see <see cref="HomeFolder"/>). It never depends on the current
    /// folder, so agents in worktrees outside the workspace reach the same database.
    /// </summary>
    public static string DefaultPath(string? workspaceRoot = null) =>
        Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env ? env
            : Environment.GetEnvironmentVariable(LegacyEnvVar) is { Length: > 0 } legacy ? legacy
            : System.IO.Path.Combine(workspaceRoot ?? Workspace.DefaultRoot, HomeFolder(workspaceRoot), RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>This repo's folder: <see cref="RepoName"/>, or <see cref="LegacyRepoName"/> while only that one exists.</summary>
    public static string HomeFolder(string? workspaceRoot = null)
    {
        var root = workspaceRoot ?? Workspace.DefaultRoot;
        return !Directory.Exists(System.IO.Path.Combine(root, RepoName)) && Directory.Exists(System.IO.Path.Combine(root, LegacyRepoName)) ? LegacyRepoName : RepoName;
    }

    /// <summary>Opens (creating the schema if needed) the database at <paramref name="path"/>.</summary>
    public static TicketsDb Open(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new TicketsDb(path);
    }

    /// <summary>The workspace database if it has been migrated; null while tickets still live in the markdown.</summary>
    public static TicketsDb? OpenIfMigrated(string? workspaceRoot = null)
    {
        var path = DefaultPath(workspaceRoot);
        if (!File.Exists(path)) return null;
        var w = new TicketsDb(path);
        try
        {
            if (w.Scalar("SELECT COUNT(*) FROM repos") is long n && n > 0) return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
        w.Dispose();
        return null;
    }

    public void Dispose() => db.Dispose();

    /// <summary>
    /// Creates the schema (tables, indexes, triggers) if missing. Pass the active transaction when called from inside
    /// one of the sanctioned-exception methods (<see cref="Import"/>, <see cref="CorrectType"/>, <see cref="RenameRepo"/>,
    /// <see cref="ReassignTicketId"/>, <see cref="RecodeRepo"/>)
    /// that drop the tickets guard triggers and must put them back before committing — recreating them only after the
    /// commit would leave a window where another connection sees no append-only/no-delete guard on the tickets table.
    /// </summary>
    private void EnsureSchema(SqliteTransaction? tx = null)
    {
        Exec(
            """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS repos (
                name TEXT PRIMARY KEY,
                code TEXT NOT NULL UNIQUE,
                project TEXT, title TEXT, updated TEXT,
                front_matter TEXT NOT NULL DEFAULT '',
                bom INTEGER NOT NULL DEFAULT 0, crlf INTEGER NOT NULL DEFAULT 0, trailing_newline INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS blocks (
                repo TEXT NOT NULL REFERENCES repos(name), ordinal INTEGER NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('raw', 'epic', 'story', 'backlog', 'tickets')),
                ref TEXT, text TEXT NOT NULL,
                PRIMARY KEY (repo, ordinal));
            CREATE TABLE IF NOT EXISTS epics (
                repo TEXT NOT NULL REFERENCES repos(name), key TEXT NOT NULL, title TEXT NOT NULL, anchor TEXT, ordinal INTEGER NOT NULL,
                PRIMARY KEY (repo, key));
            CREATE TABLE IF NOT EXISTS stories (
                uid INTEGER PRIMARY KEY, repo TEXT NOT NULL REFERENCES repos(name), story_id TEXT NOT NULL,
                parent_uid INTEGER REFERENCES stories(uid), epic TEXT, section TEXT, section_ref TEXT,
                status TEXT CHECK (status IS NULL OR status IN ('done', 'partial', 'planned')), glyph TEXT, suffix TEXT NOT NULL DEFAULT '',
                text TEXT NOT NULL, evidence TEXT, acceptance TEXT,
                source TEXT NOT NULL CHECK (source IN ('bullet', 'sub-bullet', 'table', 'backlog')), ordinal INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS stories_by_id ON stories (story_id);
            CREATE TABLE IF NOT EXISTS backlog_items (
                uid INTEGER PRIMARY KEY, repo TEXT NOT NULL REFERENCES repos(name), ordinal INTEGER NOT NULL, text TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS backlog_refs (
                item_uid INTEGER NOT NULL REFERENCES backlog_items(uid), story_id TEXT NOT NULL, PRIMARY KEY (item_uid, story_id));
            CREATE TABLE IF NOT EXISTS tickets (
                id TEXT PRIMARY KEY, repo TEXT NOT NULL REFERENCES repos(name), number INTEGER,
                date TEXT NOT NULL, type TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('open', 'closed', 'wont-do')),
                summary TEXT NOT NULL, ref TEXT NOT NULL DEFAULT '—', ordinal INTEGER NOT NULL,
                row_md TEXT);
            CREATE INDEX IF NOT EXISTS tickets_by_repo ON tickets (repo, ordinal);
            CREATE TABLE IF NOT EXISTS ticket_events (
                seq INTEGER PRIMARY KEY, ticket_id TEXT NOT NULL REFERENCES tickets(id), at TEXT NOT NULL,
                field TEXT NOT NULL, old TEXT, new TEXT);
            CREATE TABLE IF NOT EXISTS links (
                from_id TEXT NOT NULL, to_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('requires', 'related', 'follow-up-of', 'supersedes', 'duplicates', 'implements', 'part-of')),
                note TEXT, PRIMARY KEY (from_id, to_id, kind), CHECK (from_id <> to_id));
            CREATE INDEX IF NOT EXISTS links_to ON links (to_id);
            -- Schema 2: approval and queue priority. No row means proposed at priority 0. No foreign key, so Import's
            -- per-repo ticket rebuild keeps a repo's queue state.
            CREATE TABLE IF NOT EXISTS ticket_queue (
                ticket_id TEXT PRIMARY KEY COLLATE NOCASE,
                approval TEXT NOT NULL DEFAULT 'proposed' CHECK (approval IN ('proposed', 'approved', 'rejected')),
                priority INTEGER NOT NULL DEFAULT 0,
                updated TEXT NOT NULL);
            -- Schema 3: a repo's folder-name key can be renamed (RenameRepo); every rename is kept forever here,
            -- so the repo (and each of its tickets, via a 'repo' ticket_events row) traces back to its original name.
            CREATE TABLE IF NOT EXISTS repo_renames (
                seq INTEGER PRIMARY KEY, old_name TEXT NOT NULL, new_name TEXT NOT NULL, at TEXT NOT NULL, reason TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS repo_renames_by_old ON repo_renames (old_name);
            CREATE INDEX IF NOT EXISTS repo_renames_by_new ON repo_renames (new_name);
            CREATE TRIGGER IF NOT EXISTS repo_renames_no_delete BEFORE DELETE ON repo_renames
            BEGIN SELECT RAISE(ABORT, 'repo renames are never deleted'); END;
            CREATE TRIGGER IF NOT EXISTS repo_renames_no_update BEFORE UPDATE ON repo_renames
            BEGIN SELECT RAISE(ABORT, 'repo renames are append-only'); END;
            -- Schema 4: a ticket, story or epic ID (or a repo's code) can be reassigned (ReassignTicketId, RecodeRepo); every
            -- old ID is kept forever here and still resolves to the current one, so nothing that cites it ever dangles.
            CREATE TABLE IF NOT EXISTS id_history (
                seq INTEGER PRIMARY KEY, kind TEXT NOT NULL CHECK (kind IN ('ticket', 'story', 'epic', 'code')),
                old_id TEXT NOT NULL, new_id TEXT NOT NULL, at TEXT NOT NULL, reason TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS id_history_by_old ON id_history (old_id COLLATE NOCASE);
            CREATE TRIGGER IF NOT EXISTS id_history_no_delete BEFORE DELETE ON id_history
            BEGIN SELECT RAISE(ABORT, 'ID history is never deleted'); END;
            CREATE TRIGGER IF NOT EXISTS id_history_no_update BEFORE UPDATE ON id_history
            BEGIN SELECT RAISE(ABORT, 'ID history is append-only'); END;

            -- Append-only: a ticket is never deleted, and only its status and ref ever change.
            CREATE TRIGGER IF NOT EXISTS tickets_no_delete BEFORE DELETE ON tickets
            BEGIN SELECT RAISE(ABORT, 'tickets are never deleted; close one as wont-do instead'); END;
            CREATE TRIGGER IF NOT EXISTS tickets_append_only BEFORE UPDATE OF id, repo, number, date, type, summary, ordinal ON tickets
            WHEN NEW.id IS NOT OLD.id OR NEW.repo IS NOT OLD.repo OR NEW.number IS NOT OLD.number OR NEW.date IS NOT OLD.date
              OR NEW.type IS NOT OLD.type OR NEW.summary IS NOT OLD.summary OR NEW.ordinal IS NOT OLD.ordinal
            BEGIN SELECT RAISE(ABORT, 'tickets are append-only: only status and ref may change'); END;
            -- Every status or ref change is history; the row's original markdown no longer describes it.
            CREATE TRIGGER IF NOT EXISTS tickets_history AFTER UPDATE OF status, ref ON tickets
            WHEN NEW.status IS NOT OLD.status OR NEW.ref IS NOT OLD.ref
            BEGIN
                INSERT INTO ticket_events (ticket_id, at, field, old, new)
                    SELECT NEW.id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'status', OLD.status, NEW.status WHERE NEW.status IS NOT OLD.status;
                INSERT INTO ticket_events (ticket_id, at, field, old, new)
                    SELECT NEW.id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'ref', OLD.ref, NEW.ref WHERE NEW.ref IS NOT OLD.ref;
                UPDATE tickets SET row_md = NULL WHERE id = NEW.id;
            END;
            """, tx);
        Exec($"INSERT INTO meta (key, value) VALUES ('schema_version', '{SchemaVersion}') " +
             $"ON CONFLICT (key) DO UPDATE SET value = excluded.value WHERE CAST(value AS INTEGER) < {SchemaVersion}", tx);
    }

    // ── Import ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Migrates every repo's <c>docs/USER_STORIES.md</c> into the database (replacing what's there for those repos),
    /// derives links (story to epic, "blocked by" phrases, ticket refs naming other tickets or stories, and
    /// <paramref name="extraRequires"/>), then checks each repo exports back to its file byte-for-byte.
    /// </summary>
    public IReadOnlyList<ImportReport> Import(IEnumerable<Repo> repos, IReadOnlyDictionary<string, IReadOnlyList<string>>? extraRequires = null)
    {
        var docs = repos.Where(r => File.Exists(r.StoriesPath))
            .Select(r => (Repo: r, Bytes: File.ReadAllBytes(r.StoriesPath)))
            .Where(x => !IsPointer(x.Bytes)) // already migrated: its content lives here, not in the file
            .Select(x => (x.Repo, x.Bytes, Doc: StoryDocument.FromBytes(x.Bytes)))
            .Where(x => x.Doc.Code is not null)
            .ToList();

        using (var tx = db.BeginTransaction())
        {
            Exec("DROP TRIGGER IF EXISTS tickets_no_delete; DROP TRIGGER IF EXISTS tickets_append_only; DROP TRIGGER IF EXISTS tickets_history;", tx);
            foreach (var (repo, _, _) in docs)
                foreach (var sql in new[]
                {
                    "DELETE FROM backlog_refs WHERE item_uid IN (SELECT uid FROM backlog_items WHERE repo = $r)",
                    "DELETE FROM backlog_items WHERE repo = $r", "DELETE FROM stories WHERE repo = $r", "DELETE FROM epics WHERE repo = $r",
                    "DELETE FROM blocks WHERE repo = $r",
                    "DELETE FROM ticket_events WHERE ticket_id IN (SELECT id FROM tickets WHERE repo = $r)",
                    "DELETE FROM tickets WHERE repo = $r", "DELETE FROM repos WHERE name = $r",
                })
                    Exec(sql, tx, ("$r", repo.Name));
            foreach (var (repo, _, doc) in docs) ImportOne(repo, doc, tx);
            EnsureSchema(tx); // put the triggers back before commit, so no other connection ever sees them missing
            tx.Commit();
        }

        var linkCounts = ImportLinks(docs.Select(d => (d.Repo, d.Doc)).ToList(), extraRequires);
        Exec("INSERT OR REPLACE INTO meta (key, value) VALUES ('migrated_at', $v)", null, ("$v", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

        return [.. docs.Select(d => new ImportReport(d.Repo.Name, d.Doc.Code!,
            d.Doc.Blocks.Count(b => b.Kind == BlockKind.Epic), d.Doc.Stories.Count,
            d.Doc.Blocks.Count(b => b.Kind == BlockKind.BacklogItem), d.Doc.Tickets.Count, linkCounts.GetValueOrDefault(d.Repo.Name),
            Export(d.Repo.Name) is { } bytes && bytes.SequenceEqual(d.Bytes)))];
    }

    private void ImportOne(Repo repo, StoryDocument doc, SqliteTransaction tx)
    {
        var fm = string.Join("\n", doc.FrontMatter.Select(kv => $"{kv.Key}: {kv.Value}"));
        Exec("INSERT INTO repos (name, code, project, title, updated, front_matter, bom, crlf, trailing_newline) VALUES ($n, $c, $p, $t, $u, $fm, $b, $cr, $tn)", tx,
            ("$n", repo.Name), ("$c", doc.Code), ("$p", doc.FrontMatter.GetValueOrDefault("project")), ("$t", doc.Title),
            ("$u", doc.FrontMatter.GetValueOrDefault("updated")), ("$fm", fm), ("$b", doc.Bom ? 1 : 0), ("$cr", doc.Crlf ? 1 : 0), ("$tn", doc.TrailingNewline ? 1 : 0));

        var storyUids = new List<long>();
        var ordinal = 0;
        var epicOrdinal = 0;
        foreach (var b in doc.Blocks)
        {
            string? reference = null;
            switch (b.Kind)
            {
                case BlockKind.Epic:
                    Exec("INSERT OR REPLACE INTO epics (repo, key, title, anchor, ordinal) VALUES ($r, $k, $t, $a, $o)", tx,
                        ("$r", repo.Name), ("$k", b.Epic!.Key), ("$t", b.Epic.Title), ("$a", b.Epic.Anchor), ("$o", epicOrdinal++));
                    reference = b.Epic.Key;
                    break;
                case BlockKind.Story:
                    var s = b.Story!;
                    var uid = (long)Scalar(
                        """
                        INSERT INTO stories (repo, story_id, parent_uid, epic, section, section_ref, status, glyph, suffix, text, evidence, acceptance, source, ordinal)
                        VALUES ($r, $id, $p, $e, $sec, $sr, $st, $g, $suf, $txt, $ev, $ac, $src, $o) RETURNING uid
                        """, tx,
                        ("$r", repo.Name), ("$id", s.Id), ("$p", s.Parent is { } pi ? storyUids[pi] : null), ("$e", s.Epic), ("$sec", s.Section),
                        ("$sr", s.SectionRef), ("$st", s.Status), ("$g", s.Glyph), ("$suf", s.Suffix), ("$txt", s.Text), ("$ev", s.Evidence),
                        ("$ac", s.Acceptance), ("$src", s.Source), ("$o", storyUids.Count))!;
                    storyUids.Add(uid);
                    reference = uid.ToString(CultureInfo.InvariantCulture);
                    break;
                case BlockKind.BacklogItem:
                    var item = (long)Scalar("INSERT INTO backlog_items (repo, ordinal, text) VALUES ($r, $o, $t) RETURNING uid", tx,
                        ("$r", repo.Name), ("$o", ordinal), ("$t", b.Backlog!.Text))!;
                    foreach (var id in b.Backlog.StoryIds)
                        Exec("INSERT OR IGNORE INTO backlog_refs (item_uid, story_id) VALUES ($i, $s)", tx, ("$i", item), ("$s", id));
                    reference = item.ToString(CultureInfo.InvariantCulture);
                    break;
            }
            Exec("INSERT INTO blocks (repo, ordinal, kind, ref, text) VALUES ($r, $o, $k, $ref, $t)", tx,
                ("$r", repo.Name), ("$o", ordinal++), ("$k", KindName(b.Kind)), ("$ref", reference), ("$t", b.Text));
        }

        var row = 0;
        foreach (var t in doc.Tickets)
            Exec("INSERT INTO tickets (id, repo, number, date, type, status, summary, ref, ordinal, row_md) VALUES ($id, $r, $n, $d, $ty, $st, $su, $ref, $o, $md)", tx,
                ("$id", t.Ticket.Id), ("$r", repo.Name), ("$n", t.Ticket.Number), ("$d", t.Ticket.Date), ("$ty", t.Ticket.Type),
                ("$st", t.Ticket.Status), ("$su", t.Ticket.Summary), ("$ref", t.Ticket.Ref), ("$o", row++), ("$md", t.Markdown));
    }

    private Dictionary<string, int> ImportLinks(List<(Repo Repo, StoryDocument Doc)> docs, IReadOnlyDictionary<string, IReadOnlyList<string>>? extraRequires)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ticketRepo = Rows("SELECT id, repo FROM tickets", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var storyIds = Rows("SELECT DISTINCT story_id FROM stories", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
        var repos = docs.Select(d => d.Repo.Name).ToHashSet(StringComparer.Ordinal);

        using var tx = db.BeginTransaction();
        // Links from the migrated repos are derived again; links added by hand to other items are kept. 'supersedes' is
        // the one kind whose derived direction is flipped from the ticket whose Ref was actually parsed (from = the
        // superseding ticket, which can be in a different repo; to = the reimported ticket), so it also needs the to_id
        // side checked, or a stale 'supersedes' link survives a reimport of just the superseded ticket's repo.
        Exec("DELETE FROM links WHERE note LIKE 'migrated:%' AND (from_id IN (SELECT id FROM tickets WHERE repo IN (SELECT value FROM json_each($r))) " +
             "OR from_id IN (SELECT story_id FROM stories WHERE repo IN (SELECT value FROM json_each($r))) " +
             "OR (kind = 'supersedes' AND to_id IN (SELECT id FROM tickets WHERE repo IN (SELECT value FROM json_each($r)))))", tx,
             ("$r", System.Text.Json.JsonSerializer.Serialize(repos)));
        void Link(string repo, string from, string to, string kind, string why)
        {
            if (from == to) return;
            if (Exec("INSERT OR IGNORE INTO links (from_id, to_id, kind, note) VALUES ($f, $t, $k, $n)", tx, ("$f", from), ("$t", to), ("$k", kind), ("$n", "migrated: " + why)) > 0)
                counts[repo] = counts.GetValueOrDefault(repo) + 1;
        }

        foreach (var (repo, doc) in docs)
        {
            foreach (var s in doc.Stories)
            {
                if (s.Epic is { } e) Link(repo.Name, s.Id, $"{doc.Code}-EPIC-{e}", "part-of", "story under its epic");
                if (s.Parent is { } p) Link(repo.Name, s.Id, doc.Stories[p].Id, "part-of", "sub-story");
            }
            foreach (var t in doc.Tickets.Select(x => x.Ticket))
            {
                foreach (Match m in BlockedBy().Matches(t.Summary))
                    if (ticketRepo.ContainsKey(m.Groups["id"].Value)) Link(repo.Name, t.Id, m.Groups["id"].Value, "requires", "blocked by in summary");
                var superseded = SupersededBy().Match(t.Ref);
                foreach (Match m in TicketId().Matches(t.Ref))
                {
                    var other = m.Value;
                    if (!ticketRepo.ContainsKey(other)) continue;
                    if (superseded.Success && superseded.Groups["id"].Value == other) Link(repo.Name, other, t.Id, "supersedes", "superseded by in ref");
                    else Link(repo.Name, t.Id, other, "related", "named in ref");
                }
                foreach (Match m in StoryDocument.StoryIdRef().Matches(t.Ref + " " + t.Summary))
                    if (storyIds.Contains(m.Value)) Link(repo.Name, t.Id, m.Value, "implements", "story named in ticket");
            }
        }
        if (extraRequires is not null)
            foreach (var (from, tos) in extraRequires)
                if (ticketRepo.TryGetValue(from, out var repo))
                    foreach (var to in tos.Where(ticketRepo.ContainsKey))
                        Link(repo, from, to, "requires", "orchestrator block setting");
        tx.Commit();
        return counts;
    }

    private static string KindName(BlockKind k) => k switch
    {
        BlockKind.Raw => "raw", BlockKind.Epic => "epic", BlockKind.Story => "story", BlockKind.BacklogItem => "backlog", _ => "tickets",
    };

    private static BlockKind KindOf(string k) => k switch
    {
        "raw" => BlockKind.Raw, "epic" => BlockKind.Epic, "story" => BlockKind.Story, "backlog" => BlockKind.BacklogItem, _ => BlockKind.Tickets,
    };

    /// <summary>The first line of the body of a migrated repo's USER_STORIES.md while it was only a pointer.</summary>
    public const string PointerMarker = "<!-- workflow-db: this repo's stories and tickets live in the MindAttic.Workflow database -->";

    /// <summary>The line after the front-matter of a repo's generated USER_STORIES.md view.</summary>
    public const string ViewMarker = "<!-- GENERATED from MindAttic.Tickets/data/Tickets.db by `tickets db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the tickets CLI. -->";

    /// <summary>The marker views carried before the rename to MindAttic.Tickets (M.TIX-T120); a view that still has it is ours too.</summary>
    public const string LegacyViewMarker = "<!-- GENERATED from MindAttic.Workflow/data/workflow.db by `workflow db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the workflow CLI. -->";

    /// <summary>A file the database wrote (a view, or an old pointer): never imported, since its content is already here.</summary>
    public static bool IsPointer(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes) is var s && (s.Contains(PointerMarker, StringComparison.Ordinal) || s.Contains(ViewMarker, StringComparison.Ordinal)
            || s.Contains(LegacyViewMarker, StringComparison.Ordinal));

    /// <summary>
    /// The repo's read-only USER_STORIES.md view: the file rebuilt from the database (<see cref="Export"/>) with
    /// <see cref="ViewMarker"/> after its front-matter, so links into it, the codex doctors and GitHub keep working.
    /// </summary>
    public byte[]? View(string repo)
    {
        var bytes = Export(repo);
        if (bytes is null) return null;
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var at = lines.Count > 0 && lines[0].Trim() == "---" ? lines.FindIndex(1, l => l.Trim() == "---") + 1 : 0;
        lines.Insert(Math.Max(at, 0), ViewMarker);
        return StoryDocument.Encode(string.Join("\n", lines), bom, crlf);
    }

    /// <summary>Writes the views that differ from what's on disk (all repos, or those named); returns the repos rewritten.</summary>
    public IReadOnlyList<string> Render(Workspace workspace, IEnumerable<string>? only = null)
    {
        var wanted = only?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        foreach (var (name, _) in Repos())
        {
            if (wanted is not null && !wanted.Contains(name)) continue;
            if (workspace.Find(name) is not { } repo || View(name) is not { } bytes) continue;
            if (File.Exists(repo.StoriesPath) && File.ReadAllBytes(repo.StoriesPath).SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(repo.StoriesPath)!);
            File.WriteAllBytes(repo.StoriesPath, bytes);
            written.Add(name);
        }
        return written;
    }

    /// <summary>Repos whose USER_STORIES.md on disk isn't their current view: hand-edited, or not rendered since a change.</summary>
    public IReadOnlyList<string> StaleViews(Workspace workspace) =>
        [.. Repos().Select(r => r.Name).Where(n => workspace.Find(n) is { } repo &&
            !(File.Exists(repo.StoriesPath) && View(n) is { } v && File.ReadAllBytes(repo.StoriesPath).SequenceEqual(v)))];

    /// <summary>Adds a repo that has never had a USER_STORIES.md: front-matter, an empty backlog and an empty ticket log.</summary>
    public void AddRepo(Repo repo, string code, string? project = null)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf-new-" + Guid.NewGuid().ToString("N"));
        var tmp = new Repo(repo.Name, dir);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tmp.StoriesPath)!);
        try
        {
            File.WriteAllText(tmp.StoriesPath, StoryFile.NewFileText(project ?? repo.Name, code, TicketRules.Today()));
            Import([tmp]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Export ──────────────────────────────────────────────────────────────

    /// <summary>The repo's USER_STORIES.md as bytes, rebuilt from the database (tickets as they are now); null for an unknown repo.</summary>
    public byte[]? Export(string repo)
    {
        var meta = Rows("SELECT bom, crlf, trailing_newline FROM repos WHERE name = $r", r => (r.GetInt64(0) == 1, r.GetInt64(1) == 1, r.GetInt64(2) == 1), ("$r", repo)).ToList();
        if (meta.Count == 0) return null;
        var (bom, crlf, trailing) = meta[0];
        var blocks = Rows("SELECT kind, text FROM blocks WHERE repo = $r ORDER BY ordinal", r => (KindOf(r.GetString(0)), r.GetString(1)), ("$r", repo)).ToList();
        var rows = Rows("SELECT id, date, type, status, summary, ref, row_md FROM tickets WHERE repo = $r ORDER BY ordinal",
            r => r.IsDBNull(6) ? StoryFile.FormatRow(new Ticket(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5))) : r.GetString(6),
            ("$r", repo)).ToList();
        var text = StoryDocument.Join(blocks.Select(b => b.Item2), rows, blocks.Select(b => b.Item1), trailing);
        return StoryDocument.Encode(text, bom, crlf);
    }

    // ── Tickets ─────────────────────────────────────────────────────────────

    /// <summary>Every repo in the database: name and code.</summary>
    public IReadOnlyList<(string Name, string Code)> Repos() =>
        [.. Rows("SELECT name, code FROM repos ORDER BY name COLLATE NOCASE", r => (r.GetString(0), r.GetString(1)))];

    /// <summary>Tickets (with their repo name), optionally for one repo, in repo then log order.</summary>
    public IReadOnlyList<(string Repo, Ticket Ticket)> Tickets(string? repo = null) =>
        [.. Rows("SELECT repo, id, date, type, status, summary, ref FROM tickets WHERE $r IS NULL OR repo = $r COLLATE NOCASE ORDER BY repo COLLATE NOCASE, ordinal",
            r => (r.GetString(0), ReadTicket(r, 1)), ("$r", repo))];

    /// <summary>The ticket with <paramref name="id"/>, or the one a former ID was reassigned to; null if neither.</summary>
    public (string Repo, Ticket Ticket)? Ticket(string id)
    {
        (string, Ticket)? Lookup(string key) =>
            Rows("SELECT repo, id, date, type, status, summary, ref FROM tickets WHERE id = $id COLLATE NOCASE",
                r => (r.GetString(0), ReadTicket(r, 1)), ("$id", key))
                .Cast<(string, Ticket)?>().FirstOrDefault();
        return Lookup(id) ?? (CurrentId(id) is var current && !current.Equals(id, StringComparison.OrdinalIgnoreCase) ? Lookup(current) : null);
    }

    /// <summary>The ticket in the six columns id, date, type, status, summary, ref starting at <paramref name="at"/>.</summary>
    private static Ticket ReadTicket(SqliteDataReader r, int at) =>
        new(r.GetString(at), r.GetString(at + 1), r.GetString(at + 2), r.GetString(at + 3), r.GetString(at + 4), r.GetString(at + 5));

    /// <summary>Adds a ticket to <paramref name="repo"/> with the next free number (never reusing a gap), atomically.</summary>
    public Ticket Add(string repo, string type, string status, string summary, string reference, string? date = null)
    {
        if (!TicketRules.Types.Contains(type)) throw new ArgumentException($"Unknown type '{type}' (expected {string.Join("/", TicketRules.Types)}).", nameof(type));
        if (!TicketRules.Statuses.Contains(status)) throw new ArgumentException($"Unknown status '{status}' (expected {string.Join("/", TicketRules.Statuses)}).", nameof(status));
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("A ticket needs a summary.", nameof(summary));

        Exec("BEGIN IMMEDIATE");
        try
        {
            var (name, code) = FindRepoRow(repo)
                ?? throw new InvalidOperationException($"{repo} isn't in the ticket database; run 'tickets db migrate' (or 'tickets repos rollout tickets --apply').");
            var next = (long)(Scalar("SELECT COALESCE(MAX(number), 0) + 1 FROM tickets WHERE repo = $r", null, ("$r", name)) ?? 1L);
            var ordinal = (long)(Scalar("SELECT COALESCE(MAX(ordinal), -1) + 1 FROM tickets WHERE repo = $r", null, ("$r", name)) ?? 0L);
            var t = new Ticket($"{code}-T{next}", date ?? TicketRules.Today(), type, status, summary.Trim(), string.IsNullOrWhiteSpace(reference) ? "—" : reference.Trim());
            Exec("INSERT INTO tickets (id, repo, number, date, type, status, summary, ref, ordinal) VALUES ($id, $r, $n, $d, $ty, $st, $su, $ref, $o)", null,
                ("$id", t.Id), ("$r", name), ("$n", next), ("$d", t.Date), ("$ty", t.Type), ("$st", t.Status), ("$su", t.Summary), ("$ref", t.Ref), ("$o", ordinal));
            Exec("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'created', NULL, $st)", null, ("$id", t.Id), ("$st", t.Status));
            foreach (Match m in BlockedBy().Matches(t.Summary))
                if (Ticket(m.Groups["id"].Value) is not null) AddLinkCore(t.Id, m.Groups["id"].Value, "requires", "blocked by in summary");
            Exec("COMMIT");
            return t;
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }
    }

    /// <summary>Moves a ticket to closed or wont-do, setting Ref when one is given.</summary>
    public Ticket Close(string id, string status = TicketRules.Closed, string? reference = null)
    {
        if (status is not (TicketRules.Closed or TicketRules.WontDo))
            throw new ArgumentException($"Close to '{TicketRules.Closed}' or '{TicketRules.WontDo}', not '{status}'.", nameof(status));
        var t = Ticket(id)?.Ticket ?? throw new KeyNotFoundException($"No ticket {id}.");
        var updated = t with { Status = status, Ref = string.IsNullOrWhiteSpace(reference) ? t.Ref : reference.Trim() };
        Exec("UPDATE tickets SET status = $s, ref = $ref WHERE id = $id", null, ("$s", updated.Status), ("$ref", updated.Ref), ("$id", t.Id));
        return updated;
    }

    /// <summary>
    /// Corrects a ticket's type to one of <see cref="TicketRules.Types"/>, the one sanctioned exception to append-only
    /// (for rows that predate the vocabulary, like 'bugfix'): the append-only trigger is lifted for this one update only,
    /// and the change is recorded in the ticket's history with <paramref name="reason"/>.
    /// </summary>
    public Ticket CorrectType(string id, string type, string reason)
    {
        if (!TicketRules.Types.Contains(type)) throw new ArgumentException($"Unknown type '{type}' (expected {string.Join("/", TicketRules.Types)}).", nameof(type));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A correction needs a reason.", nameof(reason));
        var t = Ticket(id)?.Ticket ?? throw new KeyNotFoundException($"No ticket {id}.");
        if (t.Type == type) return t;
        using (var tx = db.BeginTransaction())
        {
            Exec("DROP TRIGGER tickets_append_only", tx);
            Exec("UPDATE tickets SET type = $ty, row_md = NULL WHERE id = $id", tx, ("$ty", type), ("$id", t.Id));
            Exec("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'type', $o, $n)", tx,
                ("$id", t.Id), ("$o", t.Type), ("$n", $"{type} (corrected: {reason.Trim()})"));
            EnsureSchema(tx); // the trigger comes straight back, before commit, so no other connection ever sees it missing
            tx.Commit();
        }
        return t with { Type = type };
    }

    /// <summary>A ticket's history, oldest first.</summary>
    public IReadOnlyList<TicketEvent> History(string id) =>
        [.. Rows("SELECT ticket_id, at, field, old, new FROM ticket_events WHERE ticket_id = $id COLLATE NOCASE ORDER BY seq",
            r => new TicketEvent(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)), ("$id", id))];

    // ── Repo rename ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renames a repo's folder-name key everywhere the database keys by it (repos, blocks, epics, stories,
    /// backlog_items, tickets.repo), in one transaction, after backing up the database file. Ticket IDs, codes,
    /// dates, types and summaries never change — this is the other sanctioned exception to the tickets append-only
    /// guard (alongside <see cref="CorrectType"/>), lifted for this update only. The old name is kept forever in
    /// <see cref="RepoRenames"/>, and every one of the repo's tickets gets a 'repo' history entry, so the repo and
    /// each of its tickets can always be traced back to where they started. Never touches anything on disk.
    /// </summary>
    public RenameResult RenameRepo(string oldName, string newName, string reason)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("A rename needs a new name.", nameof(newName));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rename needs a reason.", nameof(reason));
        var (name, _) = RepoRow(oldName);
        newName = newName.Trim();
        if (name.Equals(newName, StringComparison.Ordinal)) throw new InvalidOperationException($"{name} is already named that.");
        if (Scalar("SELECT 1 FROM repos WHERE name = $n COLLATE NOCASE", null, ("$n", newName)) is not null)
            throw new InvalidOperationException($"A repo named '{newName}' already exists.");

        var backup = Backup();

        Exec("PRAGMA foreign_keys = OFF;");
        int ticketsMoved;
        try
        {
            using (var tx = db.BeginTransaction())
            {
                Exec("DROP TRIGGER IF EXISTS tickets_append_only", tx);
                Exec("UPDATE repos SET name = $n WHERE name = $o", tx, ("$n", newName), ("$o", name));
                foreach (var table in new[] { "blocks", "epics", "stories", "backlog_items", "tickets" })
                    Exec($"UPDATE {table} SET repo = $n WHERE repo = $o", tx, ("$n", newName), ("$o", name));
                RenameInPage(name, newName, tx);
                Exec("INSERT INTO repo_renames (old_name, new_name, at, reason) VALUES ($o, $n, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), $reason)", tx,
                    ("$o", name), ("$n", newName), ("$reason", reason.Trim()));
                // Read the moved tickets inside the same transaction, after the UPDATE above: reading them earlier
                // (before the backup and the transaction even opened) raced any concurrent `ticket new` against this
                // repo, which would get silently renamed with no 'repo' history row and an undercounted TicketsMoved.
                var ticketIds = Rows("SELECT id FROM tickets WHERE repo = $n", tx, r => r.GetString(0), ("$n", newName)).ToList();
                foreach (var id in ticketIds)
                    Exec("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'repo', $o, $n)", tx,
                        ("$id", id), ("$o", name), ("$n", newName));
                ticketsMoved = ticketIds.Count;
                EnsureSchema(tx); // the trigger comes straight back, before commit, so no other connection ever sees it missing
                tx.Commit();
            }
        }
        finally { Exec("PRAGMA foreign_keys = ON;"); }
        return new RenameResult(newName, ticketsMoved, backup);
    }

    /// <summary>Copies the database beside itself (<c>&lt;file&gt;.&lt;UTC stamp&gt;.bak</c>) before a bulk rewrite; returns the copy's path.</summary>
    private string Backup()
    {
        var backup = $"{Path}.{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.bak";
        using var dest = new SqliteConnection($"Data Source={backup};Pooling=False");
        dest.Open();
        db.BackupDatabase(dest); // SQLite's online backup API: safe to run against the live, open connection
        return backup;
    }

    // The page names its repo too: front matter's "project: <name>", the "# <name> — User Stories" title,
    // and the raw header block they're rendered from. Only those exact spots change; any other mention of the
    // old name (a ticket summary, a story) is history and stays as written.
    private void RenameInPage(string oldName, string newName, SqliteTransaction tx)
    {
        string Rewrite(string text) => text
            .Replace($"\nproject: {oldName}\n", $"\nproject: {newName}\n", StringComparison.Ordinal)
            .Replace($"\n# {oldName} ", $"\n# {newName} ", StringComparison.Ordinal);
        string RewriteFrontMatter(string fm) => Rewrite($"\n{fm}\n")[1..^1];

        var row = Rows("SELECT project, title, front_matter FROM repos WHERE name = $n", tx,
            r => (Project: r.IsDBNull(0) ? null : r.GetString(0), Title: r.IsDBNull(1) ? null : r.GetString(1), FrontMatter: r.GetString(2)), ("$n", newName)).Single();
        var title = row.Title is { } t && t.StartsWith(oldName + " ", StringComparison.Ordinal) ? newName + t[oldName.Length..] : row.Title;
        Exec("UPDATE repos SET project = $p, title = $t, front_matter = $fm WHERE name = $n", tx,
            ("$p", row.Project == oldName ? newName : row.Project), ("$t", title), ("$fm", RewriteFrontMatter(row.FrontMatter)), ("$n", newName));

        var raw = Rows("SELECT ordinal, text FROM blocks WHERE repo = $n AND kind = 'raw'", tx, r => (Ordinal: r.GetInt32(0), Text: r.GetString(1)), ("$n", newName)).ToList();
        foreach (var (ordinal, text) in raw)
        {
            var rewritten = Rewrite("\n" + text)[1..];
            if (rewritten != text)
                Exec("UPDATE blocks SET text = $t WHERE repo = $n AND ordinal = $o", tx, ("$t", rewritten), ("$n", newName), ("$o", ordinal));
        }
    }

    /// <summary>Every rename on record, oldest first; optionally only those touching <paramref name="name"/> (as old or new).</summary>
    public IReadOnlyList<RepoRename> RepoRenames(string? name = null) =>
        [.. Rows("SELECT old_name, new_name, at, reason FROM repo_renames WHERE $n IS NULL OR old_name = $n COLLATE NOCASE OR new_name = $n COLLATE NOCASE ORDER BY seq",
            r => new RepoRename(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("$n", name))];

    /// <summary>Follows the rename chain from a former repo name to its current one; null if <paramref name="name"/> was never a repo's name.</summary>
    private string? ResolveRenamedName(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var cur = name;
        while (true)
        {
            var next = Rows("SELECT new_name FROM repo_renames WHERE old_name = $o COLLATE NOCASE ORDER BY seq DESC LIMIT 1", r => r.GetString(0), ("$o", cur)).FirstOrDefault();
            if (next is null || !seen.Add(next)) return seen.Count > 1 ? cur : null;
            cur = next;
        }
    }

    // ── Links ───────────────────────────────────────────────────────────────

    /// <summary>Links <paramref name="from"/> to <paramref name="to"/>; <c>requires</c> makes <paramref name="to"/> a prerequisite.</summary>
    public void AddLink(string from, string to, string kind, string? note = null)
    {
        // Trim: a CR from a CRLF script would otherwise land in the ID and the link would point at nothing.
        (from, to) = (from.Trim(), to.Trim());
        if (from.Length == 0 || to.Length == 0) throw new ArgumentException("A link needs both ends.");
        (from, to) = (CurrentId(from), CurrentId(to)); // a former ID links the item it became
        if (!LinkKinds.Contains(kind)) throw new ArgumentException($"Unknown link kind '{kind}' (expected {string.Join(", ", LinkKinds)}).", nameof(kind));
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("An item can't link to itself.");
        if (kind == "requires" && Requires(to, transitive: true).Contains(from, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{to} already (indirectly) requires {from}; that would be a cycle.");
        AddLinkCore(from, to, kind, note);
    }

    private void AddLinkCore(string from, string to, string kind, string? note) =>
        Exec("INSERT OR IGNORE INTO links (from_id, to_id, kind, note) VALUES ($f, $t, $k, $n)", null, ("$f", from), ("$t", to), ("$k", kind), ("$n", note));

    public bool RemoveLink(string from, string to, string kind) =>
        Exec("DELETE FROM links WHERE from_id = $f COLLATE NOCASE AND to_id = $t COLLATE NOCASE AND kind = $k", null, ("$f", CurrentId(from)), ("$t", CurrentId(to)), ("$k", kind)) > 0;

    /// <summary>Every link touching <paramref name="id"/>, in either direction.</summary>
    public IReadOnlyList<Link> Links(string id) =>
        [.. Rows("SELECT from_id, to_id, kind, note FROM links WHERE from_id = $id COLLATE NOCASE OR to_id = $id COLLATE NOCASE ORDER BY kind, from_id, to_id",
            ReadLink, ("$id", id))];

    /// <summary>Every link in the database.</summary>
    public IReadOnlyList<Link> AllLinks() =>
        [.. Rows("SELECT from_id, to_id, kind, note FROM links ORDER BY from_id, kind, to_id", ReadLink)];

    private static Link ReadLink(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));

    /// <summary>What <paramref name="id"/> requires (directly, or all the way down).</summary>
    public IReadOnlyList<string> Requires(string id, bool transitive = false)
    {
        // IDs are stored as typed (a CLI argument, a "blocked by" phrase), so every step of the walk matches without case,
        // like its first step: a case-sensitive join stopped at "BE-T1" vs "be-t1" and let AddLink close a cycle through it.
        var sql = transitive
            ? "WITH RECURSIVE r(x) AS (SELECT to_id FROM links WHERE from_id = $id COLLATE NOCASE AND kind = 'requires' " +
              "UNION SELECT l.to_id FROM links l JOIN r ON l.from_id = r.x COLLATE NOCASE AND l.kind = 'requires') SELECT x FROM r"
            : "SELECT to_id FROM links WHERE from_id = $id COLLATE NOCASE AND kind = 'requires'";
        return [.. Rows(sql, r => r.GetString(0), ("$id", id))];
    }

    // ── Stories ─────────────────────────────────────────────────────────────

    public IReadOnlyList<StoredStory> Stories(string? repo = null) =>
        [.. Rows("SELECT uid, repo, story_id, status, glyph, suffix, text, source, parent_uid, epic, section, evidence, acceptance FROM stories " +
                 "WHERE $r IS NULL OR repo = $r COLLATE NOCASE ORDER BY repo COLLATE NOCASE, ordinal", ReadStory, ("$r", repo))];

    public IReadOnlyList<StoredStory> Story(string id) => StoryRows(id) is { Count: > 0 } found ? found : StoryRows(CurrentId(id));

    private List<StoredStory> StoryRows(string id) =>
        [.. Rows("SELECT uid, repo, story_id, status, glyph, suffix, text, source, parent_uid, epic, section, evidence, acceptance FROM stories " +
                 "WHERE story_id = $id COLLATE NOCASE ORDER BY repo, ordinal", ReadStory, ("$id", id))];

    private static StoredStory ReadStory(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12));

    /// <summary>
    /// Sets a story's status (done, partial or planned), swapping the glyph in its markdown too so the exported file
    /// matches. With several stories under one ID (it happens), pass <paramref name="repo"/>; all of them change otherwise.
    /// </summary>
    public int SetStoryStatus(string id, string status, string? repo = null)
    {
        var glyph = StoryDocument.GlyphOf(status);
        var stories = Story(id).Where(s => repo is null || s.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (stories.Count == 0) throw new KeyNotFoundException($"No story {id}{(repo is null ? "" : " in " + repo)}.");
        using var tx = db.BeginTransaction();
        foreach (var s in stories)
        {
            Exec("UPDATE stories SET status = $s, glyph = $g WHERE uid = $u", tx, ("$s", status), ("$g", glyph), ("$u", s.Uid));
            if (s.Glyph is { } old)
            {
                var text = (string)Scalar("SELECT text FROM blocks WHERE repo = $r AND kind = 'story' AND ref = $u", tx, ("$r", s.Repo), ("$u", s.Uid.ToString(CultureInfo.InvariantCulture)))!;
                var first = text.Split('\n')[0];
                var swapped = s.Source == "table"
                    ? ReplaceCell(first, old, glyph)
                    : StoryDocument.StoryBullet().Replace(first, m => m.Value.Replace($"{m.Groups["id"].Value} {old}", $"{m.Groups["id"].Value} {glyph}", StringComparison.Ordinal), 1);
                Exec("UPDATE blocks SET text = $t WHERE repo = $r AND kind = 'story' AND ref = $u", tx,
                    ("$t", swapped + text[first.Length..]), ("$r", s.Repo), ("$u", s.Uid.ToString(CultureInfo.InvariantCulture)));
            }
        }
        tx.Commit();
        return stories.Count;
    }

    /// <summary>
    /// Adds a story under epic <paramref name="epic"/> with the next free <c>CODE-US-&lt;epic&gt;&lt;n&gt;</c> ID, written as a
    /// bullet after the epic's last story (or its heading). Returns the new ID.
    /// </summary>
    public string AddStory(string repo, string epic, string text, string status = "planned")
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A story needs text.", nameof(text));
        var glyph = StoryDocument.GlyphOf(status);
        var (name, code) = RepoRow(repo);
        var e = Rows("SELECT key FROM epics WHERE repo = $r AND key = $k COLLATE NOCASE", r => r.GetString(0), ("$r", name), ("$k", epic)).FirstOrDefault()
            ?? throw new KeyNotFoundException($"{name} has no epic {epic}; add it with 'tickets epic new'.");
        var prefix = $"{code}-US-{e}";

        // The next free ID is read inside the write transaction, as Add does for tickets: read before it, two agents adding
        // a story to the same epic at once both got the same ID.
        using var tx = db.BeginTransaction();
        var next = 1 + Rows("SELECT story_id FROM stories WHERE repo = $r AND story_id LIKE $p", tx, r => r.GetString(0), ("$r", name), ("$p", prefix + "%"))
            .Select(id => int.TryParse(id[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        var id = prefix + next;
        var line = $"- **{id} {glyph}** {OneLine(text)}";
        // MAX over no rows is NULL (DBNull here), not a missing row.
        var after = Scalar(
            "SELECT MAX(b.ordinal) FROM blocks b WHERE b.repo = $r AND (" +
            "(b.kind = 'epic' AND b.ref = $e) OR (b.kind = 'story' AND b.ref IN (SELECT CAST(uid AS TEXT) FROM stories WHERE repo = $r AND epic = $e)))", tx,
            ("$r", name), ("$e", e)) as long? ?? throw new InvalidOperationException($"{name}'s epic {e} has no heading block.");
        var uid = (long)Scalar(
            "INSERT INTO stories (repo, story_id, epic, status, glyph, suffix, text, source, ordinal) " +
            "VALUES ($r, $id, $e, $s, $g, '', $t, 'bullet', (SELECT COALESCE(MAX(ordinal), -1) + 1 FROM stories WHERE repo = $r)) RETURNING uid", tx,
            ("$r", name), ("$id", id), ("$e", e), ("$s", status), ("$g", glyph), ("$t", OneLine(text)))!;
        InsertBlock(name, after + 1, "story", uid.ToString(CultureInfo.InvariantCulture), line, tx);
        Exec("INSERT OR IGNORE INTO links (from_id, to_id, kind, note) VALUES ($f, $t, 'part-of', 'story under its epic')", tx, ("$f", id), ("$t", $"{code}-EPIC-{e}"));
        tx.Commit();
        return id;
    }

    /// <summary>Rewrites a story's text (its bullet becomes one line; ID, glyph and suffix are kept).</summary>
    public void EditStory(string id, string text, string? repo = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A story needs text.", nameof(text));
        var s = Story(id).Where(x => repo is null || x.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList() switch
        {
            [var one] => one,
            [] => throw new KeyNotFoundException($"No story {id}{(repo is null ? "" : " in " + repo)}."),
            _ => throw new InvalidOperationException($"More than one story is {id}; pass --repo."),
        };
        if (s.Source == "table") throw new InvalidOperationException($"{id} is a table row; edit table stories with a new row instead.");
        var indent = s.Source == "sub-bullet" ? "  " : "";
        var suffix = s.Suffix.Length > 0 ? " " + s.Suffix : "";
        using var tx = db.BeginTransaction();
        Exec("UPDATE stories SET text = $t, evidence = NULL, acceptance = NULL WHERE uid = $u", tx, ("$t", OneLine(text)), ("$u", s.Uid));
        Exec("UPDATE blocks SET text = $x WHERE repo = $r AND kind = 'story' AND ref = $u", tx,
            ("$x", $"{indent}- **{s.Id} {s.Glyph}{suffix}** {OneLine(text)}"), ("$r", s.Repo), ("$u", s.Uid.ToString(CultureInfo.InvariantCulture)));
        tx.Commit();
    }

    /// <summary>Adds an <c>## Epic KEY — title</c> section just before the repo's ticket log.</summary>
    public void AddEpic(string repo, string key, string title)
    {
        var (name, _) = RepoRow(repo);
        if (Scalar("SELECT 1 FROM epics WHERE repo = $r AND key = $k COLLATE NOCASE", null, ("$r", name), ("$k", key)) is not null)
            throw new InvalidOperationException($"{name} already has epic {key}.");
        using var tx = db.BeginTransaction();
        var at = (long)(Scalar("SELECT COALESCE(MIN(ordinal), (SELECT COALESCE(MAX(ordinal), -1) + 1 FROM blocks WHERE repo = $r)) FROM blocks WHERE repo = $r AND kind = 'tickets'", tx, ("$r", name)) ?? 0L);
        Exec("INSERT INTO epics (repo, key, title, ordinal) VALUES ($r, $k, $t, (SELECT COALESCE(MAX(ordinal), -1) + 1 FROM epics WHERE repo = $r))", tx,
            ("$r", name), ("$k", key), ("$t", OneLine(title)));
        // A blank line before the heading, and one after it, so the view reads like the files did.
        InsertBlock(name, at, "epic", key, $"## Epic {key} — {OneLine(title)}", tx);
        InsertBlock(name, at, "raw", null, "", tx);
        InsertBlock(name, at + 2, "raw", null, "", tx);
        tx.Commit();
    }

    /// <summary>Appends a numbered item to the repo's priority backlog; story IDs in it are linked.</summary>
    public void AddBacklogItem(string repo, string text)
    {
        var (name, _) = RepoRow(repo);
        // Position and number are read inside the write transaction: read before it, two concurrent adds took the same
        // number and block position.
        using var tx = db.BeginTransaction();
        var last = Rows("SELECT b.ordinal, i.ordinal FROM blocks b JOIN backlog_items i ON i.repo = b.repo AND CAST(i.uid AS TEXT) = b.ref " +
                        "WHERE b.repo = $r AND b.kind = 'backlog' ORDER BY b.ordinal DESC LIMIT 1", tx, r => (r.GetInt64(0), r.GetInt64(1)), ("$r", name)).ToList();
        var heading = Rows("SELECT ordinal, text FROM blocks WHERE repo = $r AND kind = 'raw' AND text LIKE '%## Priority backlog%'", tx, r => (r.GetInt64(0), r.GetString(1)), ("$r", name)).FirstOrDefault();
        if (last.Count == 0 && heading.Item2 is null) throw new InvalidOperationException($"{name} has no '## Priority backlog' section.");
        var position = last.Count > 0 ? last[0].Item1 + 1 : heading.Item1 + 1;
        var number = Rows("SELECT COUNT(*) FROM backlog_items WHERE repo = $r", tx, r => r.GetInt64(0), ("$r", name)).Single() + 1;
        if (last.Count == 0)
        {
            // The heading's raw block can run on past the backlog (blank lines, later sections): split it where the backlog's
            // content ends, so the first item lands in the backlog rather than after whatever follows it.
            var lines = heading.Item2!.Split('\n');
            var at = Array.FindIndex(lines, l => l.Contains("## Priority backlog", StringComparison.Ordinal));
            var end = Array.FindIndex(lines, at + 1, l => l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("## ", StringComparison.Ordinal));
            if (end < 0) end = lines.Length;
            while (end - 1 > at && lines[end - 1].Trim().Length == 0) end--;
            if (end < lines.Length)
            {
                Exec("UPDATE blocks SET text = $t WHERE repo = $r AND ordinal = $o", tx, ("$t", string.Join("\n", lines[..end])), ("$r", name), ("$o", heading.Item1));
                InsertBlock(name, heading.Item1 + 1, "raw", null, string.Join("\n", lines[end..]), tx);
            }
        }
        var item = (long)Scalar("INSERT INTO backlog_items (repo, ordinal, text) VALUES ($r, $o, $t) RETURNING uid", tx, ("$r", name), ("$o", position), ("$t", OneLine(text)))!;
        foreach (var sid in StoryDocument.StoryIdRef().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal))
            Exec("INSERT OR IGNORE INTO backlog_refs (item_uid, story_id) VALUES ($i, $s)", tx, ("$i", item), ("$s", sid));
        InsertBlock(name, position, "backlog", item.ToString(CultureInfo.InvariantCulture), $"{number}. {OneLine(text)}", tx);
        tx.Commit();
    }

    private (string Name, string Code) RepoRow(string repo) =>
        FindRepoRow(repo) ?? throw new KeyNotFoundException($"{repo} isn't in the ticket database.");

    /// <summary>The repo's current name and code, following its rename history from a former name; null if unknown.</summary>
    private (string Name, string Code)? FindRepoRow(string repo)
    {
        (string, string)? Lookup(string name) =>
            Rows("SELECT name, code FROM repos WHERE name = $r COLLATE NOCASE", r => (r.GetString(0), r.GetString(1)), ("$r", name))
                .Cast<(string, string)?>().FirstOrDefault();
        return Lookup(repo) ?? (ResolveRenamedName(repo) is { } current ? Lookup(current) : null);
    }

    /// <summary>Inserts a block at <paramref name="ordinal"/>, moving every later block of the repo down one.</summary>
    private void InsertBlock(string repo, long ordinal, string kind, string? reference, string text, SqliteTransaction tx)
    {
        // Two steps, so the (repo, ordinal) key never collides mid-update.
        Exec("UPDATE blocks SET ordinal = -ordinal - 1 WHERE repo = $r AND ordinal >= $o", tx, ("$r", repo), ("$o", ordinal));
        Exec("UPDATE blocks SET ordinal = -ordinal WHERE repo = $r AND ordinal < 0", tx, ("$r", repo));
        Exec("INSERT INTO blocks (repo, ordinal, kind, ref, text) VALUES ($r, $o, $k, $ref, $t)", tx, ("$r", repo), ("$o", ordinal), ("$k", kind), ("$ref", reference), ("$t", text));
    }

    private static string OneLine(string text) => string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();

    private static string ReplaceCell(string row, string old, string glyph)
    {
        var at = row.LastIndexOf(old, StringComparison.Ordinal);
        return at < 0 ? row : row[..at] + glyph + row[(at + old.Length)..];
    }

    // ── Checks and dump ─────────────────────────────────────────────────────

    /// <summary>Problems: bad ticket IDs/dates/types, codes that don't match, link endpoints that aren't a ticket, story or CODE-EPIC-KEY epic, and requires-cycles.</summary>
    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>();
        foreach (var (name, code) in Repos())
            problems.AddRange(TicketRules.Validate(code, [.. Tickets(name).Select(t => t.Ticket)]).Select(p => $"{name}: {p}"));
        // Every endpoint must resolve to a ticket, a story or an epic by its CODE-EPIC-KEY id (the repo's current code).
        var ids = Rows("SELECT id FROM tickets UNION SELECT story_id FROM stories UNION SELECT r.code || '-EPIC-' || e.key FROM epics e JOIN repos r ON r.name = e.repo",
            r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in AllLinks())
        {
            if (!ids.Contains(l.From)) problems.Add($"link {Escaped(l.From)} {l.Kind} {Escaped(l.To)}: no item {Shown(l.From)}");
            if (!ids.Contains(l.To)) problems.Add($"link {Escaped(l.From)} {l.Kind} {Escaped(l.To)}: no item {Shown(l.To)}");
        }
        foreach (var id in Rows("SELECT DISTINCT from_id FROM links WHERE kind = 'requires'", r => r.GetString(0)))
            if (Requires(id, transitive: true).Contains(id, StringComparer.OrdinalIgnoreCase)) problems.Add($"{id}: requires itself through a cycle");
        return problems;
    }

    // An endpoint with stray whitespace (a CR from a CRLF script, say) prints the same as a good ID; say so.
    private static string Shown(string id) =>
        id.Trim() == id ? id : id.Trim().Length == 0 ? "(blank; run 'tickets db check --fix')" : $"{id.Trim()} (stray whitespace; run 'tickets db check --fix')";

    /// <summary>
    /// Repairs links whose endpoint has stray whitespace: a blank endpoint is removed, a padded one is trimmed (removed
    /// instead when the trimmed link already exists), each change going in the from-ticket's history; and a story's
    /// part-of link to its epic under an old repo code moves to the current CODE-EPIC-KEY. Every change is returned.
    /// </summary>
    public IReadOnlyList<string> RepairLinks()
    {
        var log = new List<string>();
        var tickets = Rows("SELECT id FROM tickets", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var padded = AllLinks().Where(l => l.From.Trim() != l.From || l.To.Trim() != l.To).ToList();
        using var tx = db.BeginTransaction();
        foreach (var l in padded)
        {
            var (from, to) = (l.From.Trim(), l.To.Trim());
            Exec("DELETE FROM links WHERE from_id = $f AND to_id = $t AND kind = $k", tx, ("$f", l.From), ("$t", l.To), ("$k", l.Kind));
            string what;
            if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                what = "removed (blank endpoint)";
            else if (Exec("INSERT OR IGNORE INTO links (from_id, to_id, kind, note) VALUES ($f, $t, $k, $n)", tx, ("$f", from), ("$t", to), ("$k", l.Kind), ("$n", l.Note)) > 0)
                what = $"trimmed to {from} {l.Kind} {to}";
            else
                what = "removed (the trimmed link already exists)";
            var line = $"link {Escaped(l.From)} {l.Kind} {Escaped(l.To)}: {what}";
            log.Add(line);
            if (tickets.Contains(from))
                Exec("INSERT INTO ticket_events (ticket_id, at, field, old, new) VALUES ($id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), 'link', $o, $n)", tx,
                    ("$id", tickets.First(t => string.Equals(t, from, StringComparison.OrdinalIgnoreCase))), ("$o", $"{l.Kind} {Escaped(l.To)}"), ("$n", what.StartsWith("trimmed") ? $"{l.Kind} {to}" : null));
        }
        // A story's part-of link to its epic under a code its repo no longer has (Prose's SS-EPIC-G, its code now PRS):
        // retarget it to the repo's current CODE-EPIC-KEY when that epic exists.
        foreach (var (story, oldTo, newTo) in Rows("""
                SELECT l.from_id, l.to_id, r.code || '-EPIC-' || e.key FROM links l
                JOIN stories s ON s.story_id = l.from_id COLLATE NOCASE JOIN repos r ON r.name = s.repo
                JOIN epics e ON e.repo = s.repo AND l.to_id LIKE '%-EPIC-' || e.key AND substr(l.to_id, 1, length(l.to_id) - length(e.key) - 6) <> ''
                WHERE l.kind = 'part-of' AND upper(l.to_id) <> upper(r.code || '-EPIC-' || e.key)
                  AND NOT EXISTS (SELECT 1 FROM epics e2 JOIN repos r2 ON r2.name = e2.repo WHERE upper(r2.code || '-EPIC-' || e2.key) = upper(l.to_id))
                """, tx, r => (r.GetString(0), r.GetString(1), r.GetString(2))).ToList())
        {
            Exec("DELETE FROM links WHERE from_id = $f AND to_id = $t AND kind = 'part-of'", tx, ("$f", story), ("$t", oldTo));
            var added = Exec("INSERT OR IGNORE INTO links (from_id, to_id, kind, note) VALUES ($f, $t, 'part-of', 'story under its epic')", tx, ("$f", story), ("$t", newTo)) > 0;
            log.Add($"link {story} part-of {oldTo}: " + (added ? $"retargeted to {newTo} (the repo's current code)" : $"removed ({story} part-of {newTo} already exists)"));
        }
        tx.Commit();
        return log;
    }

    private static string Escaped(string id) => id.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    /// <summary>
    /// A readable, deterministic SQL dump of the whole database (one INSERT per row, in key order), committed
    /// beside it so changes show up as text diffs.
    /// </summary>
    public string Dump()
    {
        var sb = new StringBuilder("-- MindAttic.Tickets database dump (generated by 'tickets db sync'; the .db file is the store).\n");
        foreach (var (table, order) in new[]
        {
            ("meta", "key"), ("repos", "name"), ("epics", "repo, key"), ("stories", "uid"), ("backlog_items", "uid"), ("backlog_refs", "item_uid, story_id"),
            ("tickets", "repo, ordinal"), ("ticket_events", "seq"), ("ticket_queue", "ticket_id"), ("repo_renames", "seq"), ("id_history", "seq"), ("links", "from_id, to_id, kind"), ("blocks", "repo, ordinal"),
        })
        {
            sb.Append("\n-- ").Append(table).Append('\n');
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY {order}";
            using var r = cmd.ExecuteReader();
            var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
            while (r.Read())
            {
                sb.Append("INSERT INTO ").Append(table).Append(" (").Append(string.Join(", ", cols)).Append(") VALUES (");
                sb.Append(string.Join(", ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL"
                    : r.GetValue(i) is long n ? n.ToString(CultureInfo.InvariantCulture)
                    : "'" + r.GetValue(i).ToString()!.Replace("'", "''", StringComparison.Ordinal) + "'")));
                sb.Append(");\n");
            }
        }
        return sb.ToString();
    }

    // ── Plumbing ────────────────────────────────────────────────────────────

    private int Exec(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        return cmd.ExecuteScalar();
    }

    private List<T> Rows<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args) =>
        Rows(sql, null, read, args);

    private List<T> Rows<T>(string sql, SqliteTransaction? tx, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx, (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    [GeneratedRegex(@"blocked by (?<id>(?:[A-Z]\.)?[A-Z][A-Z0-9]*-T\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BlockedBy();

    [GeneratedRegex(@"superseded by (?<id>(?:[A-Z]\.)?[A-Z][A-Z0-9]*-T\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SupersededBy();

    [GeneratedRegex(@"\b(?:[A-Z]\.)?[A-Z][A-Z0-9]*-T\d+\b")]
    private static partial Regex TicketId();
}
