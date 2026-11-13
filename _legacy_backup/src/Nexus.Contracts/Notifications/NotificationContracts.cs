using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Notifications;

public enum NotificationUrgency
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

public sealed record NotificationAction(
    [property: Required, StringLength(64, MinimumLength = 1)] string ActionId,
    [property: Required, StringLength(80, MinimumLength = 1)] string Label,
    string? CommandName = null);

public sealed record DeviceNotification : IVersionedContract, IValidatableContract
{
    public Guid NotificationId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid? TargetDeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string AppName { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Title { get; init; } = string.Empty;

    [StringLength(2000)]
    public string Body { get; init; } = string.Empty;

    public NotificationUrgency Urgency { get; init; } = NotificationUrgency.Normal;
    public bool IsDismissed { get; init; }
    public IReadOnlyList<NotificationAction> Actions { get; init; } = Array.Empty<NotificationAction>();
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (NotificationId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(NotificationId), "NOTIFICATION_ID_EMPTY", "NotificationId cannot be empty.");
        }

        if (SourceDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SourceDeviceId), "SOURCE_DEVICE_ID_EMPTY", "SourceDeviceId cannot be empty.");
        }

        if (ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= CreatedAtUtc)
        {
            yield return new ContractValidationError(nameof(ExpiresAtUtc), "NOTIFICATION_EXPIRY_INVALID", "ExpiresAtUtc must be later than CreatedAtUtc.");
        }
    }
}

