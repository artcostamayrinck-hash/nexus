using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Security;

namespace Nexus.Contracts.Pairing;

public enum PairingSessionState
{
    Idle = 0,
    AwaitingScan = 1,
    Validating = 2,
    AwaitingConfirmation = 3,
    Connected = 4,
    Failed = 5,
    Expired = 6,
    Rejected = 7
}

public sealed record PairingQrPayload : IVersionedContract, IValidatableContract
{
    public Guid PairingId { get; init; }
    public Guid HostDeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string HostDeviceName { get; init; } = string.Empty;

    public DevicePlatform HostPlatform { get; init; }
    public DeviceType HostDeviceType { get; init; }

    [Required]
    public string HostAppVersion { get; init; } = string.Empty;

    [Required]
    public string HostPublicKeySpkiBase64 { get; init; } = string.Empty;

    [Required]
    public string HostPublicKeyFingerprint { get; init; } = string.Empty;

    public string? ServerPublicKeySpkiBase64 { get; init; }

    [Required]
    public string ServerEndpoint { get; init; } = string.Empty;

    public string? TlsCertThumbprint { get; init; }

    [Required]
    public string ChallengeNonce { get; init; } = string.Empty;

    [Required]
    public string OneTimeSecret { get; init; } = string.Empty;

    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    [Required]
    public string HostSignatureBase64 { get; init; } = string.Empty;

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (PairingId == Guid.Empty || HostDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(PairingId), "PAIRING_IDS_EMPTY", "PairingId and HostDeviceId are required.");
        }

        if (ExpiresAtUtc <= IssuedAtUtc)
        {
            yield return new ContractValidationError(nameof(ExpiresAtUtc), "PAIRING_EXPIRY_INVALID", "ExpiresAtUtc must be later than IssuedAtUtc.");
        }
    }
}

public sealed record CreatePairingRequest : IVersionedContract, IValidatableContract
{
    public Guid HostDeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string HostDeviceName { get; init; } = string.Empty;

    public DevicePlatform HostPlatform { get; init; }
    public DeviceType HostDeviceType { get; init; }

    [Required]
    public string HostAppVersion { get; init; } = string.Empty;

    [Required]
    public string HostPublicKeySpkiBase64 { get; init; } = string.Empty;

    [Required]
    public string HostPublicKeyFingerprint { get; init; } = string.Empty;

    public string? ServerPublicKeySpkiBase64 { get; init; }

    [Required]
    public string ServerEndpoint { get; init; } = string.Empty;

    public string? TlsCertThumbprint { get; init; }

    [Range(15, 600)]
    public int LifetimeSeconds { get; init; } = 120;

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    [Required]
    public string Nonce { get; init; } = string.Empty;

    [Required]
    public string HostSignatureBase64 { get; init; } = string.Empty;

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (HostDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(HostDeviceId), "HOST_DEVICE_ID_EMPTY", "HostDeviceId cannot be empty.");
        }
    }
}

public sealed record SubmitPairingClaimRequest : IVersionedContract, IValidatableContract
{
    public Guid PairingId { get; init; }

    [Required]
    public string OneTimeSecret { get; init; } = string.Empty;

    [Required]
    public string ChallengeNonce { get; init; } = string.Empty;

    public Guid ClientDeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string ClientDeviceName { get; init; } = string.Empty;

    public DevicePlatform ClientPlatform { get; init; }
    public DeviceType ClientDeviceType { get; init; }

    [Required]
    public string ClientAppVersion { get; init; } = string.Empty;

    [Required]
    public string ClientOperatingSystemVersion { get; init; } = string.Empty;

    [Required]
    public string ClientPublicKeySpkiBase64 { get; init; } = string.Empty;

    [Required]
    public string ClientPublicKeyFingerprint { get; init; } = string.Empty;

    public DateTimeOffset ClientTimestampUtc { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 16)]
    public string ClientNonce { get; init; } = string.Empty;

    [Required]
    public string ClientSignatureBase64 { get; init; } = string.Empty;

    public NexusPermission RequestedPermissions { get; init; } = NexusPermission.ViewStatus | NexusPermission.Notifications | NexusPermission.Clipboard | NexusPermission.FileTransfer;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (PairingId == Guid.Empty || ClientDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(PairingId), "PAIRING_CLAIM_IDS_EMPTY", "PairingId and ClientDeviceId are required.");
        }
    }
}

public sealed record ConfirmPairingRequest : IVersionedContract, IValidatableContract
{
    public Guid PairingId { get; init; }
    public bool Approve { get; init; }
    public NexusPermission GrantedPermissions { get; init; } = NexusPermission.ViewStatus | NexusPermission.Notifications | NexusPermission.Clipboard | NexusPermission.FileTransfer;

    [StringLength(256)]
    public string? RejectionReason { get; init; }

    public Guid HostDeviceId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    [Required]
    public string Nonce { get; init; } = string.Empty;

    [Required]
    public string HostSignatureBase64 { get; init; } = string.Empty;

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (PairingId == Guid.Empty || HostDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(PairingId), "CONFIRM_IDS_EMPTY", "PairingId and HostDeviceId are required.");
        }
    }
}

public sealed record FinalizePairingTokenRequest : IVersionedContract, IValidatableContract
{
    public Guid PairingId { get; init; }
    public Guid ClientDeviceId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    [Required]
    public string Nonce { get; init; } = string.Empty;

    [Required]
    public string ClientSignatureBase64 { get; init; } = string.Empty;

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (PairingId == Guid.Empty || ClientDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(PairingId), "FINALIZE_IDS_EMPTY", "PairingId and ClientDeviceId are required.");
        }
    }
}

public sealed record PairingSessionSnapshot : IVersionedContract
{
    public Guid PairingId { get; init; }
    public Guid HostDeviceId { get; init; }
    public string HostDeviceName { get; init; } = string.Empty;
    public string HostPublicKeyFingerprint { get; init; } = string.Empty;
    public Guid? CandidateDeviceId { get; init; }
    public string? CandidateDeviceName { get; init; }
    public DevicePlatform? CandidatePlatform { get; init; }
    public DeviceType? CandidateDeviceType { get; init; }
    public string? CandidateAppVersion { get; init; }
    public string? CandidatePublicKeyFingerprint { get; init; }
    public string? VerificationCode { get; init; }
    public PairingSessionState State { get; init; } = PairingSessionState.Idle;
    public string? QrPayloadJson { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}
