using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Events;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Server.Hubs;

namespace Nexus.Server.Services;

/// <summary>
/// Coordinates Nexus Panel profile synchronization and Nexus Automate rule management,
/// permission enforcement, manual/remote trigger dispatch, and SignalR broadcasts.
/// </summary>
public sealed class ServerPanelAndAutomationCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPanelLayoutService _panelService;
    private readonly IAutomationEngine _automationEngine;
    private readonly INexusPresenceTracker _presenceTracker;
    private readonly IHubContext<NexusHub, INexusHubClient> _hubContext;
    private readonly IClock _clock;
    private readonly ILogger<ServerPanelAndAutomationCoordinator> _logger;

    public ServerPanelAndAutomationCoordinator(
        IServiceScopeFactory scopeFactory,
        IPanelLayoutService panelService,
        IAutomationEngine automationEngine,
        INexusPresenceTracker presenceTracker,
        IHubContext<NexusHub, INexusHubClient> hubContext,
        IClock clock,
        ILogger<ServerPanelAndAutomationCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _panelService = panelService;
        _automationEngine = automationEngine;
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

    public Task<Result<IReadOnlyList<PanelProfile>>> GetProfilesAsync(CancellationToken ct)
        => _panelService.GetProfilesAsync(ct);

    public async Task<Result<PanelProfile>> SaveProfileAsync(
        TrustedDeviceRecord caller,
        PanelProfile profile,
        CancellationToken ct)
    {
        var saved = await _panelService.SaveProfileAsync(profile, ct).ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            await BroadcastProfileUpdateAsync(saved.Value, caller.DeviceId, ct).ConfigureAwait(false);
        }

        return saved;
    }

    public async Task<Result<PanelProfile>> ActivateProfileAsync(
        TrustedDeviceRecord caller,
        Guid profileId,
        CancellationToken ct)
    {
        var activated = await _panelService.ActivateProfileAsync(profileId, ct).ConfigureAwait(false);
        if (activated.IsSuccess)
        {
            await BroadcastProfileUpdateAsync(activated.Value, caller.DeviceId, ct).ConfigureAwait(false);
        }

        return activated;
    }

    public Task<Result<AutomationEngineSettings>> GetSettingsAsync(CancellationToken ct)
        => _automationEngine.GetSettingsAsync(ct);

    public Task<Result<AutomationEngineSettings>> SaveSettingsAsync(
        AutomationEngineSettings settings,
        CancellationToken ct)
        => _automationEngine.SaveSettingsAsync(settings, ct);

    public Task<Result<IReadOnlyList<AutomationRule>>> GetRulesAsync(CancellationToken ct)
        => _automationEngine.GetRulesAsync(ct);

    public Task<Result<AutomationRule>> SaveRuleAsync(AutomationRule rule, CancellationToken ct)
        => _automationEngine.SaveRuleAsync(rule, ct);

    public Task<Result> DeleteRuleAsync(Guid ruleId, CancellationToken ct)
        => _automationEngine.DeleteRuleAsync(ruleId, ct);

    public Task<Result<IReadOnlyList<AutomationExecutionRecord>>> GetHistoryAsync(int limit, CancellationToken ct)
        => _automationEngine.GetExecutionHistoryAsync(limit, ct);

    public async Task<Result<IReadOnlyList<AutomationExecutionRecord>>> DispatchTriggerAsync(
        TrustedDeviceRecord caller,
        AutomationTriggerDispatchRequest request,
        CancellationToken ct)
    {
        IReadOnlyList<AutomationExecutionRecord> records;
        if (request.RuleId.HasValue && request.RuleId.Value != Guid.Empty)
        {
            var single = await _automationEngine.TriggerRuleManuallyAsync(
                request.RuleId.Value,
                caller.Permissions,
                request.ApprovedSensitiveExecution,
                ct).ConfigureAwait(false);

            if (single.IsFailure)
            {
                return Result<IReadOnlyList<AutomationExecutionRecord>>.Failure(single.Error!);
            }

            records = [single.Value];
        }
        else
        {
            var triggerEvent = new AutomationTriggerEvent
            {
                CorrelationId = Guid.NewGuid(),
                Trigger = request.Trigger,
                SourceDeviceId = caller.DeviceId,
                IsConnected = true,
                Detail = request.Detail ?? $"Disparo remoto por '{caller.CustomName}'.",
                OccurredAtUtc = _clock.UtcNow
            };

            var multi = await _automationEngine.EvaluateTriggerAsync(triggerEvent, caller.Permissions, ct).ConfigureAwait(false);
            if (multi.IsFailure)
            {
                return multi;
            }

            records = multi.Value;
        }

        foreach (var rec in records)
        {
            await BroadcastExecutionAsync(rec, caller.DeviceId, ct).ConfigureAwait(false);
        }

        return Result<IReadOnlyList<AutomationExecutionRecord>>.Success(records);
    }

    private async Task BroadcastProfileUpdateAsync(PanelProfile profile, Guid senderDeviceId, CancellationToken ct)
    {
        var sessions = _presenceTracker.GetConnectedSessions();
        var targets = sessions.Select(s => s.ConnectionId).ToArray();
        if (targets.Length > 0)
        {
            await _hubContext.Clients.Clients(targets).ReceivePanelProfileUpdated(profile).ConfigureAwait(false);
            await PublishIntegrationEventAsync(
                NexusEventTypes.PanelProfileChanged,
                senderDeviceId,
                $"Perfil '{profile.Name}' atualizado.",
                EventSeverity.Information,
                ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Broadcasted panel profile update {ProfileId} from {DeviceId}", profile.ProfileId, senderDeviceId);
    }

    private async Task BroadcastExecutionAsync(AutomationExecutionRecord record, Guid senderDeviceId, CancellationToken ct)
    {
        var sessions = _presenceTracker.GetConnectedSessions();
        var targets = sessions.Select(s => s.ConnectionId).ToArray();
        if (targets.Length > 0)
        {
            await _hubContext.Clients.Clients(targets).ReceiveAutomationExecution(record).ConfigureAwait(false);
            var eventType = record.Outcome == AutomationExecutionOutcome.Failed
                ? NexusEventTypes.AutomationFailed
                : NexusEventTypes.AutomationTriggered;
            var severity = record.Outcome == AutomationExecutionOutcome.Failed
                ? EventSeverity.Warning
                : EventSeverity.Information;

            await PublishIntegrationEventAsync(
                eventType,
                senderDeviceId,
                $"Automação '{record.RuleName}': {record.Outcome} — {record.Detail}",
                severity,
                ct).ConfigureAwait(false);
        }
    }

    private async Task PublishIntegrationEventAsync(
        string eventType,
        Guid sourceDeviceId,
        string summary,
        EventSeverity severity,
        CancellationToken ct)
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
