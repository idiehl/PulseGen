using System.Text.Json;
using Microsoft.Data.Sqlite;
using PulseGen.Contracts;

namespace PulseGen.Workspace;

public sealed record AutosaveRevision(string RevisionId, Guid ProjectId, string ManifestJson, DateTimeOffset CreatedAtUtc);

public sealed record SyncJournalEntry(long Id, Guid ProjectId, string? BaseRevisionId, string Operation, string PayloadJson, string State, DateTimeOffset CreatedAtUtc);
public sealed record ProjectSyncState(Guid ProjectId, Guid HeadRevisionId, string HeadETag, DateTimeOffset UpdatedAtUtc);

public sealed class WorkspaceIndex : IDisposable
{
    private readonly string connectionString;

    public WorkspaceIndex(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public static WorkspaceIndex CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas Universalis", "PulseGen");
        return new WorkspaceIndex(Path.Combine(root, "workspace-index.sqlite3"));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS asset_cache (
                sha256 TEXT PRIMARY KEY CHECK(length(sha256) = 64),
                kind TEXT NOT NULL,
                local_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
                verified_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS autosave_revision (
                revision_id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                manifest_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_autosave_project_created
                ON autosave_revision(project_id, created_at_utc DESC);
            CREATE TABLE IF NOT EXISTS sync_journal (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id TEXT NOT NULL,
                base_revision_id TEXT NULL,
                operation TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('pending', 'inflight', 'complete', 'conflict', 'failed')),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sync_state_id ON sync_journal(state, id);
            CREATE TABLE IF NOT EXISTS project_sync_state (
                project_id TEXT PRIMARY KEY,
                head_revision_id TEXT NOT NULL,
                head_etag TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAssetAsync(AssetRef asset, string localPath, CancellationToken cancellationToken = default)
    {
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Asset identity is not a SHA-256 value.", nameof(asset));
        }
        var fullPath = Path.GetFullPath(localPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length != asset.SizeBytes || !string.Equals(await ProjectStore.ComputeSha256Async(fullPath, cancellationToken), asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Cached asset does not match its content identity.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO asset_cache(sha256, kind, local_path, size_bytes, verified_at_utc)
            VALUES($sha256, $kind, $path, $size, $verified)
            ON CONFLICT(sha256) DO UPDATE SET
                kind = excluded.kind,
                local_path = excluded.local_path,
                size_bytes = excluded.size_bytes,
                verified_at_utc = excluded.verified_at_utc;
            """;
        command.Parameters.AddWithValue("$sha256", asset.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue("$kind", asset.Kind.ToString());
        command.Parameters.AddWithValue("$path", fullPath);
        command.Parameters.AddWithValue("$size", asset.SizeBytes);
        command.Parameters.AddWithValue("$verified", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AutosaveRevision> AppendAutosaveAsync(SceneDocument scene, int keepLatest = 50, CancellationToken cancellationToken = default)
    {
        SceneValidator.EnsureValid(scene);
        if (keepLatest < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLatest));
        }
        var manifest = JsonSerializer.Serialize(scene);
        var created = DateTimeOffset.UtcNow;
        var revision = new AutosaveRevision(Guid.NewGuid().ToString("N"), scene.ProjectId, manifest, created);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO autosave_revision(revision_id, project_id, manifest_json, created_at_utc) VALUES($revision, $project, $manifest, $created);";
            insert.Parameters.AddWithValue("$revision", revision.RevisionId);
            insert.Parameters.AddWithValue("$project", scene.ProjectId.ToString("D"));
            insert.Parameters.AddWithValue("$manifest", manifest);
            insert.Parameters.AddWithValue("$created", created.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = (SqliteTransaction)transaction;
            prune.CommandText = """
                DELETE FROM autosave_revision
                WHERE project_id = $project AND revision_id NOT IN (
                    SELECT revision_id FROM autosave_revision
                    WHERE project_id = $project ORDER BY created_at_utc DESC LIMIT $keep
                );
                """;
            prune.Parameters.AddWithValue("$project", scene.ProjectId.ToString("D"));
            prune.Parameters.AddWithValue("$keep", keepLatest);
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return revision;
    }

    public async Task<AutosaveRevision?> GetLatestAutosaveAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision_id, manifest_json, created_at_utc FROM autosave_revision WHERE project_id = $project ORDER BY created_at_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), projectId, reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)))
            : null;
    }

    public async Task<long> EnqueueSyncAsync(Guid projectId, string? baseRevisionId, string operation, object payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Sync operation is required.", nameof(operation));
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_journal(project_id, base_revision_id, operation, payload_json, state, created_at_utc, updated_at_utc)
            VALUES($project, $base, $operation, $payload, 'pending', $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$base", (object?)baseRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$now", now);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Sync journal did not return an ID."));
    }

    public async Task MarkSyncStateAsync(long id, string state, CancellationToken cancellationToken = default)
    {
        if (state is not ("pending" or "inflight" or "complete" or "conflict" or "failed"))
        {
            throw new ArgumentException("Unknown sync journal state.", nameof(state));
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sync_journal SET state = $state, updated_at_utc = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"Sync journal entry {id} does not exist.");
        }
    }

    public async Task UpdateSyncPayloadAsync(long id, object payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sync_journal SET payload_json = $payload, updated_at_utc = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"Sync journal entry {id} does not exist.");
        }
    }

    public async Task<SyncJournalEntry?> GetLatestRecoverableSyncAsync(
        Guid projectId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, base_revision_id, payload_json, state, created_at_utc
            FROM sync_journal
            WHERE project_id = $project AND operation = $operation AND state IN ('pending', 'inflight')
            ORDER BY id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetInt64(0),
                projectId,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                operation,
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)))
            : null;
    }

    public async Task<ProjectSyncState?> GetProjectSyncStateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT head_revision_id, head_etag, updated_at_utc FROM project_sync_state WHERE project_id = $project;";
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (!Guid.TryParse(reader.GetString(0), out var revision) || string.IsNullOrWhiteSpace(reader.GetString(1)))
        {
            throw new InvalidDataException("The local Atlas project sync state is malformed.");
        }
        return new(projectId, revision, reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)));
    }

    public async Task UpsertProjectSyncStateAsync(
        Guid projectId,
        Guid headRevisionId,
        string headETag,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty || headRevisionId == Guid.Empty || string.IsNullOrWhiteSpace(headETag) || headETag.Length > 256)
        {
            throw new ArgumentException("A bounded Atlas project head is required.");
        }
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO project_sync_state(project_id, head_revision_id, head_etag, updated_at_utc)
            VALUES($project, $revision, $etag, $updated)
            ON CONFLICT(project_id) DO UPDATE SET
                head_revision_id = excluded.head_revision_id,
                head_etag = excluded.head_etag,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        command.Parameters.AddWithValue("$revision", headRevisionId.ToString("D"));
        command.Parameters.AddWithValue("$etag", headETag);
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
