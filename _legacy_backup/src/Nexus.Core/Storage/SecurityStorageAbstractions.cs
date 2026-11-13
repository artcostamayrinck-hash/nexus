using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;

namespace Nexus.Core.Storage;

public sealed record StoredPairingSession
{
    public required PairingSessionSnapshot Snapshot { get; init; }
    public required string OneTimeSecretHash { get; init; }
    public required string ChallengeNonce { get; init; }
    public string? CandidatePublicKeySpkiBase64 { get; init; }
    public string? CandidateOperatingSystemVersion { get; init; }
    public NexusPermission RequestedPermissions { get; init; }
    public NexusPermission GrantedPermissions { get; init; }
    public bool SecretConsumed { get; init; }
    public bool TokensFinalized { get; init; }
    public string? PendingAccessToken { get; init; }
    public string? PendingRefreshToken { get; init; }
    public DateTimeOffset? PendingAccessExpiresAtUtc { get; init; }
    public DateTimeOffset? PendingRefreshExpiresAtUtc { get; init; }
}

public sealed record StoredRefreshTokenRecord(
    string TokenHash,
    Guid DeviceId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsRevoked,
    string? ReplacedByHash);

public interface ITrustedDeviceRepository
{
    Task<Result<TrustedDeviceRecord?>> GetByIdAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAsync(CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAuthorizedAsync(CancellationToken cancellationToken = default);
    Task<Result> UpsertAsync(TrustedDeviceRecord record, CancellationToken cancellationToken = default);
    Task<Result> RenameAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default);
    Task<Result> UpdatePermissionsAsync(Guid deviceId, NexusPermission permissions, CancellationToken cancellationToken = default);
    Task<Result> UpdateLastAccessAsync(Guid deviceId, DateTimeOffset accessedAtUtc, CancellationToken cancellationToken = default);
    Task<Result> RevokeAsync(Guid deviceId, DateTimeOffset revokedAtUtc, string? reason, CancellationToken cancellationToken = default);
    Task<Result<bool>> DeleteAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

public interface IPairingSessionRepository
{
    Task<Result<StoredPairingSession?>> GetByIdAsync(Guid pairingId, CancellationToken cancellationToken = default);
    Task<Result> UpsertAsync(StoredPairingSession session, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<PairingSessionSnapshot>>> ListRecentAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task<Result<bool>> TryConsumeSecretAsync(Guid pairingId, string secretHash, CancellationToken cancellationToken = default);
    Task<Result<bool>> TryMarkTokensFinalizedAsync(Guid pairingId, CancellationToken cancellationToken = default);
}

public interface IRefreshTokenRepository
{
    Task<Result> StoreAsync(StoredRefreshTokenRecord record, CancellationToken cancellationToken = default);
    Task<Result<StoredRefreshTokenRecord?>> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);
    Task<Result> MarkRotatedAsync(string oldTokenHash, string newTokenHash, CancellationToken cancellationToken = default);
    Task<Result> RevokeByDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

