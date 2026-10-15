using Microsoft.Data.Sqlite;

namespace MindAttic.Workflow.Tickets;

/// <summary>
/// A disposable SQLite cache of every repo's tickets. Markdown stays the only ticket store
/// (WF-LAW-1); this file is rebuilt from scratch each time from those tables, so it is never
/// hand-edited and is always safe to delete — the next <see cref="Rebuild"/> reconstructs it.
/// </summary>
public static class TicketIndex
{
    /// <summary>Default location, relative to the MindAttic.Workflow repo root.</summary>
    public const string DefaultRelativePath = "data/tickets.db";

    /// <summary>
    /// Rebuilds the SQLite file at <paramref name="dbPath"/> from <paramref name="repos"/>'
    /// current <c>docs/USER_STORIES.md</c> tables. Replaces any existing file. Returns the
    /// number of ticket rows written.
    /// </summary>
    public static int Rebuild(IEnumerable<Repo> repos, string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(dbPath)) File.Delete(dbPath);

        var rows = WorkspaceTickets.All(repos);

        // Pooling off: a pooled connection keeps the file handle open after Dispose, which would
        // make the next Rebuild's File.Delete (and a caller's own cleanup) fail with a sharing violation.
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE tickets (
                    repo TEXT NOT NULL,
                    id TEXT NOT NULL,
                    date TEXT NOT NULL,
                    type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    ref TEXT NOT NULL,
                    PRIMARY KEY (repo, id)
                );
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            create.ExecuteNonQuery();
        }

        using var tx = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO tickets (repo, id, date, type, status, summary, ref) " +
                "VALUES ($repo, $id, $date, $type, $status, $summary, $ref)";
            var pRepo = insert.Parameters.Add("$repo", SqliteType.Text);
            var pId = insert.Parameters.Add("$id", SqliteType.Text);
            var pDate = insert.Parameters.Add("$date", SqliteType.Text);
            var pType = insert.Parameters.Add("$type", SqliteType.Text);
            var pStatus = insert.Parameters.Add("$status", SqliteType.Text);
            var pSummary = insert.Parameters.Add("$summary", SqliteType.Text);
            var pRef = insert.Parameters.Add("$ref", SqliteType.Text);

            foreach (var row in rows)
            {
                pRepo.Value = row.Repo.Name;
                pId.Value = row.Ticket.Id;
                pDate.Value = row.Ticket.Date;
                pType.Value = row.Ticket.Type;
                pStatus.Value = row.Ticket.Status;
                pSummary.Value = row.Ticket.Summary;
                pRef.Value = row.Ticket.Ref;
                insert.ExecuteNonQuery();
            }
        }

        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "INSERT INTO meta (key, value) VALUES ('generated_at', $v)";
            meta.Parameters.AddWithValue("$v", DateTime.UtcNow.ToString("O"));
            meta.ExecuteNonQuery();
        }

        tx.Commit();
        return rows.Count;
    }
}
