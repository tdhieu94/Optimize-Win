using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OW.Core.Journal;

/// <summary>
/// Durable record of every change OW makes, stored in SQLite.
/// An operation is written as <see cref="OperationState.Pending"/> before the change happens,
/// so after a crash or power loss the service can tell exactly what was in flight.
/// </summary>
public sealed class SqliteJournal : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public SqliteJournal(string databasePath, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA foreign_keys=ON;");
        Execute("""
            CREATE TABLE IF NOT EXISTS batches (
                id          TEXT PRIMARY KEY,
                title       TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                state       TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS operations (
                id          TEXT PRIMARY KEY,
                batch_id    TEXT NOT NULL REFERENCES batches(id),
                seq         INTEGER NOT NULL,
                kind        TEXT NOT NULL,
                target      TEXT NOT NULL,
                backup      TEXT NULL,
                size_bytes  INTEGER NOT NULL,
                state       TEXT NOT NULL,
                message     TEXT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_operations_batch ON operations(batch_id, seq);
            CREATE INDEX IF NOT EXISTS ix_operations_state ON operations(state);
            """);
    }

    public BatchRecord CreateBatch(string title)
    {
        var batch = new BatchRecord(Guid.NewGuid(), title, _time.GetUtcNow(), BatchState.Running);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO batches (id, title, created_utc, state) VALUES ($id, $title, $created, $state);";
            cmd.Parameters.AddWithValue("$id", batch.Id.ToString());
            cmd.Parameters.AddWithValue("$title", batch.Title);
            cmd.Parameters.AddWithValue("$created", Format(batch.CreatedUtc));
            cmd.Parameters.AddWithValue("$state", batch.State.ToString());
            cmd.ExecuteNonQuery();
        }

        return batch;
    }

    public void SetBatchState(Guid batchId, BatchState state)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE batches SET state = $state WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", batchId.ToString());
            cmd.Parameters.AddWithValue("$state", state.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public OperationRecord AddOperation(
        Guid batchId,
        int sequence,
        OperationKind kind,
        string targetPath,
        string? backupPath,
        long sizeBytes,
        OperationState state,
        string? message = null,
        Guid? id = null)
    {
        var op = new OperationRecord(
            id ?? Guid.NewGuid(), batchId, sequence, kind, targetPath, backupPath, sizeBytes, state, message, _time.GetUtcNow());
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO operations (id, batch_id, seq, kind, target, backup, size_bytes, state, message, updated_utc)
                VALUES ($id, $batch, $seq, $kind, $target, $backup, $size, $state, $message, $updated);
                """;
            cmd.Parameters.AddWithValue("$id", op.Id.ToString());
            cmd.Parameters.AddWithValue("$batch", batchId.ToString());
            cmd.Parameters.AddWithValue("$seq", sequence);
            cmd.Parameters.AddWithValue("$kind", kind.ToString());
            cmd.Parameters.AddWithValue("$target", targetPath);
            cmd.Parameters.AddWithValue("$backup", (object?)backupPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$state", state.ToString());
            cmd.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", Format(op.UpdatedUtc));
            cmd.ExecuteNonQuery();
        }

        return op;
    }

    /// <summary>Moves an operation to a new state. <paramref name="targetPath"/> overrides the stored target (used when a restore lands under a new name).</summary>
    public void UpdateOperation(Guid operationId, OperationState state, string? message = null, string? targetPath = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE operations
                SET state = $state, message = $message, updated_utc = $updated, target = COALESCE($target, target)
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", operationId.ToString());
            cmd.Parameters.AddWithValue("$state", state.ToString());
            cmd.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", Format(_time.GetUtcNow()));
            cmd.Parameters.AddWithValue("$target", (object?)targetPath ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public BatchRecord? GetBatch(Guid batchId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, title, created_utc, state FROM batches WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", batchId.ToString());
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadBatch(r) : null;
        }
    }

    public IReadOnlyList<BatchRecord> GetRecentBatches(int maxCount)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, title, created_utc, state FROM batches ORDER BY created_utc DESC LIMIT $max;";
            cmd.Parameters.AddWithValue("$max", Math.Clamp(maxCount, 1, 1000));
            using var r = cmd.ExecuteReader();
            var list = new List<BatchRecord>();
            while (r.Read())
            {
                list.Add(ReadBatch(r));
            }

            return list;
        }
    }

    public IReadOnlyList<OperationRecord> GetOperations(Guid batchId) =>
        QueryOperations("WHERE batch_id = $p ORDER BY seq;", batchId.ToString());

    public IReadOnlyList<OperationRecord> GetOperationsInState(OperationState state) =>
        QueryOperations("WHERE state = $p ORDER BY updated_utc;", state.ToString());

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private IReadOnlyList<OperationRecord> QueryOperations(string where, string parameter)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, batch_id, seq, kind, target, backup, size_bytes, state, message, updated_utc FROM operations " + where;
            cmd.Parameters.AddWithValue("$p", parameter);
            using var r = cmd.ExecuteReader();
            var list = new List<OperationRecord>();
            while (r.Read())
            {
                list.Add(new OperationRecord(
                    Guid.Parse(r.GetString(0)),
                    Guid.Parse(r.GetString(1)),
                    r.GetInt32(2),
                    Enum.Parse<OperationKind>(r.GetString(3)),
                    r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetInt64(6),
                    Enum.Parse<OperationState>(r.GetString(7)),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    Parse(r.GetString(9))));
            }

            return list;
        }
    }

    private static BatchRecord ReadBatch(SqliteDataReader r) =>
        new(Guid.Parse(r.GetString(0)), r.GetString(1), Parse(r.GetString(2)), Enum.Parse<BatchState>(r.GetString(3)));

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
