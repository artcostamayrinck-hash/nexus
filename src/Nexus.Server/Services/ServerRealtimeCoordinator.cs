using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Services;
using Nexus.Core.Storage;
using Nexus.Server.Hubs;

namespace Nexus.Server.Services;

/// <summary>
/// Server-side coordinator uniting SignalR presence/sessions, contract validation,
/// sender authentication verification, deduplication/reordering, and message/command routing.
/// </summary>
public sealed class ServerRealtimeCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INexusPresenceTracker _presenceTracker;
    private readonly IHubContext<NexusHub, INexusHubClient> _hubContext;
    private readonly IClock _clock;
    private readonly ILogger<ServerRealtimeCoordinator> _logger;
    private readonly RealtimeMessagePipeline _pipeline;

    public ServerRealtimeCoordinator(
        IServiceScopeFactory scopeFactory,
        INexusPresenceTracker presenceTracker,
        IHubContext<NexusHub, INexusHubClient> hubContext,
        IClock clock,
        ILogger<ServerRealtimeCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _presenceTracker = presenceTracker;
        _hubContext = hubContext;
        _clock = clock;
        _logger = logger;
        _pipeline = new RealtimeMessagePipeline(clock);
    }

    public long DuplicatesDroppedCount => _pipeline.DuplicatesDroppedCount;
    public long OutOfOrderReorderedCount => _pipeline.OutOfOrderReorderedCount;

    public void ResetDeviceStreams(Guid deviceId)
        => _pipeline.ResetStream(deviceId);

    public async Task<IReadOnlyList<DevicePresenceSnapshot>> GetMergedPresenceAsync(CancellationToken cancellationToken = default)
    {
        var activeSessions = _presenceTracker.GetConnectedSessions();
        var byDevice = activeSessions
            .GroupBy(s => s.DeviceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ConnectedAtUtc).First());

        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var trustedRes = await trustedRepo.ListAsync(cancellationToken).ConfigureAwait(false);
        var trustedList = trustedRes.IsSuccess ? trustedRes.Value : Array.Empty<TrustedDeviceRecord>();

        var result = new List<DevicePresenceSnapshot>();
        var seen = new HashSet<Guid>();

        foreach (var trusted in trustedList)
        {
            seen.Add(trusted.DeviceId);
            if (byDevice.TryGetValue(trusted.DeviceId, out var live) && trusted.TrustStatus == DeviceTrustStatus.Authorized)
            {
                result.Add(new DevicePresenceSnapshot
                {
                    DeviceId = trusted.DeviceId,
                    DeviceName = trusted.CustomName,
                    Platform = trusted.Platform,
                    Type = trusted.DeviceType,
                    TrustStatus = trusted.TrustStatus,
                    IsRealtimeConnected = true,
                    ConnectionState = ConnectionState.Connected,
                    ActiveConnectionId = live.ConnectionId,
                    ConnectedAtUtc = live.ConnectedAtUtc,
                    LastSeenAtUtc = live.LastActivityAtUtc ?? live.ConnectedAtUtc,
                    LastHeartbeatAtUtc = live.LastHeartbeatAtUtc,
                    RouteSummary = "SignalR Ativo"
                });
            }
            else
            {
                // Registered/paired devices with NO active hub session are explicitly Disconnected.
                result.Add(new DevicePresenceSnapshot
                {
                    DeviceId = trusted.DeviceId,
                    DeviceName = trusted.CustomName,
                    Platform = trusted.Platform,
                    Type = trusted.DeviceType,
                    TrustStatus = trusted.TrustStatus,
                    IsRealtimeConnected = false,
                    ConnectionState = ConnectionState.Disconnected,
                    ActiveConnectionId = null,
                    LastSeenAtUtc = trusted.LastAccessAtUtc,
                    RouteSummary = trusted.TrustStatus == DeviceTrustStatus.Revoked ? "Revogado" : "Offline"
                });
            }
        }

        // Include any active live connections whose record might not be in trustedRepo snapshot
        foreach (var live in activeSessions.Where(s => s.DeviceId != Guid.Empty && seen.Add(s.DeviceId)))
        {
            result.Add(new DevicePresenceSnapshot
            {
                DeviceId = live.DeviceId,
                DeviceName = live.DeviceName,
                Platform = DevicePlatform.Unknown,
                Type = DeviceType.Unknown,
                TrustStatus = DeviceTrustStatus.Authorized,
                IsRealtimeConnected = true,
                ConnectionState = ConnectionState.Connected,
                ActiveConnectionId = live.ConnectionId,
                ConnectedAtUtc = live.ConnectedAtUtc,
                LastSeenAtUtc = live.LastActivityAtUtc ?? live.ConnectedAtUtc,
                LastHeartbeatAtUtc = live.LastHeartbeatAtUtc,
                RouteSummary = "SignalR Ativo"
            });
        }

        return result.AsReadOnly();
    }

    public async Task BroadcastPresenceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var presence = await GetMergedPresenceAsync(cancellationToken).ConfigureAwait(false);
            await _hubContext.Clients.All.ReceivePresenceSnapshot(presence).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Non-fatal error broadcasting SignalR presence update.");
        }
    }

    public IReadOnlyList<RealtimeSessionSnapshot> GetActiveSessionSnapshots()
    {
        return _presenceTracker.GetConnectedSessions()
            .Select(s => new RealtimeSessionSnapshot
            {
                SessionId = s.SessionId == Guid.Empty ? Guid.CreateVersion7() : s.SessionId,
                ConnectionId = s.ConnectionId,
                DeviceId = s.DeviceId,
                DeviceName = s.DeviceName,
                State = ConnectionState.Connected,
                ConnectedAtUtc = s.ConnectedAtUtc,
                LastActivityAtUtc = s.LastActivityAtUtc ?? s.ConnectedAtUtc,
                MessagesSent = s.MessagesSent,
                MessagesReceived = s.MessagesReceived,
                DuplicatesDropped = _pipeline.DuplicatesDroppedCount,
                OutOfOrderBuffered = _pipeline.OutOfOrderReorderedCount
            })
            .ToList()
            .AsReadOnly();
    }

    public async Task<RealtimeMessageAck> RouteEnvelopeAsync(
        Guid authenticatedCallerId,
        string? callerConnectionId,
        RealtimeMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var validation = ContractValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            return Reject(envelope.MessageId, envelope.CorrelationId, envelope.SourceDeviceId,
                validation.Errors.FirstOrDefault()?.Message ?? "Mensagem inválida.", now);
        }

        if (authenticatedCallerId != Guid.Empty && envelope.SourceDeviceId != authenticatedCallerId)
        {
            return Reject(envelope.MessageId, envelope.CorrelationId, envelope.SourceDeviceId,
                "SPOOFED_SOURCE: SourceDeviceId não corresponde ao dispositivo autenticado.", now);
        }

        var decision = _pipeline.ProcessIncoming(envelope);
        if (decision.Status == RealtimeAckStatus.DuplicateIgnored)
        {
            return new RealtimeMessageAck
            {
                MessageId = envelope.MessageId,
                CorrelationId = envelope.CorrelationId,
                RespondingDeviceId = envelope.TargetDeviceId ?? envelope.SourceDeviceId,
                OriginalSenderDeviceId = envelope.SourceDeviceId,
                Status = RealtimeAckStatus.DuplicateIgnored,
                ExpectedNextSequence = decision.ExpectedNextSequence,
                Detail = decision.DiagnosticDetail,
                AcknowledgedAtUtc = now
            };
        }

        if (decision.Status == RealtimeAckStatus.Rejected)
        {
            return Reject(envelope.MessageId, envelope.CorrelationId, envelope.SourceDeviceId,
                decision.DiagnosticDetail ?? "Mensagem rejeitada.", now);
        }

        if (envelope.TargetDeviceId.HasValue)
        {
            var targets = _presenceTracker.GetConnectionIdsForDevice(envelope.TargetDeviceId.Value);
            if (targets.Count == 0)
            {
                return Reject(envelope.MessageId, envelope.CorrelationId, envelope.SourceDeviceId,
                    $"TARGET_DEVICE_OFFLINE: Dispositivo {envelope.TargetDeviceId} não está conectado em tempo real.", now);
            }

            foreach (var ready in decision.ReadyMessages)
            {
                await _hubContext.Clients.Clients(targets).ReceiveRealtimeMessage(ready).ConfigureAwait(false);
                foreach (var tid in targets)
                {
                    _presenceTracker.RecordActivity(tid, false, incrementSent: false, incrementReceived: true, now);
                }
            }
        }
        else
        {
            var excluded = string.IsNullOrWhiteSpace(callerConnectionId) ? Array.Empty<string>() : new[] { callerConnectionId };
            foreach (var ready in decision.ReadyMessages)
            {
                await _hubContext.Clients.AllExcept(excluded).ReceiveRealtimeMessage(ready).ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrWhiteSpace(callerConnectionId))
        {
            _presenceTracker.RecordActivity(callerConnectionId, false, incrementSent: true, incrementReceived: false, now);
        }

        return new RealtimeMessageAck
        {
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            RespondingDeviceId = envelope.TargetDeviceId ?? authenticatedCallerId,
            OriginalSenderDeviceId = envelope.SourceDeviceId,
            Status = decision.Status == RealtimeAckStatus.OutOfOrderBuffered
                ? RealtimeAckStatus.OutOfOrderBuffered
                : RealtimeAckStatus.Received,
            ExpectedNextSequence = decision.ExpectedNextSequence,
            Detail = decision.Status == RealtimeAckStatus.OutOfOrderBuffered
                ? decision.DiagnosticDetail
                : "Entregue ao hub em tempo real.",
            AcknowledgedAtUtc = now
        };
    }

    public async Task<RealtimeMessageAck> RouteCommandAsync(
        Guid authenticatedCallerId,
        string? callerConnectionId,
        DeviceCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var validation = ContractValidator.Validate(command);
        if (!validation.IsValid)
        {
            return Reject(command.CommandId, command.CorrelationId, command.SourceDeviceId,
                validation.Errors.FirstOrDefault()?.Message ?? "Comando inválido.", now);
        }

        if (authenticatedCallerId != Guid.Empty && command.SourceDeviceId != authenticatedCallerId)
        {
            return Reject(command.CommandId, command.CorrelationId, command.SourceDeviceId,
                "SPOOFED_SOURCE: Origem do comando não corresponde ao token autenticado.", now);
        }

        var targets = _presenceTracker.GetConnectionIdsForDevice(command.TargetDeviceId);
        if (targets.Count == 0)
        {
            return Reject(command.CommandId, command.CorrelationId, command.SourceDeviceId,
                $"TARGET_DEVICE_OFFLINE: Dispositivo de destino {command.TargetDeviceId} está desconectado.", now);
        }

        if (!string.IsNullOrWhiteSpace(callerConnectionId))
        {
            _presenceTracker.RecordActivity(callerConnectionId, false, incrementSent: true, incrementReceived: false, now);
        }

        await _hubContext.Clients.Clients(targets).ReceiveDeviceCommand(command).ConfigureAwait(false);
        return new RealtimeMessageAck
        {
            MessageId = command.CommandId,
            CorrelationId = command.CorrelationId,
            RespondingDeviceId = command.TargetDeviceId,
            OriginalSenderDeviceId = command.SourceDeviceId,
            Status = RealtimeAckStatus.Received,
            Detail = $"Comando '{command.CommandName}' encaminhado ao destino conectado.",
            AcknowledgedAtUtc = now
        };
    }

    public async Task<RealtimeMessageAck> RouteCancellationAsync(
        Guid authenticatedCallerId,
        string? callerConnectionId,
        CommandCancellationRequest cancellation,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var validation = ContractValidator.Validate(cancellation);
        if (!validation.IsValid)
        {
            return Reject(cancellation.CommandId, cancellation.CorrelationId, cancellation.SourceDeviceId,
                validation.Errors.FirstOrDefault()?.Message ?? "Cancelamento inválido.", now);
        }

        if (authenticatedCallerId != Guid.Empty && cancellation.SourceDeviceId != authenticatedCallerId)
        {
            return Reject(cancellation.CommandId, cancellation.CorrelationId, cancellation.SourceDeviceId,
                "SPOOFED_SOURCE: Origem do cancelamento não autorizada.", now);
        }

        var targets = _presenceTracker.GetConnectionIdsForDevice(cancellation.TargetDeviceId);
        if (targets.Count == 0)
        {
            return Reject(cancellation.CommandId, cancellation.CorrelationId, cancellation.SourceDeviceId,
                "TARGET_DEVICE_OFFLINE: Dispositivo alvo está desconectado.", now);
        }

        await _hubContext.Clients.Clients(targets).ReceiveCommandCancellation(cancellation).ConfigureAwait(false);
        return new RealtimeMessageAck
        {
            MessageId = cancellation.CommandId,
            CorrelationId = cancellation.CorrelationId,
            RespondingDeviceId = cancellation.TargetDeviceId,
            OriginalSenderDeviceId = cancellation.SourceDeviceId,
            Status = RealtimeAckStatus.Cancelled,
            Detail = cancellation.Reason ?? "Solicitação de cancelamento entregue.",
            AcknowledgedAtUtc = now
        };
    }

    public async Task RouteCommandResultAsync(
        Guid authenticatedCallerId,
        Guid targetDeviceId,
        Guid correlationId,
        CommandExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        if (!ContractValidator.Validate(result).IsValid)
        {
            return;
        }

        var targets = _presenceTracker.GetConnectionIdsForDevice(targetDeviceId);
        if (targets.Count > 0)
        {
            await _hubContext.Clients.Clients(targets).ReceiveCommandResult(result).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug("Result for command {CommandId} dropped because caller {Target} is disconnected.", result.CommandId, targetDeviceId);
        }
    }

    public async Task RouteAckAsync(
        Guid authenticatedCallerId,
        RealtimeMessageAck ack,
        CancellationToken cancellationToken = default)
    {
        if (!ContractValidator.Validate(ack).IsValid)
        {
            return;
        }

        if (authenticatedCallerId != Guid.Empty && ack.RespondingDeviceId != authenticatedCallerId)
        {
            return;
        }

        if (ack.OriginalSenderDeviceId.HasValue)
        {
            var targets = _presenceTracker.GetConnectionIdsForDevice(ack.OriginalSenderDeviceId.Value);
            if (targets.Count > 0)
            {
                await _hubContext.Clients.Clients(targets).ReceiveMessageAck(ack).ConfigureAwait(false);
            }
        }
    }

    private static RealtimeMessageAck Reject(Guid messageId, Guid correlationId, Guid senderId, string reason, DateTimeOffset now)
        => new()
        {
            MessageId = messageId == Guid.Empty ? Guid.CreateVersion7() : messageId,
            CorrelationId = correlationId == Guid.Empty ? Guid.CreateVersion7() : correlationId,
            RespondingDeviceId = senderId == Guid.Empty ? Guid.CreateVersion7() : senderId,
            OriginalSenderDeviceId = senderId == Guid.Empty ? null : senderId,
            Status = RealtimeAckStatus.Rejected,
            Detail = reason,
            AcknowledgedAtUtc = now
        };
}

