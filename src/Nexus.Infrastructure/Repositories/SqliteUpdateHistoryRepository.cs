using System.Globalization;
using Nexus.Contracts.Updates;
using Nexus.Core.Errors;
using Nexus.Core.Updates;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

/// <summary>
/// SQLite repository persisting NEXUS update check, verification, and installation prompt outcomes.
/// </summary>
public sealed class SqliteUpdateHistoryRepository : IUpdateHistoryRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteUpdateHistoryRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result> AppendAsync(UpdateHistoryRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _failureHandler.ExecuteAsync("UpdateHistory.Append", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UpdateHistory (
                    RecordId, InstalledVersion, DiscoveredVersion, Visibility, OutcomeStatus, FailureReason, Message, RecordedAtUtc, ContractVersion
                )
                VALUES ($id, $installed, $discovered, $visibility, $status, $failure, $message, $recordedAt, $version);
                """;
            cmd.Parameters.AddWithValue("$id", record.RecordId.ToString("D"));
            cmd.Parameters.AddWithValue("$installed", record.InstalledVersion);
            cmd.Parameters.AddWithValue("$discovered", record.DiscoveredVersion ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$visibility", (int)record.Visibility);
            cmd.Parameters.AddWithValue("$status", (int)record.OutcomeStatus);
            cmd.Parameters.AddWithValue("$failure", (int)record.FailureReason);
            cmd.Parameters.AddWithValue("$message", record.Message);
            cmd.Parameters.AddWithValue("$recordedAt", record.RecordedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$version", record.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<UpdateHistoryRecord>>> ListRecentAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, 200);
        return _failureHandler.ExecuteAsync<IReadOnlyList<UpdateHistoryRecord>>("UpdateHistory.ListRecent", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT RecordId, InstalledVersion, DiscoveredVersion, Visibility, OutcomeStatus, FailureReason, Message, RecordedAtUtc, ContractVersion
                FROM UpdateHistory
                ORDER BY RecordedAtUtc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", bounded);

            var items = new List<UpdateHistoryRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new UpdateHistoryRecord
                {
                    RecordId = Guid.Parse(reader.GetString(0)),
                    InstalledVersion = reader.GetString(1),
                    DiscoveredVersion = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Visibility = (UpdateRepositoryVisibility)reader.GetInt32(3),
                    OutcomeStatus = (UpdateStatusKind)reader.GetInt32(4),
                    FailureReason = (UpdateFailureReason)reader.GetInt32(5),
                    Message = reader.GetString(6),
                    RecordedAtUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    ContractVersion = reader.GetString(8)
                });
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }
}

