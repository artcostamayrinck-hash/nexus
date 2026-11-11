using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

/// <summary>
/// SQLite repository for local LAN, Tailscale VPN, and remote device endpoints.
/// </summary>
public sealed class SqliteEndpointRepository : IEndpointRegistryRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteEndpointRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result<IReadOnlyList<DeviceEndpointRecord>>> ListAsync(
        Guid? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<DeviceEndpointRecord>>("DeviceEndpoints.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = deviceId.HasValue
                ? """
                  SELECT EndpointId, DeviceId, DeviceName, Label, RouteKind, Transport, EndpointUrl, Priority,
                         ExpectedTlsThumbprint, ExpectedHostKeyFingerprint, Origin, IsEnabled,
                         LastLatencyMs, LastVerifiedAtUtc, LastConnectedAtUtc, LastError,
                         CreatedAtUtc, UpdatedAtUtc, ContractVersion
                  FROM DeviceEndpoints
                  WHERE DeviceId = $deviceId
                  ORDER BY Priority ASC, UpdatedAtUtc DESC;
                  """
                : """
                  SELECT EndpointId, DeviceId, DeviceName, Label, RouteKind, Transport, EndpointUrl, Priority,
                         ExpectedTlsThumbprint, ExpectedHostKeyFingerprint, Origin, IsEnabled,
                         LastLatencyMs, LastVerifiedAtUtc, LastConnectedAtUtc, LastError,
                         CreatedAtUtc, UpdatedAtUtc, ContractVersion
                  FROM DeviceEndpoints
                  ORDER BY Priority ASC, UpdatedAtUtc DESC;
                  """;

            if (deviceId.HasValue)
            {
                cmd.Parameters.AddWithValue("$deviceId", deviceId.Value.ToString("D"));
            }

            var items = new List<DeviceEndpointRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapRecord(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result<DeviceEndpointRecord?>> GetByIdAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<DeviceEndpointRecord?>("DeviceEndpoints.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EndpointId, DeviceId, DeviceName, Label, RouteKind, Transport, EndpointUrl, Priority,
                       ExpectedTlsThumbprint, ExpectedHostKeyFingerprint, Origin, IsEnabled,
                       LastLatencyMs, LastVerifiedAtUtc, LastConnectedAtUtc, LastError,
                       CreatedAtUtc, UpdatedAtUtc, ContractVersion
                FROM DeviceEndpoints
                WHERE EndpointId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", endpointId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRecord(reader) : null;
        }, cancellationToken);
    }

    public async Task<Result<DeviceEndpointRecord>> UpsertAsync(
        DeviceEndpointRecord endpoint,
        CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(endpoint);
        if (!validation.IsValid)
        {
            return Result<DeviceEndpointRecord>.Failure(NexusError.Validation(validation));
        }

        var normalizedUrl = NormalizeUrl(endpoint.EndpointUrl);
        return await _failureHandler.ExecuteAsync("DeviceEndpoints.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

            // Check if the same DeviceId + EndpointUrl already exists so re-discovery / re-save updates cleanly.
            Guid effectiveId = endpoint.EndpointId;
            DateTimeOffset createdAt = endpoint.CreatedAtUtc;

            await using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = """
                    SELECT EndpointId, CreatedAtUtc FROM DeviceEndpoints
                    WHERE DeviceId = $deviceId AND LOWER(EndpointUrl) = LOWER($url)
                    LIMIT 1;
                    """;
                findCmd.Parameters.AddWithValue("$deviceId", endpoint.DeviceId.ToString("D"));
                findCmd.Parameters.AddWithValue("$url", normalizedUrl);

                await using var existing = await findCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await existing.ReadAsync(ct).ConfigureAwait(false))
                {
                    effectiveId = Guid.Parse(existing.GetString(0));
                    createdAt = DateTimeOffset.Parse(existing.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                }
            }

            var stored = endpoint with
            {
                EndpointId = effectiveId,
                EndpointUrl = normalizedUrl,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DeviceEndpoints (
                    EndpointId, DeviceId, DeviceName, Label, RouteKind, Transport, EndpointUrl, Priority,
                    ExpectedTlsThumbprint, ExpectedHostKeyFingerprint, Origin, IsEnabled,
                    LastLatencyMs, LastVerifiedAtUtc, LastConnectedAtUtc, LastError,
                    CreatedAtUtc, UpdatedAtUtc, ContractVersion
                )
                VALUES (
                    $endpointId, $deviceId, $deviceName, $label, $routeKind, $transport, $endpointUrl, $priority,
                    $thumbprint, $fingerprint, $origin, $isEnabled,
                    $lastLatency, $lastVerified, $lastConnected, $lastError,
                    $createdAt, $updatedAt, $contractVersion
                )
                ON CONFLICT(EndpointId) DO UPDATE SET
                    DeviceId = excluded.DeviceId,
                    DeviceName = excluded.DeviceName,
                    Label = excluded.Label,
                    RouteKind = excluded.RouteKind,
                    Transport = excluded.Transport,
                    EndpointUrl = excluded.EndpointUrl,
                    Priority = excluded.Priority,
                    ExpectedTlsThumbprint = COALESCE(excluded.ExpectedTlsThumbprint, DeviceEndpoints.ExpectedTlsThumbprint),
                    ExpectedHostKeyFingerprint = COALESCE(excluded.ExpectedHostKeyFingerprint, DeviceEndpoints.ExpectedHostKeyFingerprint),
                    Origin = excluded.Origin,
                    IsEnabled = excluded.IsEnabled,
                    LastLatencyMs = COALESCE(excluded.LastLatencyMs, DeviceEndpoints.LastLatencyMs),
                    LastVerifiedAtUtc = COALESCE(excluded.LastVerifiedAtUtc, DeviceEndpoints.LastVerifiedAtUtc),
                    LastConnectedAtUtc = COALESCE(excluded.LastConnectedAtUtc, DeviceEndpoints.LastConnectedAtUtc),
                    LastError = excluded.LastError,
                    UpdatedAtUtc = excluded.UpdatedAtUtc,
                    ContractVersion = excluded.ContractVersion;
                """;

            cmd.Parameters.AddWithValue("$endpointId", stored.EndpointId.ToString("D"));
            cmd.Parameters.AddWithValue("$deviceId", stored.DeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$deviceName", stored.DeviceName);
            cmd.Parameters.AddWithValue("$label", stored.Label);
            cmd.Parameters.AddWithValue("$routeKind", stored.RouteKind.ToString());
            cmd.Parameters.AddWithValue("$transport", stored.Transport.ToString());
            cmd.Parameters.AddWithValue("$endpointUrl", stored.EndpointUrl);
            cmd.Parameters.AddWithValue("$priority", stored.Priority);
            cmd.Parameters.AddWithValue("$thumbprint", (object?)stored.ExpectedTlsThumbprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fingerprint", (object?)stored.ExpectedHostKeyFingerprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$origin", stored.Origin.ToString());
            cmd.Parameters.AddWithValue("$isEnabled", stored.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$lastLatency", (object?)stored.LastLatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastVerified", (object?)stored.LastVerifiedAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastConnected", (object?)stored.LastConnectedAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastError", (object?)stored.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", stored.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$updatedAt", stored.UpdatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$contractVersion", stored.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return stored;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result> DeleteAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("DeviceEndpoints.Delete", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM DeviceEndpoints WHERE EndpointId = $id;";
            cmd.Parameters.AddWithValue("$id", endpointId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> UpdateTelemetryAsync(
        Guid endpointId,
        int? latencyMs,
        DateTimeOffset? verifiedAtUtc,
        DateTimeOffset? connectedAtUtc,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("DeviceEndpoints.UpdateTelemetry", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE DeviceEndpoints
                SET LastLatencyMs = COALESCE($latency, LastLatencyMs),
                    LastVerifiedAtUtc = COALESCE($verifiedAt, LastVerifiedAtUtc),
                    LastConnectedAtUtc = COALESCE($connectedAt, LastConnectedAtUtc),
                    LastError = $lastError,
                    UpdatedAtUtc = $updatedAt
                WHERE EndpointId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", endpointId.ToString("D"));
            cmd.Parameters.AddWithValue("$latency", (object?)latencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$verifiedAt", (object?)verifiedAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$connectedAt", (object?)connectedAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static string NormalizeUrl(string url)
        => url.Trim().TrimEnd('/');

    private static DeviceEndpointRecord MapRecord(SqliteDataReader reader)
    {
        return new DeviceEndpointRecord
        {
            EndpointId = Guid.Parse(reader.GetString(0)),
            DeviceId = Guid.Parse(reader.GetString(1)),
            DeviceName = reader.GetString(2),
            Label = reader.GetString(3),
            RouteKind = Enum.TryParse<ConnectionRouteKind>(reader.GetString(4), out var rk) ? rk : ConnectionRouteKind.LocalLan,
            Transport = Enum.TryParse<TransportKind>(reader.GetString(5), out var tk) ? tk : TransportKind.LocalNetwork,
            EndpointUrl = reader.GetString(6),
            Priority = reader.GetInt32(7),
            ExpectedTlsThumbprint = reader.IsDBNull(8) ? null : reader.GetString(8),
            ExpectedHostKeyFingerprint = reader.IsDBNull(9) ? null : reader.GetString(9),
            Origin = Enum.TryParse<EndpointOrigin>(reader.GetString(10), out var org) ? org : EndpointOrigin.Manual,
            IsEnabled = reader.GetInt32(11) == 1,
            LastLatencyMs = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            LastVerifiedAtUtc = reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastConnectedAtUtc = reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastError = reader.IsDBNull(15) ? null : reader.GetString(15),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ContractVersion = reader.GetString(18)
        };
    }
}

