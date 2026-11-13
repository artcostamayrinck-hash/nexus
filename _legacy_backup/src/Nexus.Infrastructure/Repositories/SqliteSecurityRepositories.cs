using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Common;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

public sealed class SqliteTrustedDeviceRepository : ITrustedDeviceRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteTrustedDeviceRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result<TrustedDeviceRecord?>> GetByIdAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<TrustedDeviceRecord?>("TrustedDevices.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DeviceId, CustomName, Platform, DeviceType, OperatingSystemVersion, AppVersion,
                       PublicKeySpkiBase64, PublicKeyFingerprint, Permissions, TrustStatus,
                       PairedAtUtc, LastAccessAtUtc, RevokedAtUtc, RevocationReason, ContractVersion
                FROM TrustedDevices WHERE DeviceId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRecord(reader) : null;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAsync(CancellationToken cancellationToken = default)
        => QueryListAsync(null, cancellationToken);

    public Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAuthorizedAsync(CancellationToken cancellationToken = default)
        => QueryListAsync(DeviceTrustStatus.Authorized, cancellationToken);

    private Task<Result<IReadOnlyList<TrustedDeviceRecord>>> QueryListAsync(DeviceTrustStatus? statusFilter, CancellationToken cancellationToken)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<TrustedDeviceRecord>>("TrustedDevices.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statusFilter.HasValue
                ? """
                  SELECT DeviceId, CustomName, Platform, DeviceType, OperatingSystemVersion, AppVersion,
                         PublicKeySpkiBase64, PublicKeyFingerprint, Permissions, TrustStatus,
                         PairedAtUtc, LastAccessAtUtc, RevokedAtUtc, RevocationReason, ContractVersion
                  FROM TrustedDevices WHERE TrustStatus = $status ORDER BY LastAccessAtUtc DESC;
                  """
                : """
                  SELECT DeviceId, CustomName, Platform, DeviceType, OperatingSystemVersion, AppVersion,
                         PublicKeySpkiBase64, PublicKeyFingerprint, Permissions, TrustStatus,
                         PairedAtUtc, LastAccessAtUtc, RevokedAtUtc, RevocationReason, ContractVersion
                  FROM TrustedDevices ORDER BY LastAccessAtUtc DESC;
                  """;

            if (statusFilter.HasValue)
            {
                cmd.Parameters.AddWithValue("$status", statusFilter.Value.ToString());
            }

            var items = new List<TrustedDeviceRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapRecord(reader));
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result> UpsertAsync(TrustedDeviceRecord record, CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(record);
        if (!validation.IsValid)
        {
            return Task.FromResult(Result.Failure(NexusError.Validation(validation)));
        }

        return _failureHandler.ExecuteAsync("TrustedDevices.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO TrustedDevices (
                    DeviceId, CustomName, Platform, DeviceType, OperatingSystemVersion, AppVersion,
                    PublicKeySpkiBase64, PublicKeyFingerprint, Permissions, TrustStatus,
                    PairedAtUtc, LastAccessAtUtc, RevokedAtUtc, RevocationReason, ContractVersion)
                VALUES (
                    $id, $name, $platform, $type, $os, $app,
                    $spki, $fp, $perm, $status,
                    $paired, $last, $revoked, $reason, $ver)
                ON CONFLICT(DeviceId) DO UPDATE SET
                    CustomName = excluded.CustomName,
                    Platform = excluded.Platform,
                    DeviceType = excluded.DeviceType,
                    OperatingSystemVersion = excluded.OperatingSystemVersion,
                    AppVersion = excluded.AppVersion,
                    PublicKeySpkiBase64 = excluded.PublicKeySpkiBase64,
                    PublicKeyFingerprint = excluded.PublicKeyFingerprint,
                    Permissions = excluded.Permissions,
                    TrustStatus = excluded.TrustStatus,
                    LastAccessAtUtc = excluded.LastAccessAtUtc,
                    RevokedAtUtc = excluded.RevokedAtUtc,
                    RevocationReason = excluded.RevocationReason,
                    ContractVersion = excluded.ContractVersion;
                """;
            cmd.Parameters.AddWithValue("$id", record.DeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$name", record.CustomName);
            cmd.Parameters.AddWithValue("$platform", record.Platform.ToString());
            cmd.Parameters.AddWithValue("$type", record.DeviceType.ToString());
            cmd.Parameters.AddWithValue("$os", record.OperatingSystemVersion);
            cmd.Parameters.AddWithValue("$app", record.AppVersion);
            cmd.Parameters.AddWithValue("$spki", record.PublicKeySpkiBase64);
            cmd.Parameters.AddWithValue("$fp", record.PublicKeyFingerprint);
            cmd.Parameters.AddWithValue("$perm", (int)record.Permissions);
            cmd.Parameters.AddWithValue("$status", record.TrustStatus.ToString());
            cmd.Parameters.AddWithValue("$paired", record.PairedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$last", record.LastAccessAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$revoked", record.RevokedAtUtc.HasValue ? record.RevokedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$reason", (object?)record.RevocationReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", record.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> RenameAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("TrustedDevices.Rename", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE TrustedDevices SET CustomName = $name WHERE DeviceId = $id;";
            cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$name", newName.Trim());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> UpdatePermissionsAsync(Guid deviceId, NexusPermission permissions, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("TrustedDevices.UpdatePermissions", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE TrustedDevices SET Permissions = $perm WHERE DeviceId = $id;";
            cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$perm", (int)permissions);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> UpdateLastAccessAsync(Guid deviceId, DateTimeOffset accessedAtUtc, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("TrustedDevices.UpdateLastAccess", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE TrustedDevices SET LastAccessAtUtc = $last WHERE DeviceId = $id;";
            cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$last", accessedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> RevokeAsync(Guid deviceId, DateTimeOffset revokedAtUtc, string? reason, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("TrustedDevices.Revoke", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE TrustedDevices
                SET TrustStatus = $status, RevokedAtUtc = $revoked, RevocationReason = $reason
                WHERE DeviceId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$status", DeviceTrustStatus.Revoked.ToString());
            cmd.Parameters.AddWithValue("$revoked", revokedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<bool>> DeleteAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("TrustedDevices.Delete", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM TrustedDevices WHERE DeviceId = $id;";
            cmd.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken);
    }

    private static TrustedDeviceRecord MapRecord(SqliteDataReader r)
    {
        return new TrustedDeviceRecord
        {
            DeviceId = Guid.Parse(r.GetString(0)),
            CustomName = r.GetString(1),
            Platform = Enum.Parse<DevicePlatform>(r.GetString(2), true),
            DeviceType = Enum.Parse<DeviceType>(r.GetString(3), true),
            OperatingSystemVersion = r.GetString(4),
            AppVersion = r.GetString(5),
            PublicKeySpkiBase64 = r.GetString(6),
            PublicKeyFingerprint = r.GetString(7),
            Permissions = (NexusPermission)r.GetInt32(8),
            TrustStatus = Enum.Parse<DeviceTrustStatus>(r.GetString(9), true),
            PairedAtUtc = DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastAccessAtUtc = DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            RevokedAtUtc = r.IsDBNull(12) ? null : DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            RevocationReason = r.IsDBNull(13) ? null : r.GetString(13),
            ContractVersion = r.GetString(14)
        };
    }
}

public sealed class SqlitePairingSessionRepository : IPairingSessionRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;
    private readonly Dictionary<Guid, string> _hostSpkiCache = [];

    public SqlitePairingSessionRepository(ISqliteConnectionFactory connectionFactory, StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result<StoredPairingSession?>> GetByIdAsync(Guid pairingId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<StoredPairingSession?>("PairingSessions.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT PairingId, HostDeviceId, HostDeviceName, HostPublicKeySpkiBase64, HostPublicKeyFingerprint,
                       CandidateDeviceId, CandidateDeviceName, CandidatePlatform, CandidateDeviceType, CandidateAppVersion,
                       CandidateOperatingSystemVersion, CandidatePublicKeySpkiBase64, CandidatePublicKeyFingerprint,
                       VerificationCode, OneTimeSecretHash, ChallengeNonce, State, QrPayloadJson, FailureReason,
                       IssuedAtUtc, ExpiresAtUtc, CompletedAtUtc, RequestedPermissions, GrantedPermissions,
                       SecretConsumed, TokensFinalized, PendingAccessToken, PendingRefreshToken,
                       PendingAccessExpiresAtUtc, PendingRefreshExpiresAtUtc, ContractVersion
                FROM PairingSessions WHERE PairingId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", pairingId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            var stored = MapSession(reader, out var hostSpki);
            _hostSpkiCache[pairingId] = hostSpki;
            return stored;
        }, cancellationToken);
    }

    public string? GetCachedHostSpki(Guid pairingId)
        => _hostSpkiCache.GetValueOrDefault(pairingId);

    public async Task<Result<string?>> GetHostPublicKeySpkiAsync(Guid pairingId, CancellationToken cancellationToken = default)
    {
        if (_hostSpkiCache.TryGetValue(pairingId, out var cached))
        {
            return Result<string?>.Success(cached);
        }

        return await _failureHandler.ExecuteAsync<string?>("PairingSessions.GetHostSpki", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT HostPublicKeySpkiBase64 FROM PairingSessions WHERE PairingId = $id;";
            cmd.Parameters.AddWithValue("$id", pairingId.ToString("D"));
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (result is not null)
            {
                _hostSpkiCache[pairingId] = result;
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result> UpsertWithHostKeyAsync(StoredPairingSession session, string hostPublicKeySpkiBase64, CancellationToken cancellationToken = default)
    {
        _hostSpkiCache[session.Snapshot.PairingId] = hostPublicKeySpkiBase64;
        return UpsertAsync(session, cancellationToken);
    }

    public Task<Result> UpsertAsync(StoredPairingSession session, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("PairingSessions.Upsert", async ct =>
        {
            var s = session.Snapshot;
            var hostSpki = _hostSpkiCache.GetValueOrDefault(s.PairingId) ?? string.Empty;

            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO PairingSessions (
                    PairingId, HostDeviceId, HostDeviceName, HostPublicKeySpkiBase64, HostPublicKeyFingerprint,
                    CandidateDeviceId, CandidateDeviceName, CandidatePlatform, CandidateDeviceType, CandidateAppVersion,
                    CandidateOperatingSystemVersion, CandidatePublicKeySpkiBase64, CandidatePublicKeyFingerprint,
                    VerificationCode, OneTimeSecretHash, ChallengeNonce, State, QrPayloadJson, FailureReason,
                    IssuedAtUtc, ExpiresAtUtc, CompletedAtUtc, RequestedPermissions, GrantedPermissions,
                    SecretConsumed, TokensFinalized, PendingAccessToken, PendingRefreshToken,
                    PendingAccessExpiresAtUtc, PendingRefreshExpiresAtUtc, ContractVersion)
                VALUES (
                    $pid, $hid, $hname, $hspki, $hfp,
                    $cid, $cname, $cplat, $ctype, $capp,
                    $cos, $cspki, $cfp,
                    $code, $shash, $nonce, $state, $qr, $fail,
                    $issued, $exp, $comp, $rperm, $gperm,
                    $consumed, $finalized, $pat, $prt,
                    $pae, $pre, $ver)
                ON CONFLICT(PairingId) DO UPDATE SET
                    CandidateDeviceId = excluded.CandidateDeviceId,
                    CandidateDeviceName = excluded.CandidateDeviceName,
                    CandidatePlatform = excluded.CandidatePlatform,
                    CandidateDeviceType = excluded.CandidateDeviceType,
                    CandidateAppVersion = excluded.CandidateAppVersion,
                    CandidateOperatingSystemVersion = excluded.CandidateOperatingSystemVersion,
                    CandidatePublicKeySpkiBase64 = excluded.CandidatePublicKeySpkiBase64,
                    CandidatePublicKeyFingerprint = excluded.CandidatePublicKeyFingerprint,
                    VerificationCode = excluded.VerificationCode,
                    State = excluded.State,
                    FailureReason = excluded.FailureReason,
                    CompletedAtUtc = excluded.CompletedAtUtc,
                    RequestedPermissions = excluded.RequestedPermissions,
                    GrantedPermissions = excluded.GrantedPermissions,
                    SecretConsumed = excluded.SecretConsumed,
                    TokensFinalized = excluded.TokensFinalized,
                    PendingAccessToken = excluded.PendingAccessToken,
                    PendingRefreshToken = excluded.PendingRefreshToken,
                    PendingAccessExpiresAtUtc = excluded.PendingAccessExpiresAtUtc,
                    PendingRefreshExpiresAtUtc = excluded.PendingRefreshExpiresAtUtc;
                """;
            cmd.Parameters.AddWithValue("$pid", s.PairingId.ToString("D"));
            cmd.Parameters.AddWithValue("$hid", s.HostDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$hname", s.HostDeviceName);
            cmd.Parameters.AddWithValue("$hspki", hostSpki);
            cmd.Parameters.AddWithValue("$hfp", s.HostPublicKeyFingerprint);
            cmd.Parameters.AddWithValue("$cid", s.CandidateDeviceId.HasValue ? s.CandidateDeviceId.Value.ToString("D") : DBNull.Value);
            cmd.Parameters.AddWithValue("$cname", (object?)s.CandidateDeviceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cplat", s.CandidatePlatform.HasValue ? s.CandidatePlatform.Value.ToString() : DBNull.Value);
            cmd.Parameters.AddWithValue("$ctype", s.CandidateDeviceType.HasValue ? s.CandidateDeviceType.Value.ToString() : DBNull.Value);
            cmd.Parameters.AddWithValue("$capp", (object?)s.CandidateAppVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cos", (object?)session.CandidateOperatingSystemVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cspki", (object?)session.CandidatePublicKeySpkiBase64 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cfp", (object?)s.CandidatePublicKeyFingerprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$code", (object?)s.VerificationCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$shash", session.OneTimeSecretHash);
            cmd.Parameters.AddWithValue("$nonce", session.ChallengeNonce);
            cmd.Parameters.AddWithValue("$state", s.State.ToString());
            cmd.Parameters.AddWithValue("$qr", (object?)s.QrPayloadJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fail", (object?)s.FailureReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$issued", s.IssuedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$exp", s.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$comp", s.CompletedAtUtc.HasValue ? s.CompletedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$rperm", (int)session.RequestedPermissions);
            cmd.Parameters.AddWithValue("$gperm", (int)session.GrantedPermissions);
            cmd.Parameters.AddWithValue("$consumed", session.SecretConsumed ? 1 : 0);
            cmd.Parameters.AddWithValue("$finalized", session.TokensFinalized ? 1 : 0);
            cmd.Parameters.AddWithValue("$pat", (object?)session.PendingAccessToken ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prt", (object?)session.PendingRefreshToken ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pae", session.PendingAccessExpiresAtUtc.HasValue ? session.PendingAccessExpiresAtUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$pre", session.PendingRefreshExpiresAtUtc.HasValue ? session.PendingRefreshExpiresAtUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", s.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<PairingSessionSnapshot>>> ListRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<PairingSessionSnapshot>>("PairingSessions.ListRecent", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT PairingId, HostDeviceId, HostDeviceName, HostPublicKeySpkiBase64, HostPublicKeyFingerprint,
                       CandidateDeviceId, CandidateDeviceName, CandidatePlatform, CandidateDeviceType, CandidateAppVersion,
                       CandidateOperatingSystemVersion, CandidatePublicKeySpkiBase64, CandidatePublicKeyFingerprint,
                       VerificationCode, OneTimeSecretHash, ChallengeNonce, State, QrPayloadJson, FailureReason,
                       IssuedAtUtc, ExpiresAtUtc, CompletedAtUtc, RequestedPermissions, GrantedPermissions,
                       SecretConsumed, TokensFinalized, PendingAccessToken, PendingRefreshToken,
                       PendingAccessExpiresAtUtc, PendingRefreshExpiresAtUtc, ContractVersion
                FROM PairingSessions ORDER BY IssuedAtUtc DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

            var list = new List<PairingSessionSnapshot>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(MapSession(reader, out _).Snapshot);
            }
            return list.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result<bool>> TryConsumeSecretAsync(Guid pairingId, string secretHash, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("PairingSessions.ConsumeSecret", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE PairingSessions
                SET SecretConsumed = 1, State = $validating
                WHERE PairingId = $id
                  AND OneTimeSecretHash = $hash
                  AND SecretConsumed = 0
                  AND State = $awaiting;
                """;
            cmd.Parameters.AddWithValue("$id", pairingId.ToString("D"));
            cmd.Parameters.AddWithValue("$hash", secretHash);
            cmd.Parameters.AddWithValue("$validating", PairingSessionState.Validating.ToString());
            cmd.Parameters.AddWithValue("$awaiting", PairingSessionState.AwaitingScan.ToString());

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, cancellationToken);
    }

    public Task<Result<bool>> TryMarkTokensFinalizedAsync(Guid pairingId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("PairingSessions.FinalizeTokens", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE PairingSessions
                SET TokensFinalized = 1,
                    PendingAccessToken = NULL,
                    PendingRefreshToken = NULL
                WHERE PairingId = $id
                  AND TokensFinalized = 0
                  AND State = $connected;
                """;
            cmd.Parameters.AddWithValue("$id", pairingId.ToString("D"));
            cmd.Parameters.AddWithValue("$connected", PairingSessionState.Connected.ToString());

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, cancellationToken);
    }

    private static StoredPairingSession MapSession(SqliteDataReader r, out string hostSpki)
    {
        hostSpki = r.GetString(3);
        var snapshot = new PairingSessionSnapshot
        {
            PairingId = Guid.Parse(r.GetString(0)),
            HostDeviceId = Guid.Parse(r.GetString(1)),
            HostDeviceName = r.GetString(2),
            HostPublicKeyFingerprint = r.GetString(4),
            CandidateDeviceId = r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)),
            CandidateDeviceName = r.IsDBNull(6) ? null : r.GetString(6),
            CandidatePlatform = r.IsDBNull(7) ? null : Enum.Parse<DevicePlatform>(r.GetString(7), true),
            CandidateDeviceType = r.IsDBNull(8) ? null : Enum.Parse<DeviceType>(r.GetString(8), true),
            CandidateAppVersion = r.IsDBNull(9) ? null : r.GetString(9),
            CandidatePublicKeyFingerprint = r.IsDBNull(12) ? null : r.GetString(12),
            VerificationCode = r.IsDBNull(13) ? null : r.GetString(13),
            State = Enum.Parse<PairingSessionState>(r.GetString(16), true),
            QrPayloadJson = r.IsDBNull(17) ? null : r.GetString(17),
            FailureReason = r.IsDBNull(18) ? null : r.GetString(18),
            IssuedAtUtc = DateTimeOffset.Parse(r.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ExpiresAtUtc = DateTimeOffset.Parse(r.GetString(20), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CompletedAtUtc = r.IsDBNull(21) ? null : DateTimeOffset.Parse(r.GetString(21), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ContractVersion = r.GetString(30)
        };

        return new StoredPairingSession
        {
            Snapshot = snapshot,
            CandidateOperatingSystemVersion = r.IsDBNull(10) ? null : r.GetString(10),
            CandidatePublicKeySpkiBase64 = r.IsDBNull(11) ? null : r.GetString(11),
            OneTimeSecretHash = r.GetString(14),
            ChallengeNonce = r.GetString(15),
            RequestedPermissions = (NexusPermission)r.GetInt32(22),
            GrantedPermissions = (NexusPermission)r.GetInt32(23),
            SecretConsumed = r.GetInt32(24) == 1,
            TokensFinalized = r.GetInt32(25) == 1,
            PendingAccessToken = r.IsDBNull(26) ? null : r.GetString(26),
            PendingRefreshToken = r.IsDBNull(27) ? null : r.GetString(27),
            PendingAccessExpiresAtUtc = r.IsDBNull(28) ? null : DateTimeOffset.Parse(r.GetString(28), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            PendingRefreshExpiresAtUtc = r.IsDBNull(29) ? null : DateTimeOffset.Parse(r.GetString(29), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }
}

public sealed class SqliteRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteRefreshTokenRepository(ISqliteConnectionFactory connectionFactory, StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result> StoreAsync(StoredRefreshTokenRecord record, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("RefreshTokens.Store", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO RefreshTokens (TokenHash, DeviceId, IssuedAtUtc, ExpiresAtUtc, IsRevoked, ReplacedByHash)
                VALUES ($hash, $did, $issued, $exp, $rev, $rep);
                """;
            cmd.Parameters.AddWithValue("$hash", record.TokenHash);
            cmd.Parameters.AddWithValue("$did", record.DeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$issued", record.IssuedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$exp", record.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$rev", record.IsRevoked ? 1 : 0);
            cmd.Parameters.AddWithValue("$rep", (object?)record.ReplacedByHash ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<StoredRefreshTokenRecord?>> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<StoredRefreshTokenRecord?>("RefreshTokens.Get", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT TokenHash, DeviceId, IssuedAtUtc, ExpiresAtUtc, IsRevoked, ReplacedByHash
                FROM RefreshTokens WHERE TokenHash = $hash;
                """;
            cmd.Parameters.AddWithValue("$hash", tokenHash);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new StoredRefreshTokenRecord(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }, cancellationToken);
    }

    public Task<Result> MarkRotatedAsync(string oldTokenHash, string newTokenHash, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("RefreshTokens.MarkRotated", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE RefreshTokens SET IsRevoked = 1, ReplacedByHash = $new WHERE TokenHash = $old;";
            cmd.Parameters.AddWithValue("$old", oldTokenHash);
            cmd.Parameters.AddWithValue("$new", newTokenHash);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> RevokeByDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("RefreshTokens.RevokeByDevice", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE RefreshTokens SET IsRevoked = 1 WHERE DeviceId = $did;";
            cmd.Parameters.AddWithValue("$did", deviceId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}

