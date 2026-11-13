using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Identity;

public enum SessionStatus
{
    Pending = 0,
    Active = 1,
    Expired = 2,
    Revoked = 3
}

public sealed record UserProfile : IVersionedContract, IValidatableContract
{
    public Guid UserId { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string Username { get; init; } = string.Empty;

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    [EmailAddress]
    [StringLength(254)]
    public string? Email { get; init; }

    public bool IsActive { get; init; } = true;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (UserId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(UserId), "USER_ID_EMPTY", "UserId cannot be empty.");
        }
    }
}

public sealed record SessionInfo : IVersionedContract, IValidatableContract
{
    public Guid SessionId { get; init; }
    public Guid UserId { get; init; }
    public Guid DeviceId { get; init; }
    public SessionStatus Status { get; init; } = SessionStatus.Pending;
    public DateTimeOffset IssuedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    [StringLength(128)]
    public string? ClientAddress { get; init; }

    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (SessionId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SessionId), "SESSION_ID_EMPTY", "SessionId cannot be empty.");
        }

        if (UserId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(UserId), "USER_ID_EMPTY", "UserId cannot be empty.");
        }

        if (DeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(DeviceId), "DEVICE_ID_EMPTY", "DeviceId cannot be empty.");
        }

        if (ExpiresAtUtc <= IssuedAtUtc)
        {
            yield return new ContractValidationError(nameof(ExpiresAtUtc), "SESSION_EXPIRY_INVALID", "ExpiresAtUtc must be later than IssuedAtUtc.");
        }
    }
}

