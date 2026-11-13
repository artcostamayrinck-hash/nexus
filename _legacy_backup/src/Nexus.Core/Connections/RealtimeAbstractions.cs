using Nexus.Contracts.Automation;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Sync;
using Nexus.Contracts.Transfers;
using Nexus.Core.Errors;

namespace Nexus.Core.Connections;

public sealed record RealtimeCredentialBundle(
    Guid DeviceId,
    string DeviceName,
    string AccessToken,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    string ServerEndpoint,
    string? TlsThumbprint);

/// <summary>
/// Provides authenticated device identity, bearer token, endpoint, and token-refresh capabilities
/// for real-time SignalR and HTTP bulk channels.
/// </summary>
public interface IRealtimeCredentialProvider
{
    Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default);
    Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default);
}

public sealed record RealtimeIngestionDecision(
    RealtimeAckStatus Status,
    IReadOnlyList<RealtimeMessageEnvelope> ReadyMessages,
    long? ExpectedNextSequence = null,
    string? DiagnosticDetail = null);

/// <summary>
/// Thread-safe per-sender deduplication and out-of-order sequence reordering pipeline.
/// </summary>
public interface IMessageOrderingPipeline
{
    long DuplicatesDroppedCount { get; }
    long OutOfOrderReorderedCount { get; }

    RealtimeIngestionDecision ProcessIncoming(RealtimeMessageEnvelope envelope);
    long GetNextOutboundSequence(Guid sourceDeviceId, string streamKey = "default");
    void ResetStream(Guid sourceDeviceId, string? streamKey = null);
}

public sealed class RealtimeClientOptions
{
    public TimeSpan DefaultAckTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    public bool AutoReconnect { get; set; } = true;
    public Func<string, string?, HttpMessageHandler>? SignalRMessageHandlerFactory { get; set; }
}

/// <summary>
/// Authenticated real-time client channel supporting bidirectional SignalR commands,
/// events, ACKs, presence tracking, cancellation, and automatic reconnection.
/// </summary>
public interface IRealtimeClientChannel : ITransportChannel, IHeartbeatService, IAsyncDisposable
{
    ConnectionState State { get; }
    RealtimeSessionSnapshot? CurrentSession { get; }
    IReadOnlyList<DevicePresenceSnapshot> LivePresence { get; }
    IReadOnlyList<RealtimeMessageEnvelope> RecentMessages { get; }
    IReadOnlyList<RealtimeMessageAck> RecentAcknowledgements { get; }
    string? LastError { get; }

    event EventHandler<ConnectionStateSnapshot>? StateChanged;
    event EventHandler<RealtimeMessageEnvelope>? MessageReceived;
    event EventHandler<RealtimeMessageAck>? AckReceived;
    event EventHandler<DeviceCommand>? CommandReceived;
    event EventHandler<CommandCancellationRequest>? CommandCancelled;
    event EventHandler<CommandExecutionResult>? CommandResultReceived;
    event EventHandler<IReadOnlyList<DevicePresenceSnapshot>>? PresenceUpdated;
    event EventHandler<ClipboardEntryContract>? ClipboardReceived;
    event EventHandler<NexusNoteContract>? NoteChangeReceived;
    event EventHandler<FileTransferProgressEvent>? TransferProgressReceived;
    event EventHandler<FileTransferRecord>? TransferAvailableReceived;
    event EventHandler<MobileNotificationRecord>? MobileNotificationReceived;
    event EventHandler<MediaPlaybackStateContract>? MediaStateReceived;
    event EventHandler<IntegrationModuleStatusSnapshot>? ModuleStatusChangedReceived;
    event EventHandler<PanelProfile>? PanelProfileUpdatedReceived;
    event EventHandler<AutomationExecutionRecord>? AutomationExecutionReceived;

    Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(
        string? endpointOverride = null,
        CancellationToken cancellationToken = default);

    Task<Result<RealtimeMessageAck>> SendEnvelopeAsync(
        RealtimeMessageEnvelope envelope,
        bool waitForAck = true,
        TimeSpan? ackTimeout = null,
        CancellationToken cancellationToken = default);

    Task<Result<RealtimeMessageAck>> SendCommandAsync(
        DeviceCommand command,
        bool waitForAck = true,
        CancellationToken cancellationToken = default);

    Task<Result> CancelCommandAsync(
        Guid commandId,
        Guid targetDeviceId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<Result> SendCommandResultAsync(
        CommandExecutionResult result,
        Guid? targetDeviceId = null,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<Result> AcknowledgeAsync(
        RealtimeMessageAck ack,
        CancellationToken cancellationToken = default);

    Task<Result<RealtimeMessageAck>> PublishIntegrationEventAsync(
        IntegrationEvent integrationEvent,
        Guid? targetDeviceId = null,
        bool ordered = true,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dedicated HTTP channel for larger payload transfers outside real-time SignalR control frames.
/// </summary>
public interface IHttpBulkTransferChannel
{
    Task<Result<BulkPayloadTransferReceipt>> UploadLargePayloadAsync(
        ReadOnlyMemory<byte> payload,
        string contentType = "application/octet-stream",
        Guid? targetDeviceId = null,
        string? endpointOverride = null,
        CancellationToken cancellationToken = default);
}
