using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Events;

public enum EventSeverity
{
    Trace = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}

public static class NexusEventTypes
{
    public const string DeviceRegistered = "nexus.device.registered";
    public const string DeviceStatusChanged = "nexus.device.status_changed";
    public const string ConnectionStateChanged = "nexus.connection.state_changed";
    public const string CommandUpdated = "nexus.command.updated";
    public const string NotificationReceived = "nexus.notification.received";
    public const string TransferProgressUpdated = "nexus.transfer.progress_updated";
    public const string SyncCompleted = "nexus.sync.completed";
    public const string SystemHealthChanged = "nexus.system.health_changed";
    public const string PairingStateChanged = "nexus.pairing.state_changed";
    public const string DeviceTrustAuthorized = "nexus.trust.authorized";
    public const string DeviceTrustRevoked = "nexus.trust.revoked";
    public const string DeviceRenamed = "nexus.device.renamed";
    public const string RealtimeConnected = "nexus.realtime.connected";
    public const string RealtimeDisconnected = "nexus.realtime.disconnected";
    public const string PresenceUpdated = "nexus.presence.updated";
    public const string MessageAcknowledged = "nexus.message.acknowledged";
    public const string CommandCancelled = "nexus.command.cancelled";
    public const string ClipboardSynced = "nexus.clipboard.synced";
    public const string NoteUpdated = "nexus.note.updated";
    public const string SyncConflictDetected = "nexus.sync.conflict_detected";
    public const string SyncQueueFlushed = "nexus.sync.queue_flushed";
}

public sealed record IntegrationEvent : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    [Required]
    [StringLength(128, MinimumLength = 3)]
    public string EventType { get; init; } = string.Empty;

    public Guid? SourceDeviceId { get; init; }
    public EventSeverity Severity { get; init; } = EventSeverity.Information;
    public string? PayloadJson { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (EventId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(EventId), "EVENT_ID_EMPTY", "EventId cannot be empty.");
        }

        if (CorrelationId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CorrelationId), "CORRELATION_ID_EMPTY", "CorrelationId cannot be empty.");
        }
    }
}

