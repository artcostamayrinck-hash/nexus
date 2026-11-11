using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Devices;

public sealed record DeviceInfo : IVersionedContract, IValidatableContract
{
    public Guid Id { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    public DevicePlatform Platform { get; init; }
    public DeviceType Type { get; init; }

    [Required]
    [StringLength(120)]
    public string OperatingSystemVersion { get; init; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string AppVersion { get; init; } = string.Empty;

    [StringLength(256)]
    public string? HardwareFingerprint { get; init; }

    public DeviceCapability Capabilities { get; init; } = DeviceCapability.None;
    public DeviceStatus Status { get; init; } = DeviceStatus.Offline;
    public DateTimeOffset RegisteredAtUtc { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (Id == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(Id), "DEVICE_ID_EMPTY", "Device Id cannot be empty.");
        }

        if (Platform == DevicePlatform.Unknown)
        {
            yield return new ContractValidationError(nameof(Platform), "DEVICE_PLATFORM_UNKNOWN", "Device platform must be specified.");
        }

        if (LastSeenAtUtc != default && RegisteredAtUtc != default && LastSeenAtUtc < RegisteredAtUtc)
        {
            yield return new ContractValidationError(nameof(LastSeenAtUtc), "DEVICE_TIMELINE_INVALID", "LastSeenAtUtc cannot precede RegisteredAtUtc.");
        }
    }
}

public sealed record DeviceRegistrationContract : IVersionedContract, IValidatableContract
{
    public Guid DeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    public DevicePlatform Platform { get; init; }
    public DeviceType Type { get; init; }

    [Required]
    [StringLength(120)]
    public string OperatingSystemVersion { get; init; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string AppVersion { get; init; } = string.Empty;

    [StringLength(256)]
    public string? HardwareFingerprint { get; init; }

    public DeviceCapability Capabilities { get; init; } = DeviceCapability.Telemetry;
    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (DeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(DeviceId), "DEVICE_ID_EMPTY", "DeviceId cannot be empty.");
        }

        if (Platform == DevicePlatform.Unknown)
        {
            yield return new ContractValidationError(nameof(Platform), "DEVICE_PLATFORM_UNKNOWN", "Device platform must be specified.");
        }
    }
}

