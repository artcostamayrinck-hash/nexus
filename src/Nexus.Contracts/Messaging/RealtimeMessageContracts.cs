using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Security;

namespace Nexus.Contracts.Messaging;

public enum RealtimeMessageKind
{
    Command = 0,
    Event = 1,
    CommandResult = 2,
    CommandCancellation = 3,
    PresenceNotification = 4,
    Heartbeat = 5,
    Custom = 6
}

public enum RealtimeAckStatus
{
    Received = 0,
    Processed = 1,
    DuplicateIgnored = 2,
    OutOfOrderBuffered = 3,
    Rejected = 4,
    Cancelled = 5
}

/// <summary>
/// Standardized envelope for all real-time SignalR messages exchanged between Windows, Android, and NEXUS Server.
/// Carries unique ID, origin/target device identity, UTC timestamp, contract version, correlation ID,
/// sequence metadata, and validated payload.
/// </summary>
public sealed record RealtimeMessageEnvelope : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid MessageId { get; init; } = Guid.CreateVersion7();

    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string MessageType { get; init; } = string.Empty;

    public RealtimeMessageKind Kind { get; init; } = RealtimeMessageKind.Event;
    public Guid SourceDeviceId { get; init; }
    public Guid? TargetDeviceId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    [Required]
    [StringLength(65536, MinimumLength = 2)]
    public string PayloadJson { get; init; } = "{}";

    public Guid CorrelationId { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Optional monotonic sequence number per (SourceDeviceId, StreamKey).
    /// When > 0, receivers buffer out-of-order messages and deliver them strictly in sequence.
    /// </summary>
    public long SequenceNumber { get; init; }

    [StringLength(64)]
    public string StreamKey { get; init; } = "default";

    public bool RequiresAck { get; init; } = true;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (MessageId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(MessageId), "MESSAGE_ID_EMPTY", "MessageId cannot be empty.");
        }

        if (SourceDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SourceDeviceId), "SOURCE_DEVICE_EMPTY", "SourceDeviceId is required.");
        }

        if (TargetDeviceId.HasValue && TargetDeviceId.Value == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(TargetDeviceId), "TARGET_DEVICE_EMPTY", "TargetDeviceId cannot be an empty GUID (use null for broadcast/server).");
        }

        if (CorrelationId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CorrelationId), "CORRELATION_ID_EMPTY", "CorrelationId cannot be empty.");
        }

        if (SequenceNumber < 0)
        {
            yield return new ContractValidationError(nameof(SequenceNumber), "SEQUENCE_NEGATIVE", "SequenceNumber cannot be negative.");
        }

        if (TimestampUtc == default)
        {
            yield return new ContractValidationError(nameof(TimestampUtc), "TIMESTAMP_MISSING", "TimestampUtc is required.");
        }
    }
}

public sealed record RealtimeMessageAck : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid AckId { get; init; } = Guid.CreateVersion7();
    public Guid MessageId { get; init; }
    public Guid CorrelationId { get; init; } = Guid.CreateVersion7();
    public Guid RespondingDeviceId { get; init; }
    public Guid? OriginalSenderDeviceId { get; init; }
    public RealtimeAckStatus Status { get; init; } = RealtimeAckStatus.Received;
    public long? ExpectedNextSequence { get; init; }

    [StringLength(512)]
    public string? Detail { get; init; }

    public DateTimeOffset AcknowledgedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (AckId == Guid.Empty || MessageId == Guid.Empty || RespondingDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(MessageId), "ACK_IDS_EMPTY", "AckId, MessageId, and RespondingDeviceId are required.");
        }
    }
}

public sealed record CommandCancellationRequest : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid CommandId { get; init; }
    public Guid CorrelationId { get; init; } = Guid.CreateVersion7();
    public Guid SourceDeviceId { get; init; }
    public Guid TargetDeviceId { get; init; }

    [StringLength(256)]
    public string? Reason { get; init; }

    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (CommandId == Guid.Empty || SourceDeviceId == Guid.Empty || TargetDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CommandId), "CANCEL_IDS_EMPTY", "CommandId, SourceDeviceId, and TargetDeviceId are required.");
        }
    }
}

/// <summary>
/// Live presence view of a paired/trusted device.
/// IsRealtimeConnected is true ONLY while an active authenticated SignalR session exists.
/// </summary>
public sealed record DevicePresenceSnapshot : IVersionedContract
{
    public Guid DeviceId { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public DevicePlatform Platform { get; init; } = DevicePlatform.Unknown;
    public DeviceType Type { get; init; } = DeviceType.Unknown;
    public DeviceTrustStatus TrustStatus { get; init; } = DeviceTrustStatus.Pending;
    public bool IsRealtimeConnected { get; init; }
    public ConnectionState ConnectionState { get; init; } = ConnectionState.Disconnected;
    public string? ActiveConnectionId { get; init; }
    public DateTimeOffset? ConnectedAtUtc { get; init; }
    public DateTimeOffset? LastSeenAtUtc { get; init; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; init; }
    public string? RouteSummary { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record RealtimeSessionSnapshot : IVersionedContract
{
    public Guid SessionId { get; init; } = Guid.CreateVersion7();
    public string ConnectionId { get; init; } = string.Empty;
    public Guid DeviceId { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public ConnectionState State { get; init; } = ConnectionState.Disconnected;
    public string? Endpoint { get; init; }
    public DateTimeOffset ConnectedAtUtc { get; init; }
    public DateTimeOffset LastActivityAtUtc { get; init; }
    public long MessagesSent { get; init; }
    public long MessagesReceived { get; init; }
    public long DuplicatesDropped { get; init; }
    public long OutOfOrderBuffered { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record BulkPayloadTransferReceipt(
    Guid TransferId,
    Guid SourceDeviceId,
    Guid? TargetDeviceId,
    string ContentType,
    long ByteLength,
    string Sha256Hex,
    DateTimeOffset ReceivedAtUtc,
    string ContractVersion = ContractVersions.Current) : IVersionedContract;
