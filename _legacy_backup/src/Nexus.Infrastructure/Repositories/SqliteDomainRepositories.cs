using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Common;
using Nexus.Contracts.Identity;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Sync;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

public sealed class SqliteSessionRepository : ISessionRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteSessionRepository(ISqliteConnectionFactory connectionFactory, StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result<SessionInfo?>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<SessionInfo?>("Sessions.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, UserId, DeviceId, Status, IssuedAtUtc, ExpiresAtUtc, ClientAddress, ContractVersion
                FROM UserSessions WHERE SessionId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapSession(reader) : null;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<SessionInfo>>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<SessionInfo>>("Sessions.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, UserId, DeviceId, Status, IssuedAtUtc, ExpiresAtUtc, ClientAddress, ContractVersion
                FROM UserSessions ORDER BY IssuedAtUtc DESC;
                """;

            var items = new List<SessionInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapSession(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<SessionInfo>>> ListActiveByDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<SessionInfo>>("Sessions.ListActiveByDevice", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, UserId, DeviceId, Status, IssuedAtUtc, ExpiresAtUtc, ClientAddress, ContractVersion
                FROM UserSessions
                WHERE DeviceId = $deviceId AND Status = $status
                ORDER BY IssuedAtUtc DESC;
                """;
            cmd.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());

            var items = new List<SessionInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapSession(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result> UpsertAsync(SessionInfo entity, CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(entity);
        if (!validation.IsValid)
        {
            return Task.FromResult(Result.Failure(NexusError.Validation(validation)));
        }

        return _failureHandler.ExecuteAsync("Sessions.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UserSessions (SessionId, UserId, DeviceId, Status, IssuedAtUtc, ExpiresAtUtc, ClientAddress, ContractVersion)
                VALUES ($sid, $uid, $did, $status, $issued, $expires, $addr, $ver)
                ON CONFLICT(SessionId) DO UPDATE SET
                    Status = excluded.Status,
                    ExpiresAtUtc = excluded.ExpiresAtUtc,
                    ClientAddress = excluded.ClientAddress,
                    ContractVersion = excluded.ContractVersion;
                """;
            cmd.Parameters.AddWithValue("$sid", entity.SessionId.ToString("D"));
            cmd.Parameters.AddWithValue("$uid", entity.UserId.ToString("D"));
            cmd.Parameters.AddWithValue("$did", entity.DeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$status", entity.Status.ToString());
            cmd.Parameters.AddWithValue("$issued", entity.IssuedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$expires", entity.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$addr", (object?)entity.ClientAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", entity.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Sessions.Revoke", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE UserSessions SET Status = $status WHERE SessionId = $sid;";
            cmd.Parameters.AddWithValue("$sid", sessionId.ToString("D"));
            cmd.Parameters.AddWithValue("$status", SessionStatus.Revoked.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Sessions.Delete", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM UserSessions WHERE SessionId = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken);
    }

    private static SessionInfo MapSession(SqliteDataReader reader)
    {
        return new SessionInfo
        {
            SessionId = Guid.Parse(reader.GetString(0)),
            UserId = Guid.Parse(reader.GetString(1)),
            DeviceId = Guid.Parse(reader.GetString(2)),
            Status = Enum.Parse<SessionStatus>(reader.GetString(3), ignoreCase: true),
            IssuedAtUtc = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ExpiresAtUtc = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ClientAddress = reader.IsDBNull(6) ? null : reader.GetString(6),
            ContractVersion = reader.GetString(7)
        };
    }
}

public sealed class SqliteNotificationRepository : INotificationRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;
    private readonly IJsonSerializer _serializer;

    public SqliteNotificationRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler,
        IJsonSerializer serializer)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
        _serializer = serializer;
    }

    public Task<Result<DeviceNotification?>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<DeviceNotification?>("Notifications.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT NotificationId, SourceDeviceId, TargetDeviceId, AppName, Title, Body,
                       Urgency, IsDismissed, ActionsJson, CreatedAtUtc, ExpiresAtUtc, ContractVersion
                FROM Notifications WHERE NotificationId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapNotification(reader) : null;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DeviceNotification>>> ListAsync(CancellationToken cancellationToken = default)
        => QueryAsync("SELECT NotificationId, SourceDeviceId, TargetDeviceId, AppName, Title, Body, Urgency, IsDismissed, ActionsJson, CreatedAtUtc, ExpiresAtUtc, ContractVersion FROM Notifications ORDER BY CreatedAtUtc DESC;", null, cancellationToken);

    public Task<Result<IReadOnlyList<DeviceNotification>>> ListPendingAsync(int limit = 100, CancellationToken cancellationToken = default)
        => QueryAsync("SELECT NotificationId, SourceDeviceId, TargetDeviceId, AppName, Title, Body, Urgency, IsDismissed, ActionsJson, CreatedAtUtc, ExpiresAtUtc, ContractVersion FROM Notifications WHERE IsDismissed = 0 ORDER BY CreatedAtUtc DESC LIMIT $limit;", Math.Clamp(limit, 1, 500), cancellationToken);

    private Task<Result<IReadOnlyList<DeviceNotification>>> QueryAsync(string sql, int? limit, CancellationToken cancellationToken)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<DeviceNotification>>("Notifications.Query", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (limit.HasValue)
            {
                cmd.Parameters.AddWithValue("$limit", limit.Value);
            }

            var items = new List<DeviceNotification>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapNotification(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result> UpsertAsync(DeviceNotification entity, CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(entity);
        if (!validation.IsValid)
        {
            return Task.FromResult(Result.Failure(NexusError.Validation(validation)));
        }

        return _failureHandler.ExecuteAsync("Notifications.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Notifications (
                    NotificationId, SourceDeviceId, TargetDeviceId, AppName, Title, Body,
                    Urgency, IsDismissed, ActionsJson, CreatedAtUtc, ExpiresAtUtc, ContractVersion)
                VALUES (
                    $id, $src, $tgt, $app, $title, $body,
                    $urgency, $dismissed, $actions, $created, $expires, $ver)
                ON CONFLICT(NotificationId) DO UPDATE SET
                    Title = excluded.Title,
                    Body = excluded.Body,
                    Urgency = excluded.Urgency,
                    IsDismissed = excluded.IsDismissed,
                    ActionsJson = excluded.ActionsJson;
                """;
            cmd.Parameters.AddWithValue("$id", entity.NotificationId.ToString("D"));
            cmd.Parameters.AddWithValue("$src", entity.SourceDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$tgt", entity.TargetDeviceId.HasValue ? entity.TargetDeviceId.Value.ToString("D") : DBNull.Value);
            cmd.Parameters.AddWithValue("$app", entity.AppName);
            cmd.Parameters.AddWithValue("$title", entity.Title);
            cmd.Parameters.AddWithValue("$body", entity.Body);
            cmd.Parameters.AddWithValue("$urgency", entity.Urgency.ToString());
            cmd.Parameters.AddWithValue("$dismissed", entity.IsDismissed ? 1 : 0);
            cmd.Parameters.AddWithValue("$actions", _serializer.Serialize(entity.Actions));
            cmd.Parameters.AddWithValue("$created", entity.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$expires", entity.ExpiresAtUtc.HasValue ? entity.ExpiresAtUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", entity.ContractVersion);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> MarkDismissedAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Notifications.MarkDismissed", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Notifications SET IsDismissed = 1 WHERE NotificationId = $id;";
            cmd.Parameters.AddWithValue("$id", notificationId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Notifications.Delete", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Notifications WHERE NotificationId = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken);
    }

    private DeviceNotification MapNotification(SqliteDataReader reader)
    {
        var actions = _serializer.Deserialize<List<NotificationAction>>(reader.GetString(8)) ?? [];
        return new DeviceNotification
        {
            NotificationId = Guid.Parse(reader.GetString(0)),
            SourceDeviceId = Guid.Parse(reader.GetString(1)),
            TargetDeviceId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            AppName = reader.GetString(3),
            Title = reader.GetString(4),
            Body = reader.GetString(5),
            Urgency = Enum.Parse<NotificationUrgency>(reader.GetString(6), ignoreCase: true),
            IsDismissed = reader.GetInt32(7) == 1,
            Actions = actions.AsReadOnly(),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ExpiresAtUtc = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ContractVersion = reader.GetString(11)
        };
    }
}

public sealed class SqliteSyncStateRepository : ISyncStateRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;
    private readonly IJsonSerializer _serializer;
    private readonly IClock _clock;

    public SqliteSyncStateRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler,
        IJsonSerializer serializer,
        IClock clock)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
        _serializer = serializer;
        _clock = clock;
    }

    public Task<Result<long>> GetRevisionAsync(string scope, Guid targetDeviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope) || targetDeviceId == Guid.Empty)
        {
            return Task.FromResult(Result<long>.Failure(NexusError.Validation("Scope and TargetDeviceId are required.", code: "SYNC_QUERY_INVALID")));
        }

        return _failureHandler.ExecuteAsync("Sync.GetRevision", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Revision FROM SyncCheckpoints WHERE Scope = $scope AND TargetDeviceId = $target;";
            cmd.Parameters.AddWithValue("$scope", scope.Trim());
            cmd.Parameters.AddWithValue("$target", targetDeviceId.ToString("D"));

            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return scalar is null || scalar is DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public Task<Result> SaveCheckpointAsync(SyncBatchContract batch, CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(batch);
        if (!validation.IsValid)
        {
            return Task.FromResult(Result.Failure(NexusError.Validation(validation)));
        }

        return _failureHandler.ExecuteAsync("Sync.SaveCheckpoint", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SyncCheckpoints (Scope, TargetDeviceId, Revision, Status, ItemsJson, UpdatedAtUtc)
                VALUES ($scope, $target, $rev, $status, $items, $updated)
                ON CONFLICT(Scope, TargetDeviceId) DO UPDATE SET
                    Revision = excluded.Revision,
                    Status = excluded.Status,
                    ItemsJson = excluded.ItemsJson,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$scope", batch.Scope.Trim());
            cmd.Parameters.AddWithValue("$target", batch.TargetDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$rev", batch.TargetRevision);
            cmd.Parameters.AddWithValue("$status", batch.Status.ToString());
            cmd.Parameters.AddWithValue("$items", _serializer.Serialize(batch.Items));
            cmd.Parameters.AddWithValue("$updated", _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}

