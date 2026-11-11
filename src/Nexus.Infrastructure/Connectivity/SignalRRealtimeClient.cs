using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Sync;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Security.Tls;

namespace Nexus.Infrastructure.Connectivity;

/// <summary>
/// Authenticated SignalR real-time client supporting ordered/deduplicated messaging,
/// delivery acknowledgements, bidirectional commands & cancellations, live device presence,
/// token refresh handling, and resilient automatic reconnection.
/// </summary>
public sealed class SignalRRealtimeClient : IRealtimeClientChannel
{
    private sealed class NexusReconnectRetryPolicy : IRetryPolicy
    {
        private readonly RealtimeClientOptions _options;

        public NexusReconnectRetryPolicy(RealtimeClientOptions options)
        {
            _options = options;
        }

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (!_options.AutoReconnect)
            {
                return null;
            }

            var exponent = Math.Min(retryContext.PreviousRetryCount, 6);
            var delayMs = _options.ReconnectBaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
            return TimeSpan.FromMilliseconds(Math.Min(delayMs, _options.ReconnectMaxDelay.TotalMilliseconds));
        }
    }

    private const int MaxHistoryItems = 100;

    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly IConnectionManager _connectionManager;
    private readonly IMessageOrderingPipeline _orderingPipeline;
    private readonly IEventBus _eventBus;
    private readonly IJsonSerializer _serializer;
    private readonly IClock _clock;
    private readonly RealtimeClientOptions _options;
    private readonly ILogger<SignalRRealtimeClient> _logger;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _stateSync = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RealtimeMessageAck>> _pendingAcks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeIncomingCommands = new();
    private readonly List<RealtimeMessageEnvelope> _recentMessages = new();
    private readonly List<RealtimeMessageAck> _recentAcks = new();

    private HubConnection? _hubConnection;
    private RealtimeCredentialBundle? _activeCredentials;
    private RealtimeSessionSnapshot? _currentSession;
    private IReadOnlyList<DevicePresenceSnapshot> _livePresence = Array.Empty<DevicePresenceSnapshot>();
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _lastError;
    private DateTimeOffset? _lastHeartbeatUtc;
    private bool _intentionalClose;
    private bool _fatalAuthOrRevocation;
    private bool _hasSentInCurrentSession;
    private bool _disposed;

    public SignalRRealtimeClient(
        IRealtimeCredentialProvider credentialProvider,
        IConnectionManager connectionManager,
        IMessageOrderingPipeline orderingPipeline,
        IEventBus eventBus,
        IJsonSerializer serializer,
        IClock clock,
        RealtimeClientOptions options,
        ILogger<SignalRRealtimeClient> logger)
    {
        _credentialProvider = credentialProvider;
        _connectionManager = connectionManager;
        _orderingPipeline = orderingPipeline;
        _eventBus = eventBus;
        _serializer = serializer;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public TransportKind Transport => TransportKind.WebSocket;
    public bool IsConnected => State == ConnectionState.Connected && _hubConnection?.State == HubConnectionState.Connected;
    public DateTimeOffset? LastHeartbeatUtc => _lastHeartbeatUtc;

    public ConnectionState State
    {
        get
        {
            lock (_stateSync)
            {
                return _state;
            }
        }
    }

    public RealtimeSessionSnapshot? CurrentSession
    {
        get
        {
            lock (_stateSync)
            {
                return _currentSession;
            }
        }
    }

    public IReadOnlyList<DevicePresenceSnapshot> LivePresence
    {
        get
        {
            lock (_stateSync)
            {
                return _livePresence;
            }
        }
    }

    public IReadOnlyList<RealtimeMessageEnvelope> RecentMessages
    {
        get
        {
            lock (_stateSync)
            {
                return _recentMessages.ToArray();
            }
        }
    }

    public IReadOnlyList<RealtimeMessageAck> RecentAcknowledgements
    {
        get
        {
            lock (_stateSync)
            {
                return _recentAcks.ToArray();
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateSync)
            {
                return _lastError;
            }
        }
    }

    public event EventHandler<ConnectionStateSnapshot>? StateChanged;
    public event EventHandler<RealtimeMessageEnvelope>? MessageReceived;
    public event EventHandler<RealtimeMessageAck>? AckReceived;
    public event EventHandler<DeviceCommand>? CommandReceived;
    public event EventHandler<CommandCancellationRequest>? CommandCancelled;
    public event EventHandler<CommandExecutionResult>? CommandResultReceived;
    public event EventHandler<IReadOnlyList<DevicePresenceSnapshot>>? PresenceUpdated;
    public event EventHandler<ClipboardEntryContract>? ClipboardReceived;
    public event EventHandler<NexusNoteContract>? NoteChangeReceived;
    public event EventHandler<FileTransferProgressEvent>? TransferProgressReceived;
    public event EventHandler<FileTransferRecord>? TransferAvailableReceived;
    public event EventHandler<MobileNotificationRecord>? MobileNotificationReceived;
    public event EventHandler<MediaPlaybackStateContract>? MediaStateReceived;
    public event EventHandler<IntegrationModuleStatusSnapshot>? ModuleStatusChangedReceived;
    public event EventHandler<PanelProfile>? PanelProfileUpdatedReceived;
    public event EventHandler<AutomationExecutionRecord>? AutomationExecutionReceived;

    public async Task<Result> OpenAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var opened = await OpenAuthenticatedAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return opened.IsSuccess ? Result.Success() : Result.Failure(opened.Error!);
    }

    public async Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(
        string? endpointOverride = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected && string.IsNullOrWhiteSpace(endpointOverride) && CurrentSession is not null)
            {
                return Result<RealtimeSessionSnapshot>.Success(CurrentSession);
            }

            _intentionalClose = false;
            _fatalAuthOrRevocation = false;
            ClearError();

            await DisposeHubConnectionLockedAsync().ConfigureAwait(false);

            await SetStateAsync(ConnectionState.Connecting, endpointOverride, null, null, "Iniciando conexão em tempo real...", cancellationToken)
                .ConfigureAwait(false);

            var credsResult = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (credsResult.IsFailure)
            {
                await FailToFaultedAsync(endpointOverride, credsResult.Error!.Message, cancellationToken).ConfigureAwait(false);
                return Result<RealtimeSessionSnapshot>.Failure(credsResult.Error!);
            }

            var creds = credsResult.Value;
            var targetEndpoint = string.IsNullOrWhiteSpace(endpointOverride)
                ? creds.ServerEndpoint
                : endpointOverride.Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(targetEndpoint))
            {
                var err = NexusError.Validation("Endpoint do servidor para SignalR não informado.", code: "ENDPOINT_EMPTY");
                await FailToFaultedAsync(null, err.Message, cancellationToken).ConfigureAwait(false);
                return Result<RealtimeSessionSnapshot>.Failure(err);
            }

            await SetStateAsync(ConnectionState.Authenticating, targetEndpoint, creds.DeviceId, null, "Autenticando dispositivo...", cancellationToken)
                .ConfigureAwait(false);

            if (creds.AccessTokenExpiresAtUtc.HasValue && creds.AccessTokenExpiresAtUtc.Value <= _clock.UtcNow.AddSeconds(15))
            {
                var refreshed = await _credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
                if (refreshed.IsFailure)
                {
                    await FailToFaultedAsync(targetEndpoint, $"Autenticação expirada: {refreshed.Error!.Message}", cancellationToken)
                        .ConfigureAwait(false);
                    return Result<RealtimeSessionSnapshot>.Failure(refreshed.Error!);
                }

                creds = refreshed.Value;
            }

            creds = creds with { ServerEndpoint = targetEndpoint };
            lock (_stateSync)
            {
                _activeCredentials = creds;
            }

            var startOutcome = await StartHubWithAuthRetryAsync(creds, cancellationToken).ConfigureAwait(false);
            if (startOutcome.IsFailure)
            {
                await FailToFaultedAsync(targetEndpoint, startOutcome.Error!.Message, cancellationToken).ConfigureAwait(false);
                return Result<RealtimeSessionSnapshot>.Failure(startOutcome.Error!);
            }

            var hub = startOutcome.Value;
            var now = _clock.UtcNow;

            try
            {
                await hub.InvokeAsync("AnnouncePresenceAsync", creds.DeviceId, creds.DeviceName, cancellationToken)
                    .ConfigureAwait(false);

                if (!_hasSentInCurrentSession)
                {
                    await hub.InvokeAsync("ResetSessionStreamAsync", creds.DeviceId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Hub handshake post-connect call failed for device {DeviceId}.", creds.DeviceId);
                await FailToFaultedAsync(targetEndpoint, $"Falha ao registrar sessão no hub: {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
                await SafeDisposeHubAsync(hub).ConfigureAwait(false);
                return Result<RealtimeSessionSnapshot>.Failure(NexusError.Unauthorized(ex.Message, code: "HUB_HANDSHAKE_FAILED"));
            }

            var session = new RealtimeSessionSnapshot
            {
                SessionId = Guid.CreateVersion7(),
                ConnectionId = hub.ConnectionId ?? string.Empty,
                DeviceId = creds.DeviceId,
                DeviceName = creds.DeviceName,
                Endpoint = targetEndpoint,
                State = ConnectionState.Connected,
                ConnectedAtUtc = now,
                LastActivityAtUtc = now
            };

            lock (_stateSync)
            {
                _hubConnection = hub;
                _currentSession = session;
            }

            await SetStateAsync(ConnectionState.Connected, targetEndpoint, creds.DeviceId, null, "Conectado em tempo real via SignalR.", cancellationToken)
                .ConfigureAwait(false);

            await RefreshPresenceInternalAsync(hub, cancellationToken).ConfigureAwait(false);
            await PublishDiagnosticSafeAsync(
                $"Sessão SignalR autenticada para {creds.DeviceName} ({creds.DeviceId}).",
                isError: false,
                cancellationToken).ConfigureAwait(false);

            return Result<RealtimeSessionSnapshot>.Success(session);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<Result> CloseAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _intentionalClose = true;
            CancelPendingWaiters("Conexão encerrada.");

            var deviceId = _activeCredentials?.DeviceId ?? Guid.Empty;
            if (deviceId != Guid.Empty)
            {
                _orderingPipeline.ResetStream(deviceId);
            }

            _hasSentInCurrentSession = false;

            await DisposeHubConnectionLockedAsync().ConfigureAwait(false);

            lock (_stateSync)
            {
                _currentSession = null;
                _livePresence = _livePresence
                    .Select(p => p with { IsRealtimeConnected = false, ConnectionState = ConnectionState.Disconnected, RouteSummary = "Offline" })
                    .ToList()
                    .AsReadOnly();
            }

            await SetStateAsync(ConnectionState.Disconnected, null, null, null, "Desconectado manualmente.", cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<Result<RealtimeMessageAck>> SendEnvelopeAsync(
        RealtimeMessageEnvelope envelope,
        bool waitForAck = true,
        TimeSpan? ackTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var hub = GetActiveHubOrFail(out var hubError);
        if (hub is null)
        {
            return Result<RealtimeMessageAck>.Failure(hubError!);
        }

        var creds = GetActiveCredentialsSnapshot();
        var effectiveSource = envelope.SourceDeviceId == Guid.Empty && creds is not null
            ? creds.DeviceId
            : envelope.SourceDeviceId;

        var effectiveStream = string.IsNullOrWhiteSpace(envelope.StreamKey) ? "default" : envelope.StreamKey;
        var effectiveSeq = envelope.SequenceNumber > 0
            ? envelope.SequenceNumber
            : _orderingPipeline.GetNextOutboundSequence(effectiveSource, effectiveStream);

        var normalized = envelope with
        {
            MessageId = envelope.MessageId == Guid.Empty ? Guid.CreateVersion7() : envelope.MessageId,
            CorrelationId = envelope.CorrelationId == Guid.Empty ? Guid.CreateVersion7() : envelope.CorrelationId,
            SourceDeviceId = effectiveSource,
            SequenceNumber = effectiveSeq,
            StreamKey = effectiveStream,
            MessageType = string.IsNullOrWhiteSpace(envelope.MessageType) ? "nexus.message" : envelope.MessageType.Trim(),
            TimestampUtc = envelope.TimestampUtc == default ? _clock.UtcNow : envelope.TimestampUtc
        };

        var validation = ContractValidator.Validate(normalized);
        if (!validation.IsValid)
        {
            return Result<RealtimeMessageAck>.Failure(NexusError.Validation(validation));
        }

        TaskCompletionSource<RealtimeMessageAck>? ackWaiter = null;
        var shouldAwaitRecipientAck = waitForAck && normalized.RequiresAck && normalized.TargetDeviceId.HasValue;
        if (shouldAwaitRecipientAck)
        {
            ackWaiter = new TaskCompletionSource<RealtimeMessageAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[normalized.MessageId] = ackWaiter;
        }

        try
        {
            var hubAck = await hub.InvokeAsync<RealtimeMessageAck>("SendRealtimeMessageAsync", normalized, cancellationToken)
                .ConfigureAwait(false);
            _hasSentInCurrentSession = true;
            UpdateSessionCounters(sentDelta: 1);

            if (hubAck.Status == RealtimeAckStatus.Rejected)
            {
                _pendingAcks.TryRemove(normalized.MessageId, out _);
                RecordAck(hubAck);
                var code = hubAck.Detail?.Contains("TARGET_DEVICE_OFFLINE", StringComparison.Ordinal) == true
                    ? "TARGET_DEVICE_OFFLINE"
                    : "MESSAGE_REJECTED";
                return Result<RealtimeMessageAck>.Failure(new NexusError(code, hubAck.Detail ?? "Mensagem rejeitada.", ErrorCategory.Conflict));
            }

            if (!shouldAwaitRecipientAck ||
                hubAck.Status is RealtimeAckStatus.DuplicateIgnored or RealtimeAckStatus.OutOfOrderBuffered or RealtimeAckStatus.Processed)
            {
                _pendingAcks.TryRemove(normalized.MessageId, out _);
                RecordAck(hubAck);
                return Result<RealtimeMessageAck>.Success(hubAck);
            }

            var effectiveTimeout = ackTimeout ?? _options.DefaultAckTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            var completed = await ackWaiter!.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return Result<RealtimeMessageAck>.Success(completed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<RealtimeMessageAck>.Failure(
                new NexusError("ACK_TIMEOUT", $"Tempo esgotado aguardando confirmação da mensagem {normalized.MessageId}.", ErrorCategory.Timeout));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<RealtimeMessageAck>.Failure(
                NexusError.Network($"Falha ao enviar mensagem pelo SignalR: {ex.Message}", exception: ex));
        }
        finally
        {
            _pendingAcks.TryRemove(normalized.MessageId, out _);
        }
    }

    public async Task<Result<RealtimeMessageAck>> SendCommandAsync(
        DeviceCommand command,
        bool waitForAck = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var hub = GetActiveHubOrFail(out var hubError);
        if (hub is null)
        {
            return Result<RealtimeMessageAck>.Failure(hubError!);
        }

        var creds = GetActiveCredentialsSnapshot();
        var effectiveCommand = command.SourceDeviceId == Guid.Empty && creds is not null
            ? command with
            {
                CommandId = command.CommandId == Guid.Empty ? Guid.CreateVersion7() : command.CommandId,
                SourceDeviceId = creds.DeviceId,
                IssuedAtUtc = command.IssuedAtUtc == default ? _clock.UtcNow : command.IssuedAtUtc
            }
            : command;

        var validation = ContractValidator.Validate(effectiveCommand);
        if (!validation.IsValid)
        {
            return Result<RealtimeMessageAck>.Failure(NexusError.Validation(validation));
        }

        TaskCompletionSource<RealtimeMessageAck>? ackWaiter = null;
        if (waitForAck)
        {
            ackWaiter = new TaskCompletionSource<RealtimeMessageAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[effectiveCommand.CommandId] = ackWaiter;
        }

        try
        {
            var hubAck = await hub.InvokeAsync<RealtimeMessageAck>("SendCommandAsync", effectiveCommand, cancellationToken)
                .ConfigureAwait(false);
            UpdateSessionCounters(sentDelta: 1);

            if (hubAck.Status == RealtimeAckStatus.Rejected)
            {
                _pendingAcks.TryRemove(effectiveCommand.CommandId, out _);
                RecordAck(hubAck);
                var code = hubAck.Detail?.Contains("TARGET_DEVICE_OFFLINE", StringComparison.Ordinal) == true
                    ? "TARGET_DEVICE_OFFLINE"
                    : "COMMAND_REJECTED";
                return Result<RealtimeMessageAck>.Failure(new NexusError(code, hubAck.Detail ?? "Comando rejeitado.", ErrorCategory.Conflict));
            }

            if (!waitForAck || ackWaiter is null)
            {
                RecordAck(hubAck);
                return Result<RealtimeMessageAck>.Success(hubAck);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.DefaultAckTimeout);

            var recipientAck = await ackWaiter.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return Result<RealtimeMessageAck>.Success(recipientAck);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<RealtimeMessageAck>.Failure(
                new NexusError("ACK_TIMEOUT", $"Tempo esgotado aguardando confirmação do comando '{effectiveCommand.CommandName}'.", ErrorCategory.Timeout));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<RealtimeMessageAck>.Failure(
                NexusError.Network($"Falha ao enviar comando pelo SignalR: {ex.Message}", exception: ex));
        }
        finally
        {
            _pendingAcks.TryRemove(effectiveCommand.CommandId, out _);
        }
    }

    public async Task<Result> CancelCommandAsync(
        Guid commandId,
        Guid targetDeviceId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var hub = GetActiveHubOrFail(out var hubError);
        if (hub is null)
        {
            return Result.Failure(hubError!);
        }

        var creds = GetActiveCredentialsSnapshot();
        var request = new CommandCancellationRequest
        {
            CommandId = commandId,
            CorrelationId = Guid.CreateVersion7(),
            SourceDeviceId = creds?.DeviceId ?? Guid.Empty,
            TargetDeviceId = targetDeviceId,
            Reason = reason,
            RequestedAtUtc = _clock.UtcNow
        };

        var validation = ContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Result.Failure(NexusError.Validation(validation));
        }

        try
        {
            var ack = await hub.InvokeAsync<RealtimeMessageAck>("CancelCommandAsync", request, cancellationToken)
                .ConfigureAwait(false);
            RecordAck(ack);

            if (ack.Status == RealtimeAckStatus.Rejected)
            {
                return Result.Failure(new NexusError("CANCEL_REJECTED", ack.Detail ?? "Cancelamento rejeitado.", ErrorCategory.Conflict));
            }

            await PublishDiagnosticSafeAsync(
                $"Cancelamento enviado para comando {commandId:D}.",
                isError: false,
                cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure(NexusError.Network($"Falha ao cancelar comando em tempo real: {ex.Message}", exception: ex));
        }
    }

    public async Task<Result> SendCommandResultAsync(
        CommandExecutionResult result,
        Guid? targetDeviceId = null,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var hub = GetActiveHubOrFail(out var hubError);
        if (hub is null)
        {
            return Result.Failure(hubError!);
        }

        try
        {
            var effectiveTarget = targetDeviceId ?? Guid.Empty;
            var effectiveCorr = correlationId ?? Guid.CreateVersion7();
            await hub.InvokeAsync("SendCommandResultAsync", result, effectiveTarget, effectiveCorr, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure(NexusError.Network($"Falha ao enviar resultado de comando: {ex.Message}", exception: ex));
        }
    }

    public async Task<Result> AcknowledgeAsync(
        RealtimeMessageAck ack,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ack);

        var hub = GetActiveHubOrFail(out var hubError);
        if (hub is null)
        {
            return Result.Failure(hubError!);
        }

        try
        {
            await hub.InvokeAsync("AcknowledgeMessageAsync", ack, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure(NexusError.Network($"Falha ao enviar ACK em tempo real: {ex.Message}", exception: ex));
        }
    }

    public async Task<Result<RealtimeMessageAck>> PublishIntegrationEventAsync(
        IntegrationEvent integrationEvent,
        Guid? targetDeviceId = null,
        bool ordered = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var creds = GetActiveCredentialsSnapshot();
        var sourceId = integrationEvent.SourceDeviceId is { } explicitSource && explicitSource != Guid.Empty
            ? explicitSource
            : creds?.DeviceId ?? Guid.Empty;

        var seq = ordered && sourceId != Guid.Empty
            ? _orderingPipeline.GetNextOutboundSequence(sourceId, "events")
            : 1;

        var envelope = new RealtimeMessageEnvelope
        {
            MessageId = integrationEvent.EventId == Guid.Empty ? Guid.CreateVersion7() : integrationEvent.EventId,
            CorrelationId = integrationEvent.CorrelationId == Guid.Empty ? Guid.CreateVersion7() : integrationEvent.CorrelationId,
            Kind = RealtimeMessageKind.Event,
            MessageType = integrationEvent.EventType,
            SourceDeviceId = sourceId,
            TargetDeviceId = targetDeviceId,
            SequenceNumber = seq,
            StreamKey = ordered ? "events" : $"unordered-{Guid.CreateVersion7():N}",
            PayloadJson = string.IsNullOrWhiteSpace(integrationEvent.PayloadJson) ? "{}" : integrationEvent.PayloadJson,
            RequiresAck = targetDeviceId.HasValue,
            TimestampUtc = integrationEvent.OccurredAtUtc == default ? _clock.UtcNow : integrationEvent.OccurredAtUtc,
            ContractVersion = integrationEvent.ContractVersion
        };

        return await SendEnvelopeAsync(
            envelope,
            waitForAck: targetDeviceId.HasValue,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        var result = await PublishIntegrationEventAsync(integrationEvent, null, ordered: true, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!);
    }

    public async Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(
        NexusRequest<TRequest> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var envelope = new RealtimeMessageEnvelope
        {
            MessageId = request.RequestId == Guid.Empty ? Guid.CreateVersion7() : request.RequestId,
            CorrelationId = request.CorrelationId,
            Kind = RealtimeMessageKind.Command,
            MessageType = string.IsNullOrWhiteSpace(request.Action) ? typeof(TRequest).Name : request.Action,
            SourceDeviceId = request.SourceDeviceId,
            TargetDeviceId = request.TargetDeviceId,
            PayloadJson = _serializer.Serialize(request.Payload),
            RequiresAck = request.TargetDeviceId.HasValue,
            TimestampUtc = request.CreatedAtUtc == default ? _clock.UtcNow : request.CreatedAtUtc,
            ContractVersion = request.ContractVersion
        };

        var ackResult = await SendEnvelopeAsync(
            envelope,
            waitForAck: request.TargetDeviceId.HasValue,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (ackResult.IsFailure)
        {
            return Result<NexusResponse<TResponse>>.Failure(ackResult.Error!);
        }

        if (typeof(TResponse) == typeof(RealtimeMessageAck))
        {
            var typed = (TResponse)(object)ackResult.Value;
            return Result<NexusResponse<TResponse>>.Success(
                NexusResponse<TResponse>.Success(typed, request.RequestId, request.CorrelationId));
        }

        return Result<NexusResponse<TResponse>>.Success(
            NexusResponse<TResponse>.Success(default!, request.RequestId, request.CorrelationId));
    }

    public async Task<Result<int>> SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var hub = GetActiveHubOrFail(out var hubError);
        if (hub is null)
        {
            return Result<int>.Failure(hubError!);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            await hub.InvokeAsync("PingAsync", cancellationToken).ConfigureAwait(false);
            sw.Stop();
            _lastHeartbeatUtc = _clock.UtcNow;

            var latencyMs = Math.Max(0, (int)Math.Round(sw.Elapsed.TotalMilliseconds));
            await _connectionManager.TransitionAsync(
                ConnectionState.Connected,
                TransportKind.WebSocket,
                _activeCredentials?.ServerEndpoint,
                _activeCredentials?.DeviceId,
                latencyMs,
                "Heartbeat SignalR confirmado.",
                cancellationToken).ConfigureAwait(false);

            return Result<int>.Success(latencyMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<int>.Failure(NexusError.Network($"Heartbeat SignalR falhou: {ex.Message}", exception: ex));
        }
    }

    public async Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(CancellationToken cancellationToken = default)
    {
        var hub = GetActiveHubOrFail(out var hubError);
        if (hub is null)
        {
            return Result<IReadOnlyList<DevicePresenceSnapshot>>.Failure(hubError!);
        }

        return await RefreshPresenceInternalAsync(hub, cancellationToken).ConfigureAwait(false);
    }

    public CancellationToken TrackIncomingCommand(Guid commandId, TimeSpan? timeout = null)
    {
        var cts = new CancellationTokenSource();
        if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout.Value);
        }

        _activeIncomingCommands[commandId] = cts;
        return cts.Token;
    }

    public void CompleteIncomingCommand(Guid commandId)
    {
        if (_activeIncomingCommands.TryRemove(commandId, out var cts))
        {
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _intentionalClose = true;
        CancelPendingWaiters("Cliente SignalR descartado.");
        await DisposeHubConnectionLockedAsync().ConfigureAwait(false);
        _connectLock.Dispose();
    }

    private async Task<Result<HubConnection>> StartHubWithAuthRetryAsync(
        RealtimeCredentialBundle creds,
        CancellationToken cancellationToken)
    {
        var attemptCreds = creds;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var hub = BuildHubConnection(attemptCreds);
            try
            {
                await hub.StartAsync(cancellationToken).ConfigureAwait(false);
                return Result<HubConnection>.Success(hub);
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await SafeDisposeHubAsync(hub).ConfigureAwait(false);

                if (attempt == 0)
                {
                    var refreshed = await _credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
                    if (refreshed.IsSuccess &&
                        !string.IsNullOrWhiteSpace(refreshed.Value.AccessToken) &&
                        !string.Equals(refreshed.Value.AccessToken, attemptCreds.AccessToken, StringComparison.Ordinal))
                    {
                        attemptCreds = refreshed.Value with { ServerEndpoint = creds.ServerEndpoint };
                        lock (_stateSync)
                        {
                            _activeCredentials = attemptCreds;
                        }
                        continue;
                    }
                }

                _fatalAuthOrRevocation = true;
                return Result<HubConnection>.Failure(
                    NexusError.Unauthorized(
                        "Autenticação expirada ou dispositivo revogado pelo servidor.",
                        code: httpEx.StatusCode == HttpStatusCode.Forbidden ? "DEVICE_REVOKED" : "REALTIME_AUTH_EXPIRED"));
            }
            catch (OperationCanceledException)
            {
                await SafeDisposeHubAsync(hub).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await SafeDisposeHubAsync(hub).ConfigureAwait(false);
                return Result<HubConnection>.Failure(
                    NexusError.Network($"Falha ao conectar ao hub SignalR em '{attemptCreds.ServerEndpoint}': {ex.Message}", exception: ex));
            }
        }

        return Result<HubConnection>.Failure(NexusError.Unauthorized("Falha de autenticação SignalR.", code: "REALTIME_AUTH_EXPIRED"));
    }

    private HubConnection BuildHubConnection(RealtimeCredentialBundle creds)
    {
        var hubUrl = new Uri($"{creds.ServerEndpoint.TrimEnd('/')}/hubs/nexus", UriKind.Absolute);

        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, httpOptions =>
            {
                httpOptions.AccessTokenProvider = async () =>
                {
                    var current = GetActiveCredentialsSnapshot();
                    if (current?.AccessTokenExpiresAtUtc is { } exp && exp <= _clock.UtcNow.AddSeconds(15))
                    {
                        var refreshed = await _credentialProvider.RefreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
                        if (refreshed.IsSuccess)
                        {
                            current = refreshed.Value with { ServerEndpoint = creds.ServerEndpoint };
                            lock (_stateSync)
                            {
                                _activeCredentials = current;
                            }
                        }
                    }

                    return current?.AccessToken;
                };

                if (_options.SignalRMessageHandlerFactory is not null)
                {
                    httpOptions.HttpMessageHandlerFactory = _ =>
                        _options.SignalRMessageHandlerFactory(creds.ServerEndpoint, creds.TlsThumbprint);
                }
                else if (hubUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(creds.TlsThumbprint))
                {
                    httpOptions.HttpMessageHandlerFactory = _ =>
                        LocalTlsCertificateManager.CreatePinnedTlsHandler(creds.TlsThumbprint);
                }
            });

        if (_options.AutoReconnect)
        {
            builder.WithAutomaticReconnect(new NexusReconnectRetryPolicy(_options));
        }

        var hub = builder.Build();
        RegisterHubHandlers(hub);
        return hub;
    }

    private void RegisterHubHandlers(HubConnection hub)
    {
        hub.On<RealtimeMessageEnvelope>("ReceiveRealtimeMessage", envelope =>
            _ = HandleIncomingEnvelopeAsync(hub, envelope));

        hub.On<RealtimeMessageAck>("ReceiveMessageAck", ack =>
        {
            RecordAck(ack);
            if (_pendingAcks.TryRemove(ack.MessageId, out var waiter))
            {
                waiter.TrySetResult(ack);
            }
            AckReceived?.Invoke(this, ack);
        });

        hub.On<DeviceCommand>("ReceiveDeviceCommand", command =>
            _ = HandleIncomingCommandAsync(hub, command));

        hub.On<CommandCancellationRequest>("ReceiveCommandCancellation", cancellation =>
        {
            if (_activeIncomingCommands.TryRemove(cancellation.CommandId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                finally
                {
                    cts.Dispose();
                }
            }

            CommandCancelled?.Invoke(this, cancellation);
        });

        hub.On<CommandExecutionResult>("ReceiveCommandResult", result =>
        {
            CommandResultReceived?.Invoke(this, result);
        });

        hub.On<IReadOnlyList<DevicePresenceSnapshot>>("ReceivePresenceSnapshot", presenceList =>
        {
            UpdatePresence(presenceList);
        });

        hub.On<RealtimeSessionSnapshot>("ReceiveSessionNotice", session =>
        {
            lock (_stateSync)
            {
                _currentSession = session;
            }

            if (session.State == ConnectionState.Disconnected && !_intentionalClose)
            {
                _ = HandleForcedDisconnectNoticeAsync(session);
            }
        });

        hub.On<Guid, string>("ReceiveRevocationNotice", (deviceId, reason) =>
        {
            var local = GetActiveCredentialsSnapshot();
            if (local is not null && (deviceId == Guid.Empty || deviceId == local.DeviceId))
            {
                _fatalAuthOrRevocation = true;
                _ = FailAndStopAfterRevocationAsync(hub, reason);
            }
        });

        hub.On<ClipboardEntryContract>("ReceiveClipboardEntry", entry =>
        {
            if (entry is not null)
            {
                ClipboardReceived?.Invoke(this, entry);
            }
        });

        hub.On<NexusNoteContract>("ReceiveNoteUpdated", note =>
        {
            if (note is not null)
            {
                NoteChangeReceived?.Invoke(this, note);
            }
        });

        hub.On<FileTransferProgressEvent>("ReceiveTransferProgress", progress =>
        {
            if (progress is not null)
            {
                TransferProgressReceived?.Invoke(this, progress);
            }
        });

        hub.On<FileTransferRecord>("ReceiveTransferAvailable", transfer =>
        {
            if (transfer is not null)
            {
                TransferAvailableReceived?.Invoke(this, transfer);
            }
        });

        hub.On<MobileNotificationRecord>("ReceiveMobileNotification", notification =>
        {
            if (notification is not null)
            {
                MobileNotificationReceived?.Invoke(this, notification);
            }
        });

        hub.On<MediaPlaybackStateContract>("ReceiveMediaState", mediaState =>
        {
            if (mediaState is not null)
            {
                MediaStateReceived?.Invoke(this, mediaState);
            }
        });

        hub.On<IntegrationModuleStatusSnapshot>("ReceiveModuleStatusChanged", status =>
        {
            if (status is not null)
            {
                ModuleStatusChangedReceived?.Invoke(this, status);
            }
        });

        hub.On<PanelProfile>("ReceivePanelProfileUpdated", profile =>
        {
            if (profile is not null)
            {
                PanelProfileUpdatedReceived?.Invoke(this, profile);
            }
        });

        hub.On<AutomationExecutionRecord>("ReceiveAutomationExecution", execution =>
        {
            if (execution is not null)
            {
                AutomationExecutionReceived?.Invoke(this, execution);
            }
        });

        hub.On<DateTimeOffset>("ReceivePong", serverTimeUtc =>
        {
            _lastHeartbeatUtc = serverTimeUtc;
        });

        hub.On<IntegrationEvent>("ReceiveIntegrationEvent", integrationEvent =>
        {
            _ = _eventBus.PublishAsync(
                new DiagnosticDomainEvent("SignalR", $"Evento '{integrationEvent.EventType}' recebido.", IsError: false),
                CancellationToken.None);
        });

        hub.Reconnecting += error =>
        {
            if (_intentionalClose || _fatalAuthOrRevocation)
            {
                return Task.CompletedTask;
            }

            var endpoint = _activeCredentials?.ServerEndpoint;
            var reason = error is null ? "Conexão instável; reconectando..." : $"Reconectando após falha: {error.Message}";
            _logger.LogWarning(error, "SignalR connection entering Reconnecting state.");

            lock (_stateSync)
            {
                if (_currentSession is not null)
                {
                    _currentSession = _currentSession with { State = ConnectionState.Reconnecting };
                }
            }

            return SetStateAsync(ConnectionState.Reconnecting, endpoint, _activeCredentials?.DeviceId, null, reason, CancellationToken.None);
        };

        hub.Reconnected += async connectionId =>
        {
            if (_intentionalClose || _fatalAuthOrRevocation)
            {
                return;
            }

            var creds = GetActiveCredentialsSnapshot();
            if (creds is null)
            {
                return;
            }

            try
            {
                await hub.InvokeAsync("AnnouncePresenceAsync", creds.DeviceId, creds.DeviceName, CancellationToken.None)
                    .ConfigureAwait(false);

                lock (_stateSync)
                {
                    _currentSession = (_currentSession ?? new RealtimeSessionSnapshot
                    {
                        SessionId = Guid.CreateVersion7(),
                        ConnectionId = connectionId ?? string.Empty,
                        DeviceId = creds.DeviceId,
                        DeviceName = creds.DeviceName,
                        Endpoint = creds.ServerEndpoint,
                        State = ConnectionState.Connected,
                        ConnectedAtUtc = _clock.UtcNow,
                        LastActivityAtUtc = _clock.UtcNow
                    }) with
                    {
                        ConnectionId = connectionId ?? string.Empty,
                        State = ConnectionState.Connected,
                        LastActivityAtUtc = _clock.UtcNow
                    };
                }

                await SetStateAsync(
                    ConnectionState.Connected,
                    creds.ServerEndpoint,
                    creds.DeviceId,
                    null,
                    "Conexão em tempo real restabelecida.",
                    CancellationToken.None).ConfigureAwait(false);

                await RefreshPresenceInternalAsync(hub, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-reconnect AnnouncePresenceAsync failed.");
                await FailToFaultedAsync(creds.ServerEndpoint, $"Falha após reconexão: {ex.Message}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        };

        hub.Closed += async error =>
        {
            if (_intentionalClose)
            {
                return;
            }

            var endpoint = _activeCredentials?.ServerEndpoint;
            var isAuthError = _fatalAuthOrRevocation ||
                              error is HttpRequestException httpEx &&
                              httpEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

            if (isAuthError)
            {
                await FailToFaultedAsync(endpoint, "Sessão encerrada por autenticação expirada ou revogação.", CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            if (error is not null)
            {
                await FailToFaultedAsync(endpoint, $"Conexão perdida: {error.Message}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await SetStateAsync(ConnectionState.Disconnected, endpoint, null, null, "Sessão encerrada pelo servidor.", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        };
    }

    private async Task HandleIncomingEnvelopeAsync(HubConnection hub, RealtimeMessageEnvelope envelope)
    {
        try
        {
            var decision = _orderingPipeline.ProcessIncoming(envelope);
            foreach (var ready in decision.ReadyMessages)
            {
                RecordMessage(ready);
                UpdateSessionCounters(receivedDelta: 1);
                MessageReceived?.Invoke(this, ready);

                if (ready.Kind == RealtimeMessageKind.Event)
                {
                    await _eventBus.PublishAsync(
                        new DiagnosticDomainEvent("SignalR", $"Evento '{ready.MessageType}' recebido de {ready.SourceDeviceId}.", IsError: false),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (envelope.RequiresAck)
            {
                var creds = GetActiveCredentialsSnapshot();
                if (creds is not null && hub.State == HubConnectionState.Connected)
                {
                    var ackStatus = decision.Status switch
                    {
                        RealtimeAckStatus.DuplicateIgnored => RealtimeAckStatus.DuplicateIgnored,
                        RealtimeAckStatus.OutOfOrderBuffered => RealtimeAckStatus.OutOfOrderBuffered,
                        RealtimeAckStatus.Rejected => RealtimeAckStatus.Rejected,
                        _ => RealtimeAckStatus.Processed
                    };

                    var ack = new RealtimeMessageAck
                    {
                        MessageId = envelope.MessageId,
                        CorrelationId = envelope.CorrelationId,
                        RespondingDeviceId = creds.DeviceId,
                        OriginalSenderDeviceId = envelope.SourceDeviceId,
                        Status = ackStatus,
                        ExpectedNextSequence = decision.ExpectedNextSequence,
                        Detail = decision.DiagnosticDetail ?? "Processado pelo dispositivo destino.",
                        AcknowledgedAtUtc = _clock.UtcNow
                    };

                    await hub.InvokeAsync("AcknowledgeMessageAsync", ack, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing incoming realtime envelope {MessageId}.", envelope.MessageId);
        }
    }

    private async Task HandleIncomingCommandAsync(HubConnection hub, DeviceCommand command)
    {
        try
        {
            UpdateSessionCounters(receivedDelta: 1);
            CommandReceived?.Invoke(this, command);

            var creds = GetActiveCredentialsSnapshot();
            if (creds is not null && hub.State == HubConnectionState.Connected)
            {
                var ack = new RealtimeMessageAck
                {
                    MessageId = command.CommandId,
                    CorrelationId = command.CorrelationId,
                    RespondingDeviceId = creds.DeviceId,
                    OriginalSenderDeviceId = command.SourceDeviceId,
                    Status = RealtimeAckStatus.Processed,
                    Detail = $"Comando '{command.CommandName}' recebido no destino.",
                    AcknowledgedAtUtc = _clock.UtcNow
                };

                await hub.InvokeAsync("AcknowledgeMessageAsync", ack, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error handling incoming device command {CommandId}.", command.CommandId);
        }
    }

    private async Task HandleForcedDisconnectNoticeAsync(RealtimeSessionSnapshot session)
    {
        await SetStateAsync(
            ConnectionState.Disconnected,
            _activeCredentials?.ServerEndpoint,
            session.DeviceId,
            null,
            "Sessão desconectada remotamente pelo servidor.",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FailAndStopAfterRevocationAsync(HubConnection hub, string reason)
    {
        await FailToFaultedAsync(_activeCredentials?.ServerEndpoint, $"Dispositivo revogado: {reason}", CancellationToken.None)
            .ConfigureAwait(false);
        await SafeDisposeHubAsync(hub).ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceInternalAsync(
        HubConnection hub,
        CancellationToken cancellationToken)
    {
        try
        {
            var list = await hub.InvokeAsync<IReadOnlyList<DevicePresenceSnapshot>>("GetPresenceSnapshotAsync", cancellationToken)
                .ConfigureAwait(false);
            UpdatePresence(list);
            return Result<IReadOnlyList<DevicePresenceSnapshot>>.Success(list);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<IReadOnlyList<DevicePresenceSnapshot>>.Failure(
                NexusError.Network($"Falha ao consultar presença: {ex.Message}", exception: ex));
        }
    }

    private void UpdatePresence(IReadOnlyList<DevicePresenceSnapshot> list)
    {
        var copy = list.ToList().AsReadOnly();
        lock (_stateSync)
        {
            _livePresence = copy;
        }

        PresenceUpdated?.Invoke(this, copy);
    }

    private async Task SetStateAsync(
        ConnectionState targetState,
        string? endpoint,
        Guid? remoteDeviceId,
        int? latencyMs,
        string reason,
        CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            _state = targetState;
            if (_currentSession is not null)
            {
                _currentSession = _currentSession with
                {
                    State = targetState,
                    LastActivityAtUtc = _clock.UtcNow
                };
            }
        }

        var result = await _connectionManager.TransitionAsync(
            targetState,
            TransportKind.WebSocket,
            endpoint ?? _activeCredentials?.ServerEndpoint,
            remoteDeviceId ?? _activeCredentials?.DeviceId,
            latencyMs,
            reason,
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            StateChanged?.Invoke(this, result.Value);
        }
    }

    private async Task FailToFaultedAsync(string? endpoint, string errorMessage, CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            _lastError = errorMessage;
        }

        await SetStateAsync(ConnectionState.Faulted, endpoint, _activeCredentials?.DeviceId, null, errorMessage, cancellationToken)
            .ConfigureAwait(false);
    }

    private HubConnection? GetActiveHubOrFail(out NexusError? error)
    {
        HubConnection? hub;
        lock (_stateSync)
        {
            hub = _hubConnection;
        }

        if (hub is null || hub.State != HubConnectionState.Connected)
        {
            error = NexusError.Network("Cliente SignalR não está conectado.");
            return null;
        }

        error = null;
        return hub;
    }

    private RealtimeCredentialBundle? GetActiveCredentialsSnapshot()
    {
        lock (_stateSync)
        {
            return _activeCredentials;
        }
    }

    private void UpdateSessionCounters(long sentDelta = 0, long receivedDelta = 0)
    {
        lock (_stateSync)
        {
            if (_currentSession is not null)
            {
                _currentSession = _currentSession with
                {
                    LastActivityAtUtc = _clock.UtcNow,
                    MessagesSent = _currentSession.MessagesSent + sentDelta,
                    MessagesReceived = _currentSession.MessagesReceived + receivedDelta,
                    DuplicatesDropped = _orderingPipeline.DuplicatesDroppedCount,
                    OutOfOrderBuffered = _orderingPipeline.OutOfOrderReorderedCount
                };
            }
        }
    }

    private void RecordMessage(RealtimeMessageEnvelope envelope)
    {
        lock (_stateSync)
        {
            _recentMessages.Insert(0, envelope);
            if (_recentMessages.Count > MaxHistoryItems)
            {
                _recentMessages.RemoveRange(MaxHistoryItems, _recentMessages.Count - MaxHistoryItems);
            }
        }
    }

    private void RecordAck(RealtimeMessageAck ack)
    {
        lock (_stateSync)
        {
            _recentAcks.Insert(0, ack);
            if (_recentAcks.Count > MaxHistoryItems)
            {
                _recentAcks.RemoveRange(MaxHistoryItems, _recentAcks.Count - MaxHistoryItems);
            }
        }
    }

    private void ClearError()
    {
        lock (_stateSync)
        {
            _lastError = null;
        }
    }

    private void CancelPendingWaiters(string reason)
    {
        foreach (var pair in _pendingAcks)
        {
            if (_pendingAcks.TryRemove(pair.Key, out var tcs))
            {
                tcs.TrySetException(new OperationCanceledException(reason));
            }
        }
    }

    private async Task DisposeHubConnectionLockedAsync()
    {
        HubConnection? existing;
        lock (_stateSync)
        {
            existing = _hubConnection;
            _hubConnection = null;
        }

        await SafeDisposeHubAsync(existing).ConfigureAwait(false);
    }

    private static async Task SafeDisposeHubAsync(HubConnection? hub)
    {
        if (hub is null)
        {
            return;
        }

        try
        {
            await hub.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort shutdown.
        }

        try
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore disposal exceptions during cleanup.
        }
    }

    private async Task PublishDiagnosticSafeAsync(string message, bool isError, CancellationToken ct)
    {
        try
        {
            await _eventBus.PublishAsync(new DiagnosticDomainEvent("SignalRRealtimeClient", message, isError), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Ignore secondary event bus notification failures.
        }
    }
}
