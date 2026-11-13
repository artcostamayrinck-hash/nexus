using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Common;
using Nexus.Contracts.Transfers;
using Nexus.Core.Errors;
using Nexus.Core.Transfers;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

/// <summary>
/// Persists file transfer queue items, resume offsets, and history in SQLite.
/// </summary>
public sealed class SqliteTransferRepository : ITransferRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteTransferRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result<FileTransferRecord>> UpsertAsync(
        FileTransferRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var safeRecord = record with { AutoExecute = false };

        return _failureHandler.ExecuteAsync("Transfer.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO FileTransfers (
                    TransferId, CorrelationId, SourceDeviceId, SourceDeviceName,
                    TargetDeviceId, TargetDeviceName, OriginalFileName, SafeFileName,
                    ContentType, TotalBytes, TransferredBytes, ServerUploadedBytes,
                    Sha256Checksum, Direction, State, Stage, DuplicatePolicy,
                    FailureReason, FailureMessage, LocalFilePath, BytesPerSecond,
                    ChunkSizeBytes, AutoExecute, CreatedAtUtc, UpdatedAtUtc,
                    CompletedAtUtc, ContractVersion
                )
                VALUES (
                    $transferId, $correlationId, $sourceId, $sourceName,
                    $targetId, $targetName, $originalName, $safeName,
                    $contentType, $totalBytes, $transferredBytes, $serverUploadedBytes,
                    $sha256, $direction, $state, $stage, $duplicatePolicy,
                    $failureReason, $failureMessage, $localPath, $bytesPerSecond,
                    $chunkSize, 0, $createdAt, $updatedAt,
                    $completedAt, $contractVersion
                )
                ON CONFLICT(TransferId) DO UPDATE SET
                    SafeFileName = excluded.SafeFileName,
                    TransferredBytes = excluded.TransferredBytes,
                    ServerUploadedBytes = excluded.ServerUploadedBytes,
                    State = excluded.State,
                    Stage = excluded.Stage,
                    DuplicatePolicy = excluded.DuplicatePolicy,
                    FailureReason = excluded.FailureReason,
                    FailureMessage = excluded.FailureMessage,
                    LocalFilePath = COALESCE(excluded.LocalFilePath, FileTransfers.LocalFilePath),
                    BytesPerSecond = excluded.BytesPerSecond,
                    UpdatedAtUtc = excluded.UpdatedAtUtc,
                    CompletedAtUtc = excluded.CompletedAtUtc;
                """;

            cmd.Parameters.AddWithValue("$transferId", safeRecord.TransferId.ToString("D"));
            cmd.Parameters.AddWithValue("$correlationId", safeRecord.CorrelationId.ToString("D"));
            cmd.Parameters.AddWithValue("$sourceId", safeRecord.SourceDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$sourceName", safeRecord.SourceDeviceName);
            cmd.Parameters.AddWithValue("$targetId", safeRecord.TargetDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$targetName", safeRecord.TargetDeviceName);
            cmd.Parameters.AddWithValue("$originalName", safeRecord.OriginalFileName);
            cmd.Parameters.AddWithValue("$safeName", safeRecord.SafeFileName);
            cmd.Parameters.AddWithValue("$contentType", safeRecord.ContentType);
            cmd.Parameters.AddWithValue("$totalBytes", safeRecord.TotalBytes);
            cmd.Parameters.AddWithValue("$transferredBytes", safeRecord.TransferredBytes);
            cmd.Parameters.AddWithValue("$serverUploadedBytes", safeRecord.ServerUploadedBytes);
            cmd.Parameters.AddWithValue("$sha256", safeRecord.Sha256Checksum);
            cmd.Parameters.AddWithValue("$direction", (int)safeRecord.Direction);
            cmd.Parameters.AddWithValue("$state", (int)safeRecord.State);
            cmd.Parameters.AddWithValue("$stage", (int)safeRecord.Stage);
            cmd.Parameters.AddWithValue("$duplicatePolicy", (int)safeRecord.DuplicatePolicy);
            cmd.Parameters.AddWithValue("$failureReason", (int)safeRecord.FailureReason);
            cmd.Parameters.AddWithValue("$failureMessage", (object?)safeRecord.FailureMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$localPath", (object?)safeRecord.LocalFilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bytesPerSecond", safeRecord.BytesPerSecond);
            cmd.Parameters.AddWithValue("$chunkSize", safeRecord.ChunkSizeBytes);
            cmd.Parameters.AddWithValue("$createdAt", safeRecord.CreatedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$updatedAt", safeRecord.UpdatedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$completedAt", safeRecord.CompletedAtUtc.HasValue
                ? safeRecord.CompletedAtUtc.Value.ToUniversalTime().ToString("O")
                : DBNull.Value);
            cmd.Parameters.AddWithValue("$contractVersion", safeRecord.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return safeRecord;
        }, cancellationToken);
    }

    public Task<Result<FileTransferRecord?>> GetByIdAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<FileTransferRecord?>("Transfer.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT TransferId, CorrelationId, SourceDeviceId, SourceDeviceName,
                       TargetDeviceId, TargetDeviceName, OriginalFileName, SafeFileName,
                       ContentType, TotalBytes, TransferredBytes, ServerUploadedBytes,
                       Sha256Checksum, Direction, State, Stage, DuplicatePolicy,
                       FailureReason, FailureMessage, LocalFilePath, BytesPerSecond,
                       ChunkSizeBytes, AutoExecute, CreatedAtUtc, UpdatedAtUtc,
                       CompletedAtUtc, ContractVersion
                FROM FileTransfers
                WHERE TransferId = $transferId
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$transferId", transferId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return ReadRecord(reader);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<FileTransferRecord>>> ListAsync(
        TransferDirection? direction = null,
        TransferState? state = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 1000);

        return _failureHandler.ExecuteAsync<IReadOnlyList<FileTransferRecord>>("Transfer.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();

            var conditions = new List<string>();
            if (direction.HasValue)
            {
                conditions.Add("Direction = $direction");
                cmd.Parameters.AddWithValue("$direction", (int)direction.Value);
            }

            if (state.HasValue)
            {
                conditions.Add("State = $state");
                cmd.Parameters.AddWithValue("$state", (int)state.Value);
            }

            var whereClause = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : string.Empty;

            cmd.CommandText = $"""
                SELECT TransferId, CorrelationId, SourceDeviceId, SourceDeviceName,
                       TargetDeviceId, TargetDeviceName, OriginalFileName, SafeFileName,
                       ContentType, TotalBytes, TransferredBytes, ServerUploadedBytes,
                       Sha256Checksum, Direction, State, Stage, DuplicatePolicy,
                       FailureReason, FailureMessage, LocalFilePath, BytesPerSecond,
                       ChunkSizeBytes, AutoExecute, CreatedAtUtc, UpdatedAtUtc,
                       CompletedAtUtc, ContractVersion
                FROM FileTransfers
                {whereClause}
                ORDER BY CreatedAtUtc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            var items = new List<FileTransferRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(ReadRecord(reader));
            }

            return items;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<FileTransferRecord>>> ListResumableAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<FileTransferRecord>>("Transfer.ListResumable", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT TransferId, CorrelationId, SourceDeviceId, SourceDeviceName,
                       TargetDeviceId, TargetDeviceName, OriginalFileName, SafeFileName,
                       ContentType, TotalBytes, TransferredBytes, ServerUploadedBytes,
                       Sha256Checksum, Direction, State, Stage, DuplicatePolicy,
                       FailureReason, FailureMessage, LocalFilePath, BytesPerSecond,
                       ChunkSizeBytes, AutoExecute, CreatedAtUtc, UpdatedAtUtc,
                       CompletedAtUtc, ContractVersion
                FROM FileTransfers
                WHERE State IN ($pending, $inProgress, $paused, $failed)
                ORDER BY CreatedAtUtc ASC;
                """;
            cmd.Parameters.AddWithValue("$pending", (int)TransferState.Pending);
            cmd.Parameters.AddWithValue("$inProgress", (int)TransferState.InProgress);
            cmd.Parameters.AddWithValue("$paused", (int)TransferState.Paused);
            cmd.Parameters.AddWithValue("$failed", (int)TransferState.Failed);

            var items = new List<FileTransferRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var record = ReadRecord(reader);
                if (record.State != TransferState.Failed
                    || record.FailureReason == TransferFailureReason.NetworkInterrupted)
                {
                    items.Add(record);
                }
            }

            return items;
        }, cancellationToken);
    }

    public Task<Result<int>> DeleteFinishedHistoryAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Transfer.DeleteFinishedHistory", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM FileTransfers
                WHERE State IN ($completed, $failed, $cancelled);
                """;
            cmd.Parameters.AddWithValue("$completed", (int)TransferState.Completed);
            cmd.Parameters.AddWithValue("$failed", (int)TransferState.Failed);
            cmd.Parameters.AddWithValue("$cancelled", (int)TransferState.Cancelled);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static FileTransferRecord ReadRecord(SqliteDataReader reader)
    {
        return new FileTransferRecord
        {
            TransferId = Guid.Parse(reader.GetString(0)),
            CorrelationId = Guid.Parse(reader.GetString(1)),
            SourceDeviceId = Guid.Parse(reader.GetString(2)),
            SourceDeviceName = reader.GetString(3),
            TargetDeviceId = Guid.Parse(reader.GetString(4)),
            TargetDeviceName = reader.GetString(5),
            OriginalFileName = reader.GetString(6),
            SafeFileName = reader.GetString(7),
            ContentType = reader.GetString(8),
            TotalBytes = reader.GetInt64(9),
            TransferredBytes = reader.GetInt64(10),
            ServerUploadedBytes = reader.GetInt64(11),
            Sha256Checksum = reader.GetString(12),
            Direction = (TransferDirection)reader.GetInt32(13),
            State = (TransferState)reader.GetInt32(14),
            Stage = (TransferStage)reader.GetInt32(15),
            DuplicatePolicy = (TransferDuplicatePolicy)reader.GetInt32(16),
            FailureReason = (TransferFailureReason)reader.GetInt32(17),
            FailureMessage = reader.IsDBNull(18) ? null : reader.GetString(18),
            LocalFilePath = reader.IsDBNull(19) ? null : reader.GetString(19),
            BytesPerSecond = reader.GetDouble(20),
            ChunkSizeBytes = reader.GetInt32(21),
            AutoExecute = false,
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(23), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(24), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CompletedAtUtc = reader.IsDBNull(25)
                ? null
                : DateTimeOffset.Parse(reader.GetString(25), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ContractVersion = reader.IsDBNull(26) ? ContractVersions.Current : reader.GetString(26)
        };
    }
}

