using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Common;
using Nexus.Contracts.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteDeviceRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result<DeviceInfo?>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<DeviceInfo?>("Devices.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Name, Platform, Type, OperatingSystemVersion, AppVersion,
                       HardwareFingerprint, Capabilities, Status, RegisteredAtUtc, LastSeenAtUtc, ContractVersion
                FROM Devices
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapDevice(reader) : null;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DeviceInfo>>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<DeviceInfo>>("Devices.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Name, Platform, Type, OperatingSystemVersion, AppVersion,
                       HardwareFingerprint, Capabilities, Status, RegisteredAtUtc, LastSeenAtUtc, ContractVersion
                FROM Devices
                ORDER BY LastSeenAtUtc DESC;
                """;

            var items = new List<DeviceInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapDevice(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DeviceInfo>>> ListByPlatformAsync(DevicePlatform platform, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<DeviceInfo>>("Devices.ListByPlatform", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Name, Platform, Type, OperatingSystemVersion, AppVersion,
                       HardwareFingerprint, Capabilities, Status, RegisteredAtUtc, LastSeenAtUtc, ContractVersion
                FROM Devices
                WHERE Platform = $platform
                ORDER BY Name ASC;
                """;
            cmd.Parameters.AddWithValue("$platform", platform.ToString());

            var items = new List<DeviceInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapDevice(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result> UpsertAsync(DeviceInfo entity, CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(entity);
        if (!validation.IsValid)
        {
            return Task.FromResult(Result.Failure(NexusError.Validation(validation)));
        }

        return _failureHandler.ExecuteAsync("Devices.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Devices (
                    Id, Name, Platform, Type, OperatingSystemVersion, AppVersion,
                    HardwareFingerprint, Capabilities, Status, RegisteredAtUtc, LastSeenAtUtc, ContractVersion)
                VALUES (
                    $id, $name, $platform, $type, $os, $app,
                    $fp, $caps, $status, $reg, $seen, $ver)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Platform = excluded.Platform,
                    Type = excluded.Type,
                    OperatingSystemVersion = excluded.OperatingSystemVersion,
                    AppVersion = excluded.AppVersion,
                    HardwareFingerprint = excluded.HardwareFingerprint,
                    Capabilities = excluded.Capabilities,
                    Status = excluded.Status,
                    LastSeenAtUtc = excluded.LastSeenAtUtc,
                    ContractVersion = excluded.ContractVersion;
                """;

            cmd.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$name", entity.Name);
            cmd.Parameters.AddWithValue("$platform", entity.Platform.ToString());
            cmd.Parameters.AddWithValue("$type", entity.Type.ToString());
            cmd.Parameters.AddWithValue("$os", entity.OperatingSystemVersion);
            cmd.Parameters.AddWithValue("$app", entity.AppVersion);
            cmd.Parameters.AddWithValue("$fp", (object?)entity.HardwareFingerprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$caps", (int)entity.Capabilities);
            cmd.Parameters.AddWithValue("$status", entity.Status.ToString());
            cmd.Parameters.AddWithValue("$reg", entity.RegisteredAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$seen", entity.LastSeenAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$ver", entity.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> UpdateStatusAsync(Guid deviceId, DeviceStatus status, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Devices.UpdateStatus", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE Devices
                SET Status = $status, LastSeenAtUtc = $seen
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$seen", seenAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Devices.Delete", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Devices WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));

            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return rows > 0;
        }, cancellationToken);
    }

    private static DeviceInfo MapDevice(SqliteDataReader reader)
    {
        return new DeviceInfo
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            Platform = Enum.Parse<DevicePlatform>(reader.GetString(2), ignoreCase: true),
            Type = Enum.Parse<DeviceType>(reader.GetString(3), ignoreCase: true),
            OperatingSystemVersion = reader.GetString(4),
            AppVersion = reader.GetString(5),
            HardwareFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
            Capabilities = (DeviceCapability)reader.GetInt32(7),
            Status = Enum.Parse<DeviceStatus>(reader.GetString(8), ignoreCase: true),
            RegisteredAtUtc = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastSeenAtUtc = DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ContractVersion = reader.GetString(11)
        };
    }
}

