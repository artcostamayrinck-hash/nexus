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
    public const string TransferStarted = "nexus.transfer.started";
    public const string TransferCompleted = "nexus.transfer.completed";
    public const string TransferFailed = "nexus.transfer.failed";
    public const string TransferCancelled = "nexus.transfer.cancelled";
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
    public const string ControlExecuted = "nexus.module.control_executed";
    public const string MobileNotificationForwarded = "nexus.module.notification_forwarded";
    public const string MonitorTelemetryUpdated = "nexus.module.monitor_updated";
    public const string MediaStateChanged = "nexus.module.media_changed";
    public const string ModuleStateChanged = "nexus.module.state_changed";
    public const string PanelProfileChanged = "nexus.panel.profile_changed";
    public const string AutomationTriggered = "nexus.automate.triggered";
    public const string AutomationFailed = "nexus.automate.failed";
    public const string UpdateCheckCompleted = "nexus.update.check_completed";
    public const string UpdatePackageVerified = "nexus.update.package_verified";
    public const string UpdateInstallPrompted = "nexus.update.install_prompted";
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

