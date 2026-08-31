using Microsoft.Data.Sqlite;
using Speech2Issues.Core.Models;

namespace Speech2Issues.Core.Storage;

public sealed class HistoryRepository(AppPaths paths)
{
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = paths.HistoryFile,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                destination INTEGER NOT NULL,
                target TEXT NOT NULL,
                status INTEGER NOT NULL,
                title TEXT NOT NULL,
                transcript TEXT NOT NULL,
                draft_json TEXT NOT NULL,
                external_url TEXT NULL,
                error TEXT NULL,
                audio_path TEXT NULL,
                project_id TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_created_at ON history(created_at DESC);
            CREATE TABLE IF NOT EXISTS deliveries (
                draft_id TEXT NOT NULL,
                binding_id TEXT NOT NULL,
                destination INTEGER NOT NULL,
                target_id TEXT NOT NULL,
                target TEXT NOT NULL,
                status INTEGER NOT NULL,
                external_url TEXT NULL,
                error TEXT NULL,
                PRIMARY KEY (draft_id, binding_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(history);";
        var hasProjectId = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                hasProjectId |= string.Equals(reader.GetString(1), "project_id", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (!hasProjectId)
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE history ADD COLUMN project_id TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO history
              (id, created_at, destination, target, status, title, transcript, draft_json, external_url, error, audio_path, project_id)
            VALUES
              ($id, $createdAt, $destination, $target, $status, $title, $transcript, $draftJson, $externalUrl, $error, $audioPath, $projectId)
            ON CONFLICT(id) DO UPDATE SET
              status = excluded.status,
              title = excluded.title,
              transcript = excluded.transcript,
              draft_json = excluded.draft_json,
              external_url = excluded.external_url,
              error = excluded.error,
              audio_path = excluded.audio_path,
              project_id = excluded.project_id;
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$destination", (int)entry.Destination);
        command.Parameters.AddWithValue("$target", entry.Target);
        command.Parameters.AddWithValue("$status", (int)entry.Status);
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$transcript", entry.Transcript);
        command.Parameters.AddWithValue("$draftJson", entry.DraftJson);
        command.Parameters.AddWithValue("$externalUrl", (object?)entry.ExternalUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)entry.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$audioPath", (object?)entry.AudioPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)entry.ProjectId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertDeliveryAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO deliveries (draft_id, binding_id, destination, target_id, target, status, external_url, error)
            VALUES ($draftId, $bindingId, $destination, $targetId, $target, $status, $externalUrl, $error)
            ON CONFLICT(draft_id, binding_id) DO UPDATE SET
              target_id = excluded.target_id,
              target = excluded.target,
              status = excluded.status,
              external_url = excluded.external_url,
              error = excluded.error;
            """;
        command.Parameters.AddWithValue("$draftId", attempt.DraftId);
        command.Parameters.AddWithValue("$bindingId", attempt.BindingId);
        command.Parameters.AddWithValue("$destination", (int)attempt.Destination);
        command.Parameters.AddWithValue("$targetId", attempt.TargetId);
        command.Parameters.AddWithValue("$target", attempt.Target);
        command.Parameters.AddWithValue("$status", (int)attempt.Status);
        command.Parameters.AddWithValue("$externalUrl", (object?)attempt.ExternalUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)attempt.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryAttempt>> GetDeliveriesAsync(string draftId, CancellationToken cancellationToken = default)
    {
        var result = new List<DeliveryAttempt>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT draft_id, binding_id, destination, target_id, target, status, external_url, error
            FROM deliveries WHERE draft_id = $draftId ORDER BY destination, target;
            """;
        command.Parameters.AddWithValue("$draftId", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DeliveryAttempt(
                reader.GetString(0), reader.GetString(1), (DestinationKind)reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), (HistoryStatus)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var result = new List<HistoryEntry>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_at, destination, target, status, title, transcript, draft_json, external_url, error, audio_path, project_id
            FROM history ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HistoryEntry(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                (DestinationKind)reader.GetInt32(2),
                reader.GetString(3),
                (HistoryStatus)reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return result;
    }

    public async Task<int> CountCreatedTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM history WHERE status IN ($sent, $partiallySent);";
        command.Parameters.AddWithValue("$sent", (int)HistoryStatus.Sent);
        command.Parameters.AddWithValue("$partiallySent", (int)HistoryStatus.PartiallySent);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
