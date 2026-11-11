using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Events;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Modules;
using Nexus.Core.Storage;
using Nexus.Server.Hubs;

namespace Nexus.Server.Services;

/// <summary>
/// Server coordinator for Fase 08 integration modules (Control, Notify, Monitor, Media).
/// Enforces per-module permissions, per-module enablement toggles, high-impact confirmation gates,
/// and SignalR real-time state/notification broadcasts.
/// </summary>
public sealed class ServerIntegrationModulesCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IModuleSettingsService _settingsService;
    private readonly IWindowsControlExecutor _controlExecutor;
    private readonly IHostMonitorService _hostMonitorService;
    private readonly IWindowsMediaService _mediaService;
    private readonly INexusNotifyService _notifyService;
    private readonly INexusPresenceTracker _presenceTracker;
    private readonly IHubContext<NexusHub, INexusHubClient> _hubContext;
    private readonly IClock _clock;
    private readonly ILogger<ServerIntegrationModulesCoordinator> _logger;

    public ServerIntegrationModulesCoordinator(
        IServiceScopeFactory scopeFactory,
        IModuleSettingsService settingsService,
        IWindowsControlExecutor controlExecutor,
        IHostMonitorService hostMonitorService,
        IWindowsMediaService mediaService,
        INexusNotifyService notifyService,
        INexusPresenceTracker presenceTracker,
        IHubContext<NexusHub, INexusHubClient> hubContext,
        IClock clock,
        ILogger<ServerIntegrationModulesCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _settingsService = settingsService;
        _controlExecutor = controlExecutor;
        _hostMonitorService = hostMonitorService;
        _mediaService = mediaService;
        _notifyService = notifyService;
        _presenceTracker = presenceTracker;
        _hubContext = hubContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<TrustedDeviceRecord>> AuthorizeCallerAsync(
        Guid callerDeviceId,
        NexusPermission requiredPermission,
        CancellationToken cancellationToken)
    {
        if (callerDeviceId == Guid.Empty)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Identidade do dispositivo ausente.", code: "DEVICE_IDENTITY_MISSING"));
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var deviceResult = await trustedRepo.GetByIdAsync(callerDeviceId, cancellationToken).ConfigureAwait(false);
        if (deviceResult.IsFailure || deviceResult.Value is null)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Dispositivo não está autorizado neste servidor.", code: "DEVICE_NOT_TRUSTED"));
        }

        var device = deviceResult.Value;
        if (device.TrustStatus != DeviceTrustStatus.Authorized)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Forbidden($"Dispositivo está em estado '{device.TrustStatus}'.", code: "DEVICE_NOT_AUTHORIZED"));
        }

        if (requiredPermission != NexusPermission.None && !device.Permissions.HasFlag(requiredPermission))
        {
            var code = $"PERMISSION_DENIED_{requiredPermission.ToString().ToUpperInvariant()}";
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Forbidden($"Dispositivo não possui permissão '{requiredPermission}'.", code: code));
        }

        return Result<TrustedDeviceRecord>.Success(device);
    }

    public Task<Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>> GetModuleStatusesAsync(CancellationToken ct)
        => _settingsService.GetModuleStatusesAsync(ct);

    public async Task<Result<IntegrationModuleStatusSnapshot>> SetModuleEnabledAsync(
        TrustedDeviceRecord caller,
        UpdateModuleToggleRequest request,
        CancellationToken ct)
    {
        var updated = await _settingsService.SetModuleEnabledAsync(request, ct).ConfigureAwait(false);
        if (updated.IsSuccess)
        {
            await BroadcastModuleStatusAsync(updated.Value, caller.DeviceId, ct).ConfigureAwait(false);
        }

        return updated;
    }

    public Task<Result<ControlModuleSettings>> GetControlSettingsAsync(CancellationToken ct)
        => _settingsService.GetControlSettingsAsync(ct);

    public Task<Result<ControlModuleSettings>> SaveControlSettingsAsync(ControlModuleSettings settings, CancellationToken ct)
        => _settingsService.SaveControlSettingsAsync(settings, ct);

    public async Task<Result<RemoteControlExecutionReceipt>> ExecuteControlAsync(
        TrustedDeviceRecord caller,
        RemoteControlCommandRequest request,
        CancellationToken ct)
    {
        var normalized = request with { SourceDeviceId = caller.DeviceId };
        var result = await _controlExecutor.ExecuteAsync(normalized, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result;
        }

        var receipt = result.Value;
        if (!receipt.Executed)
        {
            if (receipt.RequiresConfirmation)
            {
                return Result<RemoteControlExecutionReceipt>.Failure(
                    NexusError.Validation(receipt.Message, code: receipt.ErrorCode ?? "CONFIRMATION_REQUIRED"));
            }

            if (string.Equals(receipt.ErrorCode, "MODULE_DISABLED", StringComparison.Ordinal) ||
                string.Equals(receipt.ErrorCode, "POWER_ACTIONS_DISABLED", StringComparison.Ordinal))
            {
                return Result<RemoteControlExecutionReceipt>.Failure(
                    NexusError.Forbidden(receipt.Message, code: receipt.ErrorCode!));
            }
        }

        await PublishIntegrationEventAsync(
            NexusEventTypes.ControlExecuted,
            caller.DeviceId,
            $"{receipt.Action}: {(receipt.Executed ? "Executado" : receipt.Message)}",
            EventSeverity.Information).ConfigureAwait(false);

        return result;
    }

    public Task<Result<NotifyModuleSettings>> GetNotifySettingsAsync(CancellationToken ct)
        => _settingsService.GetNotifySettingsAsync(ct);

    public Task<Result<NotifyModuleSettings>> SaveNotifySettingsAsync(NotifyModuleSettings settings, CancellationToken ct)
        => _settingsService.SaveNotifySettingsAsync(settings, ct);

    public async Task<Result<PushMobileNotificationReceipt>> PushNotificationAsync(
        TrustedDeviceRecord caller,
        PushMobileNotificationRequest request,
        CancellationToken ct)
    {
        var normalized = request with { SourceDeviceId = caller.DeviceId };
        var result = await _notifyService.IngestAsync(normalized, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result;
        }

        var receipt = result.Value;
        if (receipt.Forwarded)
        {
            var history = await _notifyService.ListHistoryAsync(1, ct).ConfigureAwait(false);
            var record = history.IsSuccess
                ? history.Value.FirstOrDefault(n => n.NotificationId == receipt.NotificationId)
                : null;

            if (record is not null)
            {
                await BroadcastNotificationAsync(record, caller.DeviceId).ConfigureAwait(false);
            }

            await PublishIntegrationEventAsync(
                NexusEventTypes.MobileNotificationForwarded,
                caller.DeviceId,
                $"Notificação '{normalized.AppName}': {normalized.Title}",
                EventSeverity.Information).ConfigureAwait(false);
        }

        return result;
    }

    public Task<Result<IReadOnlyList<MobileNotificationRecord>>> GetNotificationHistoryAsync(int limit, CancellationToken ct)
        => _notifyService.ListHistoryAsync(limit, ct);

    public Task<Result> DismissNotificationAsync(Guid notificationId, CancellationToken ct)
        => _notifyService.DismissAsync(notificationId, ct);

    public Task<Result<int>> ClearNotificationHistoryAsync(CancellationToken ct)
        => _notifyService.ClearHistoryAsync(ct);

    public Task<Result<MonitorModuleSettings>> GetMonitorSettingsAsync(CancellationToken ct)
        => _settingsService.GetMonitorSettingsAsync(ct);

    public Task<Result<MonitorModuleSettings>> SaveMonitorSettingsAsync(MonitorModuleSettings settings, CancellationToken ct)
        => _settingsService.SaveMonitorSettingsAsync(settings, ct);

    public async Task<Result<HostTelemetrySnapshot>> GetMonitorSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await _hostMonitorService.CollectSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.IsSuccess)
        {
            _logger.LogDebug("Collected host monitor snapshot with {DiskCount} disks and CPU {Cpu}%",
                snapshot.Value.Disks.Count,
                snapshot.Value.Cpu.UsagePercent.Value);
        }

        return snapshot;
    }

    public Task<Result<MediaModuleSettings>> GetMediaSettingsAsync(CancellationToken ct)
        => _settingsService.GetMediaSettingsAsync(ct);

    public Task<Result<MediaModuleSettings>> SaveMediaSettingsAsync(MediaModuleSettings settings, CancellationToken ct)
        => _settingsService.SaveMediaSettingsAsync(settings, ct);

    public Task<Result<MediaPlaybackStateContract>> GetMediaStateAsync(CancellationToken ct)
        => _mediaService.GetCurrentStateAsync(ct);

    public async Task<Result<MediaPlaybackStateContract>> ExecuteMediaCommandAsync(
        TrustedDeviceRecord caller,
        MediaControlCommandRequest request,
        CancellationToken ct)
    {
        var normalized = request with { SourceDeviceId = caller.DeviceId };
        var result = await _mediaService.ExecuteCommandAsync(normalized, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await BroadcastMediaStateAsync(result.Value).ConfigureAwait(false);
            await PublishIntegrationEventAsync(
                NexusEventTypes.MediaStateChanged,
                caller.DeviceId,
                $"Mídia executada: {normalized.Action}",
                EventSeverity.Information).ConfigureAwait(false);
        }

        return result;
    }

    private async Task BroadcastNotificationAsync(MobileNotificationRecord record, Guid senderDeviceId)
    {
        var sessions = _presenceTracker.GetConnectedSessions();
        var targets = record.TargetDeviceId.HasValue
            ? sessions.Where(s => s.DeviceId == record.TargetDeviceId.Value).Select(s => s.ConnectionId).ToArray()
            : sessions.Where(s => s.DeviceId != senderDeviceId).Select(s => s.ConnectionId).ToArray();

        if (targets.Length > 0)
        {
            await _hubContext.Clients.Clients(targets).ReceiveMobileNotification(record).ConfigureAwait(false);
        }
    }

    private async Task BroadcastMediaStateAsync(MediaPlaybackStateContract mediaState)
    {
        var sessions = _presenceTracker.GetConnectedSessions();
        var targets = sessions.Select(s => s.ConnectionId).ToArray();
        if (targets.Length > 0)
        {
            await _hubContext.Clients.Clients(targets).ReceiveMediaState(mediaState).ConfigureAwait(false);
        }
    }

    private async Task BroadcastModuleStatusAsync(IntegrationModuleStatusSnapshot status, Guid senderDeviceId, CancellationToken ct)
    {
        var sessions = _presenceTracker.GetConnectedSessions();
        var targets = sessions.Select(s => s.ConnectionId).ToArray();
        if (targets.Length > 0)
        {
            await _hubContext.Clients.Clients(targets).ReceiveModuleStatusChanged(status).ConfigureAwait(false);
        }

        await PublishIntegrationEventAsync(
            NexusEventTypes.ModuleStateChanged,
            senderDeviceId,
            $"Módulo {status.DisplayName} {(status.IsEnabled ? "ativado" : "desativado")}.",
            EventSeverity.Information,
            ct).ConfigureAwait(false);
    }

    private async Task PublishIntegrationEventAsync(
        string eventType,
        Guid sourceDeviceId,
        string summary,
        EventSeverity severity,
        CancellationToken ct = default)
    {
        var sessions = _presenceTracker.GetConnectedSessions();
        if (sessions.Count == 0)
        {
            return;
        }

        var ev = new IntegrationEvent
        {
            EventType = eventType,
            SourceDeviceId = sourceDeviceId,
            Severity = severity,
            PayloadJson = summary,
            OccurredAtUtc = _clock.UtcNow
        };

        await _hubContext.Clients.Clients(sessions.Select(s => s.ConnectionId).ToArray())
            .ReceiveIntegrationEvent(ev)
            .ConfigureAwait(false);
    }
}

