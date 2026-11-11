using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;
using Nexus.Contracts.Devices;

namespace Nexus.Contracts.Security;

[Flags]
public enum NexusPermission
{
    None = 0,
    ViewStatus = 1 << 0,
    Notifications = 1 << 1,
    Clipboard = 1 << 2,
    FileTransfer = 1 << 3,
    RemoteCommands = 1 << 4,
    ManageDevices = 1 << 5,
    Notes = 1 << 6,
    All = ViewStatus | Notifications | Clipboard | FileTransfer | RemoteCommands | ManageDevices | Notes
}

public enum DeviceTrustStatus
{
    Pending = 0,
    Authorized = 1,
    Revoked = 2
}

public sealed record DeviceCryptographicIdentity : IVersionedContract, IValidatableContract
{
    public Guid DeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string CustomName { get; init; } = string.Empty;

    public DevicePlatform Platform { get; init; }
    public DeviceType DeviceType { get; init; }

    [Required]
    [StringLength(40)]
    public string AppVersion { get; init; } = string.Empty;

    [Required]
    [StringLength(120)]
    public string OperatingSystemVersion { get; init; } = string.Empty;

    [Required]
    public string PublicKeySpkiBase64 { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    public string PublicKeyFingerprint { get; init; } = string.Empty;

    public DateTimeOffset RegisteredAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (DeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(DeviceId), "DEVICE_ID_EMPTY", "DeviceId cannot be empty.");
        }

        if (Platform == DevicePlatform.Unknown)
        {
            yield return new ContractValidationError(nameof(Platform), "PLATFORM_UNKNOWN", "Platform must be specified.");
        }
    }
}

public sealed record TrustedDeviceRecord : IVersionedContract, IValidatableContract
{
    public Guid DeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string CustomName { get; init; } = string.Empty;

    public DevicePlatform Platform { get; init; }
    public DeviceType DeviceType { get; init; }

    [Required]
    [StringLength(120)]
    public string OperatingSystemVersion { get; init; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string AppVersion { get; init; } = string.Empty;

    [Required]
    public string PublicKeySpkiBase64 { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    public string PublicKeyFingerprint { get; init; } = string.Empty;

    public NexusPermission Permissions { get; init; } = NexusPermission.ViewStatus;
    public DeviceTrustStatus TrustStatus { get; init; } = DeviceTrustStatus.Authorized;
    public DateTimeOffset PairedAtUtc { get; init; }
    public DateTimeOffset LastAccessAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }

    [StringLength(256)]
    public string? RevocationReason { get; init; }

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (DeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(DeviceId), "DEVICE_ID_EMPTY", "DeviceId cannot be empty.");
        }

        if (TrustStatus == DeviceTrustStatus.Revoked && RevokedAtUtc is null)
        {
            yield return new ContractValidationError(nameof(RevokedAtUtc), "REVOKED_TIMESTAMP_MISSING", "Revoked device must specify RevokedAtUtc.");
        }
    }
}

public sealed record DeviceAuthTokenPair : IVersionedContract
{
    public Guid DeviceId { get; init; }
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAtUtc { get; init; }
    public DateTimeOffset RefreshTokenExpiresAtUtc { get; init; }
    public NexusPermission Permissions { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record RefreshTokenRequest : IVersionedContract, IValidatableContract
{
    public Guid DeviceId { get; init; }

    [Required]
    public string RefreshToken { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 16)]
    public string Nonce { get; init; } = string.Empty;

    public DateTimeOffset TimestampUtc { get; init; }

    [Required]
    public string SignatureBase64 { get; init; } = string.Empty;

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (DeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(DeviceId), "DEVICE_ID_EMPTY", "DeviceId cannot be empty.");
        }
    }
}

public sealed record RenameDeviceRequest : IVersionedContract, IValidatableContract
{
    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string CustomName { get; init; } = string.Empty;

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (string.IsNullOrWhiteSpace(CustomName) || CustomName.Trim().Length < 2)
        {
            yield return new ContractValidationError(nameof(CustomName), "NAME_TOO_SHORT", "CustomName must have at least 2 non-whitespace characters.");
        }
    }
}

public sealed record UpdateDevicePermissionsRequest : IVersionedContract
{
    public NexusPermission Permissions { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record RevokeDeviceRequest : IVersionedContract
{
    [StringLength(256)]
    public string? Reason { get; init; }

    public string ContractVersion { get; init; } = ContractVersions.Current;
}

