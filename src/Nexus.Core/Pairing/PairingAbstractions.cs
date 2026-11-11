using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;

namespace Nexus.Core.Pairing;

public interface IPairingService
{
    Task<Result<PairingSessionSnapshot>> CreateSessionAsync(CreatePairingRequest request, CancellationToken cancellationToken = default);
    Task<Result<PairingSessionSnapshot>> GetSessionAsync(Guid pairingId, CancellationToken cancellationToken = default);
    Task<Result<PairingSessionSnapshot>> SubmitClaimAsync(SubmitPairingClaimRequest claim, CancellationToken cancellationToken = default);
    Task<Result<PairingSessionSnapshot>> ConfirmAsync(ConfirmPairingRequest confirm, CancellationToken cancellationToken = default);
    Task<Result<DeviceAuthTokenPair>> FinalizeTokensAsync(FinalizePairingTokenRequest request, CancellationToken cancellationToken = default);
}

public interface ITrustedDeviceService
{
    Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAuthorizedAsync(CancellationToken cancellationToken = default);
    Task<Result<TrustedDeviceRecord>> GetByIdAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<Result<TrustedDeviceRecord>> RenameAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default);
    Task<Result<TrustedDeviceRecord>> UpdatePermissionsAsync(Guid deviceId, NexusPermission permissions, CancellationToken cancellationToken = default);
    Task<Result> RevokeAsync(Guid deviceId, string? reason = null, CancellationToken cancellationToken = default);
    Task<Result<bool>> RemoveAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<Result<DeviceAuthTokenPair>> RefreshTokensAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);
}

