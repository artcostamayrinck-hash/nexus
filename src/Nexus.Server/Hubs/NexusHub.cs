using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Sync;
using Nexus.Server.Services;

namespace Nexus.Server.Hubs;

public interface INexusHubClient
{
    Task ReceiveConnectionSnapshot(ConnectionStateSnapshot snapshot);
    Task ReceiveIntegrationEvent(IntegrationEvent integrationEvent);
    Task ReceivePairingSnapshot(PairingSessionSnapshot snapshot);
    Task ReceiveRevocationNotice(Guid deviceId, string reason);
    Task ReceivePong(DateTimeOffset serverTimeUtc);

    Task ReceiveRealtimeMessage(RealtimeMessageEnvelope envelope);
    Task ReceiveMessageAck(RealtimeMessageAck ack);
    Task ReceiveDeviceCommand(DeviceCommand command);
    Task ReceiveCommandCancellation(CommandCancellationRequest cancellation);
    Task ReceiveCommandResult(CommandExecutionResult result);
    Task ReceivePresenceSnapshot(IReadOnlyList<DevicePresenceSnapshot> presenceList);
    Task ReceiveSessionNotice(RealtimeSessionSnapshot session);
    Task ReceiveClipboardEntry(ClipboardEntryContract entry);
    Task ReceiveNoteUpdated(NexusNoteContract note);
}

public sealed record ConnectedHubSession(
    string ConnectionId,
    Guid DeviceId,
    string DeviceName,
    DateTimeOffset ConnectedAtUtc,
    Guid SessionId = default,
    DateTimeOffset? LastActivityAtUtc = null,
    DateTimeOffset? LastHeartbeatAtUtc = null,
    long MessagesSent = 0,
    long MessagesReceived = 0);

public interface INexusPresenceTracker
{
    void Register(string connectionId, Guid deviceId, string deviceName, DateTimeOffset connectedAtUtc);
    void Unregister(string connectionId);
    IReadOnlyList<ConnectedHubSession> GetConnectedSessions();
    IReadOnlyList<string> GetConnectionIdsForDevice(Guid deviceId);
    void RecordActivity(string connectionId, bool isHeartbeat, bool incrementSent, bool incrementReceived, DateTimeOffset timestampUtc);
}

public sealed class InMemoryPresenceTracker : INexusPresenceTracker
{
    private readonly ConcurrentDictionary<string, ConnectedHubSession> _sessions = new(StringComparer.Ordinal);

    public void Register(string connectionId, Guid deviceId, string deviceName, DateTimeOffset connectedAtUtc)
    {
        _sessions.AddOrUpdate(
            connectionId,
            _ => new ConnectedHubSession(
                connectionId,
                deviceId,
                deviceName,
                connectedAtUtc,
                Guid.CreateVersion7(),
                connectedAtUtc,
                connectedAtUtc),
            (_, existing) => existing with
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                LastActivityAtUtc = connectedAtUtc
            });
    }

    public void Unregister(string connectionId)
        => _sessions.TryRemove(connectionId, out _);

    public IReadOnlyList<ConnectedHubSession> GetConnectedSessions()
        => _sessions.Values.OrderBy(s => s.ConnectedAtUtc).ToArray();

    public IReadOnlyList<string> GetConnectionIdsForDevice(Guid deviceId)
        => _sessions.Values
            .Where(s => s.DeviceId == deviceId)
            .Select(s => s.ConnectionId)
            .ToArray();

    public void RecordActivity(
        string connectionId,
        bool isHeartbeat,
        bool incrementSent,
        bool incrementReceived,
        DateTimeOffset timestampUtc)
    {
        _sessions.AddOrUpdate(
            connectionId,
            _ => new ConnectedHubSession(connectionId, Guid.Empty, "Unknown", timestampUtc),
            (_, existing) => existing with
            {
                LastActivityAtUtc = timestampUtc,
                LastHeartbeatAtUtc = isHeartbeat ? timestampUtc : existing.LastHeartbeatAtUtc,
                MessagesSent = incrementSent ? existing.MessagesSent + 1 : existing.MessagesSent,
                MessagesReceived = incrementReceived ? existing.MessagesReceived + 1 : existing.MessagesReceived
            });
    }
}

/// <summary>
/// Authenticated ASP.NET Core SignalR hub for real-time commands, events, acknowledgements,
/// live session/presence tracking, and revocation notifications.
/// </summary>
[Authorize]
public sealed class NexusHub : Hub<INexusHubClient>
{
    private readonly INexusPresenceTracker _presenceTracker;
    private readonly ServerRealtimeCoordinator _coordinator;
    private readonly ILogger<NexusHub> _logger;

    public NexusHub(
        INexusPresenceTracker presenceTracker,
        ServerRealtimeCoordinator coordinator,
        ILogger<NexusHub> logger)
    {
        _presenceTracker = presenceTracker;
        _coordinator = coordinator;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var (deviceId, deviceName) = ExtractCallerIdentity();
        if (deviceId != Guid.Empty)
        {
            _presenceTracker.Register(Context.ConnectionId, deviceId, deviceName, DateTimeOffset.UtcNow);
        }

        _logger.LogInformation("SignalR client connected: {ConnectionId} ({DeviceId})", Context.ConnectionId, deviceId);
        await base.OnConnectedAsync().ConfigureAwait(false);
        await _coordinator.BroadcastPresenceAsync(Context.ConnectionAborted).ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _presenceTracker.Unregister(Context.ConnectionId);
        _logger.LogInformation(exception, "SignalR client disconnected: {ConnectionId}", Context.ConnectionId);
        await _coordinator.BroadcastPresenceAsync(CancellationToken.None).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public async Task AnnouncePresenceAsync(Guid deviceId, string deviceName)
    {
        var (claimId, _) = ExtractCallerIdentity();
        var effectiveId = claimId != Guid.Empty ? claimId : deviceId;
        var effectiveName = string.IsNullOrWhiteSpace(deviceName) ? "NEXUS Device" : deviceName.Trim();

        _presenceTracker.Register(Context.ConnectionId, effectiveId, effectiveName, DateTimeOffset.UtcNow);
        _logger.LogInformation("Hub presence announced for device {DeviceId} ({DeviceName})", effectiveId, effectiveName);
        await _coordinator.BroadcastPresenceAsync(Context.ConnectionAborted).ConfigureAwait(false);
        await Clients.Caller.ReceivePong(DateTimeOffset.UtcNow).ConfigureAwait(false);
    }

    public Task ResetSessionStreamAsync(Guid deviceId)
    {
        var (claimId, _) = ExtractCallerIdentity();
        if (claimId != Guid.Empty && claimId != deviceId)
        {
            throw new HubException("Não autorizado a redefinir stream de outro dispositivo.");
        }

        _coordinator.ResetDeviceStreams(claimId != Guid.Empty ? claimId : deviceId);
        return Task.CompletedTask;
    }

    public async Task PingAsync()
    {
        var now = DateTimeOffset.UtcNow;
        _presenceTracker.RecordActivity(Context.ConnectionId, isHeartbeat: true, incrementSent: false, incrementReceived: false, now);
        await Clients.Caller.ReceivePong(now).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DevicePresenceSnapshot>> GetPresenceSnapshotAsync()
        => _coordinator.GetMergedPresenceAsync(Context.ConnectionAborted);

    public Task<RealtimeMessageAck> SendRealtimeMessageAsync(RealtimeMessageEnvelope envelope)
    {
        var (callerId, _) = ExtractCallerIdentity();
        return _coordinator.RouteEnvelopeAsync(callerId, Context.ConnectionId, envelope, Context.ConnectionAborted);
    }

    public Task<RealtimeMessageAck> SendCommandAsync(DeviceCommand command)
    {
        var (callerId, _) = ExtractCallerIdentity();
        return _coordinator.RouteCommandAsync(callerId, Context.ConnectionId, command, Context.ConnectionAborted);
    }

    public Task<RealtimeMessageAck> CancelCommandAsync(CommandCancellationRequest cancellation)
    {
        var (callerId, _) = ExtractCallerIdentity();
        return _coordinator.RouteCancellationAsync(callerId, Context.ConnectionId, cancellation, Context.ConnectionAborted);
    }

    public Task SendCommandResultAsync(CommandExecutionResult result, Guid targetDeviceId, Guid correlationId)
    {
        var (callerId, _) = ExtractCallerIdentity();
        return _coordinator.RouteCommandResultAsync(callerId, targetDeviceId, correlationId, result, Context.ConnectionAborted);
    }

    public Task AcknowledgeMessageAsync(RealtimeMessageAck ack)
    {
        var (callerId, _) = ExtractCallerIdentity();
        return _coordinator.RouteAckAsync(callerId, ack, Context.ConnectionAborted);
    }

    private (Guid DeviceId, string DeviceName) ExtractCallerIdentity()
    {
        var rawId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var parsedId = Guid.TryParse(rawId, out var id) ? id : Guid.Empty;
        var name = Context.User?.Identity?.Name ?? "NEXUS Device";
        return (parsedId, name);
    }
}
