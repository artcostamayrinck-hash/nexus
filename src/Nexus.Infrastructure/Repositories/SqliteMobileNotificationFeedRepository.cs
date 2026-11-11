using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Notifications;
using Nexus.Core.Errors;
using Nexus.Core.Modules;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

/// <summary>
/// Persists forwarded Android notifications and desktop inbox history in SQLite.
/// </summary>
public sealed class SqliteMobileNotificationFeedRepository : IMobileNotificationFeedRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteMobileNotificationFeedRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result> SaveAsync(MobileNotificationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _failureHandler.ExecuteAsync("MobileNotifications.Save", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO MobileNotifications (
                    NotificationId, SourceDeviceId, TargetDeviceId, AppName, PackageName,
                    Title, Body, Urgency, IsDismissed, ForwardedToDesktop,
                    FilterOutcome, CreatedAtUtc, ReceivedAtUtc, ContractVersion
                )
                VALUES (
                    $id, $sourceId, $targetId, $appName, $packageName,
                    $title, $body, $urgency, $isDismissed, $forwarded,
                    $filterOutcome, $createdAt, $receivedAt, $version
                )
                ON CONFLICT(NotificationId) DO UPDATE SET
                    IsDismissed = excluded.IsDismissed,
                    ForwardedToDesktop = excluded.ForwardedToDesktop,
                    FilterOutcome = excluded.FilterOutcome,
                    ReceivedAtUtc = excluded.ReceivedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$id", record.NotificationId.ToString("D"));
            cmd.Parameters.AddWithValue("$sourceId", record.SourceDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$targetId", record.TargetDeviceId?.ToString("D") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$appName", record.AppName);
            cmd.Parameters.AddWithValue("$packageName", (object?)record.PackageName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", record.Title);
            cmd.Parameters.AddWithValue("$body", record.Body);
            cmd.Parameters.AddWithValue("$urgency", (int)record.Urgency);
            cmd.Parameters.AddWithValue("$isDismissed", record.IsDismissed ? 1 : 0);
            cmd.Parameters.AddWithValue("$forwarded", record.ForwardedToDesktop ? 1 : 0);
            cmd.Parameters.AddWithValue("$filterOutcome", record.FilterOutcome);
            cmd.Parameters.AddWithValue("$createdAt", record.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$receivedAt", record.ReceivedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$version", record.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<MobileNotificationRecord>>> ListRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        return _failureHandler.ExecuteAsync<IReadOnlyList<MobileNotificationRecord>>("MobileNotifications.ListRecent", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT NotificationId, SourceDeviceId, TargetDeviceId, AppName, PackageName,
                       Title, Body, Urgency, IsDismissed, ForwardedToDesktop,
                       FilterOutcome, CreatedAtUtc, ReceivedAtUtc, ContractVersion
                FROM MobileNotifications
                ORDER BY ReceivedAtUtc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", safeLimit);

            var items = new List<MobileNotificationRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapRecord(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public async Task<Result> MarkDismissedAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        var res = await _failureHandler.ExecuteAsync<int>("MobileNotifications.MarkDismissed", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE MobileNotifications SET IsDismissed = 1 WHERE NotificationId = $id;";
            cmd.Parameters.AddWithValue("$id", notificationId.ToString("D"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (res.IsFailure)
        {
            return Result.Failure(res.Error!);
        }

        return res.Value > 0
            ? Result.Success()
            : Result.Failure(NexusError.NotFound($"Notificação '{notificationId:D}' não encontrada.", "NOTIFY_NOT_FOUND"));
    }

    public Task<Result<int>> ClearAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("MobileNotifications.Clear", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MobileNotifications;";
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static MobileNotificationRecord MapRecord(SqliteDataReader reader)
    {
        return new MobileNotificationRecord
        {
            NotificationId = Guid.Parse(reader.GetString(0)),
            SourceDeviceId = Guid.Parse(reader.GetString(1)),
            TargetDeviceId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            AppName = reader.GetString(3),
            PackageName = reader.IsDBNull(4) ? null : reader.GetString(4),
            Title = reader.GetString(5),
            Body = reader.GetString(6),
            Urgency = (NotificationUrgency)reader.GetInt32(7),
            IsDismissed = reader.GetInt32(8) != 0,
            ForwardedToDesktop = reader.GetInt32(9) != 0,
            FilterOutcome = reader.GetString(10),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            ReceivedAtUtc = DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            ContractVersion = reader.GetString(13)
        };
    }
}
