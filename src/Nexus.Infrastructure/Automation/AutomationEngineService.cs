using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Security;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Modules;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;

namespace Nexus.Infrastructure.Automation;

/// <summary>
/// Event-driven and manual rule evaluation engine for Nexus Automate with condition matching,
/// sensitive-action authorization, per-rule cooldown and rate limits, recursion/cycle protection,
/// SQLite execution history logging, and an immediate global Emergency Kill Switch.
/// </summary>
public sealed class AutomationEngineService : IAutomationEngine, IDisposable
{
    public const string AutomationSettingsKey = "nexus.automation.settings.v1";
    private const int MaxRuntimeIndicators = 24;

    private static readonly AsyncLocal<ImmutableHashSet<Guid>?> ActiveExecutionChain = new();

    private readonly IAutomationRuleRepository _ruleRepository;
    private readonly IAutomationExecutionRepository _executionRepository;
    private readonly ISettingsStore _settingsStore;
    private readonly IPanelLayoutService _panelLayoutService;
    private readonly IDeviceBatteryProvider _batteryProvider;
    private readonly IApprovedAppLauncher _appLauncher;
    private readonly IWindowsControlExecutor _controlExecutor;
    private readonly IWindowsMediaService _mediaService;
    private readonly INexusNotifyService _notifyService;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IClock _clock;
    private readonly ILogger<AutomationEngineService> _logger;
    private readonly INexusRouteOrchestrator? _routeOrchestrator;
    private readonly IRealtimeClientChannel? _realtimeChannel;
    private readonly ITransferManager? _transferManager;

    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private readonly List<PanelRuntimeIndicator> _activeIndicators = new();
    private readonly Dictionary<Guid, List<DateTimeOffset>> _executionWindowByRule = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastSuccessByRule = new();
    private readonly ConcurrentDictionary<Guid, TransferState> _lastTransferStates = new();

    private AutomationEngineSettings? _cachedSettings;
    private ConnectionState _lastConnectionState = ConnectionState.Disconnected;
    private string? _lastRouteKey;
    private bool _disposed;

    public AutomationEngineService(
        IAutomationRuleRepository ruleRepository,
        IAutomationExecutionRepository executionRepository,
        ISettingsStore settingsStore,
        IPanelLayoutService panelLayoutService,
        IDeviceBatteryProvider batteryProvider,
        IApprovedAppLauncher appLauncher,
        IWindowsControlExecutor controlExecutor,
        IWindowsMediaService mediaService,
        INexusNotifyService notifyService,
        ILocalDeviceContext localDevice,
        IClock clock,
        ILogger<AutomationEngineService> logger,
        INexusRouteOrchestrator? routeOrchestrator = null,
        IRealtimeClientChannel? realtimeChannel = null,
        ITransferManager? transferManager = null)
    {
        _ruleRepository = ruleRepository;
        _executionRepository = executionRepository;
        _settingsStore = settingsStore;
        _panelLayoutService = panelLayoutService;
        _batteryProvider = batteryProvider;
        _appLauncher = appLauncher;
        _controlExecutor = controlExecutor;
        _mediaService = mediaService;
        _notifyService = notifyService;
        _localDevice = localDevice;
        _clock = clock;
        _logger = logger;
        _routeOrchestrator = routeOrchestrator;
        _realtimeChannel = realtimeChannel;
        _transferManager = transferManager;

        WireReactiveSources();
    }

    public event EventHandler<AutomationExecutionRecord>? ExecutionLogged;
    public event EventHandler<PanelRuntimeIndicator>? IndicatorEmitted;
    public event EventHandler<AutomationEngineSettings>? SettingsChanged;

    public IReadOnlyList<PanelRuntimeIndicator> ActiveIndicators
    {
        get
        {
            lock (_activeIndicators)
            {
                return _activeIndicators.ToArray();
            }
        }
    }

    public async Task<Result<AutomationEngineSettings>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSettings is not null)
            {
                return Result<AutomationEngineSettings>.Success(_cachedSettings);
            }

            var stored = await _settingsStore.GetAsync<AutomationEngineSettings>(AutomationSettingsKey, cancellationToken).ConfigureAwait(false);
            if (stored.IsFailure)
            {
                return Result<AutomationEngineSettings>.Failure(stored.Error!);
            }

            if (stored.Value is not null)
            {
                _cachedSettings = stored.Value;
                return Result<AutomationEngineSettings>.Success(_cachedSettings);
            }

            var defaults = CreateDefaultSettings();
            var saved = await _settingsStore.SetAsync(AutomationSettingsKey, defaults, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                return Result<AutomationEngineSettings>.Failure(saved.Error!);
            }

            _cachedSettings = defaults;
            return Result<AutomationEngineSettings>.Success(defaults);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<Result<AutomationEngineSettings>> SaveSettingsAsync(
        AutomationEngineSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var shortcut in settings.ApprovedApps)
        {
            var err = shortcut.Validate().FirstOrDefault();
            if (err is not null)
            {
                return Result<AutomationEngineSettings>.Failure(NexusError.Validation(err.Message, code: err.Code));
            }
        }

        var normalized = settings with
        {
            MaxChainDepth = Math.Clamp(settings.MaxChainDepth <= 0 ? 4 : settings.MaxChainDepth, 1, 16),
            ApprovedApps = settings.ApprovedApps
                .DistinctBy(a => a.ShortcutId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            UpdatedAtUtc = _clock.UtcNow
        };

        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var res = await _settingsStore.SetAsync(AutomationSettingsKey, normalized, cancellationToken).ConfigureAwait(false);
            if (res.IsFailure)
            {
                return Result<AutomationEngineSettings>.Failure(res.Error!);
            }

            _cachedSettings = normalized;
        }
        finally
        {
            _settingsLock.Release();
        }

        SettingsChanged?.Invoke(this, normalized);
        return Result<AutomationEngineSettings>.Success(normalized);
    }

    public async Task<Result<AutomationEngineSettings>> SetEmergencyKillSwitchAsync(
        bool active,
        CancellationToken cancellationToken = default)
    {
        var current = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result<AutomationEngineSettings>.Failure(current.Error!);
        }

        var updated = current.Value with
        {
            EmergencyKillSwitchActive = active
        };

        return await SaveSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<IReadOnlyList<AutomationRule>>> GetRulesAsync(CancellationToken cancellationToken = default)
        => _ruleRepository.ListAsync(cancellationToken);

    public async Task<Result<AutomationRule>> SaveRuleAsync(
        AutomationRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var errors = rule.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Result<AutomationRule>.Failure(NexusError.Validation(errors[0].Message, code: errors[0].Code));
        }

        var now = _clock.UtcNow;
        var existing = await _ruleRepository.GetByIdAsync(rule.RuleId, cancellationToken).ConfigureAwait(false);
        var created = existing.IsSuccess && existing.Value is not null ? existing.Value.CreatedAtUtc : (rule.CreatedAtUtc == default ? now : rule.CreatedAtUtc);

        var sanitized = rule with
        {
            Name = rule.Name.Trim(),
            Description = rule.Description?.Trim() ?? string.Empty,
            CreatedAtUtc = created,
            UpdatedAtUtc = now
        };

        var upsert = await _ruleRepository.UpsertAsync(sanitized, cancellationToken).ConfigureAwait(false);
        return upsert.IsSuccess
            ? Result<AutomationRule>.Success(sanitized)
            : Result<AutomationRule>.Failure(upsert.Error!);
    }

    public async Task<Result<AutomationRule>> SetRuleEnabledAsync(
        Guid ruleId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var existing = await _ruleRepository.GetByIdAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<AutomationRule>.Failure(existing.Error!);
        }

        if (existing.Value is null)
        {
            return Result<AutomationRule>.Failure(NexusError.NotFound($"Regra '{ruleId}' não encontrada.", "AUTO_RULE_NOT_FOUND"));
        }

        var updated = existing.Value with
        {
            IsEnabled = enabled,
            UpdatedAtUtc = _clock.UtcNow
        };

        var upsert = await _ruleRepository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return upsert.IsSuccess
            ? Result<AutomationRule>.Success(updated)
            : Result<AutomationRule>.Failure(upsert.Error!);
    }

    public Task<Result> DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
        => _ruleRepository.DeleteAsync(ruleId, cancellationToken);

    public Task<Result<IReadOnlyList<AutomationExecutionRecord>>> GetExecutionHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => _executionRepository.ListRecentAsync(limit, cancellationToken);

    public Task<Result<int>> ClearExecutionHistoryAsync(CancellationToken cancellationToken = default)
        => _executionRepository.ClearAsync(cancellationToken);

    public async Task<Result<IReadOnlyList<AutomationExecutionRecord>>> EvaluateTriggerAsync(
        AutomationTriggerEvent triggerEvent,
        NexusPermission callerPermissions = NexusPermission.All,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerEvent);

        if ((callerPermissions & NexusPermission.Automation) != NexusPermission.Automation)
        {
            return Result<IReadOnlyList<AutomationExecutionRecord>>.Failure(
                NexusError.Forbidden("Permissão Automation ausente para disparar automações.", "PERMISSION_DENIED"));
        }

        var settingsRes = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsFailure)
        {
            return Result<IReadOnlyList<AutomationExecutionRecord>>.Failure(settingsRes.Error!);
        }

        var settings = settingsRes.Value;
        if (settings.EmergencyKillSwitchActive || !settings.GlobalAutomationsEnabled)
        {
            return Result<IReadOnlyList<AutomationExecutionRecord>>.Success(Array.Empty<AutomationExecutionRecord>());
        }

        var rulesRes = await _ruleRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        if (rulesRes.IsFailure)
        {
            return Result<IReadOnlyList<AutomationExecutionRecord>>.Failure(rulesRes.Error!);
        }

        var matchingRules = rulesRes.Value
            .Where(r => r.IsEnabled && r.Trigger == triggerEvent.Trigger)
            .Where(r => string.IsNullOrWhiteSpace(r.ExpectedRouteFilter) ||
                        string.Equals(r.ExpectedRouteFilter, triggerEvent.ActiveRouteKind, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingRules.Length == 0)
        {
            return Result<IReadOnlyList<AutomationExecutionRecord>>.Success(Array.Empty<AutomationExecutionRecord>());
        }

        var battery = triggerEvent.Battery ?? await ReadSafeBatteryAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<AutomationExecutionRecord>(matchingRules.Length);

        foreach (var rule in matchingRules)
        {
            var rec = await ExecuteSingleRuleAsync(
                rule,
                triggerEvent,
                battery,
                settings,
                callerPermissions,
                approvedSensitiveExecution: false,
                cancellationToken).ConfigureAwait(false);

            if (rec is not null)
            {
                records.Add(rec);
            }
        }

        return Result<IReadOnlyList<AutomationExecutionRecord>>.Success(records.AsReadOnly());
    }

    public async Task<Result<AutomationExecutionRecord>> TriggerRuleManuallyAsync(
        Guid ruleId,
        NexusPermission callerPermissions = NexusPermission.All,
        bool approvedSensitiveExecution = false,
        CancellationToken cancellationToken = default)
    {
        if (ruleId == Guid.Empty)
        {
            return Result<AutomationExecutionRecord>.Failure(NexusError.Validation("RuleId inválido.", code: "AUTO_RULE_ID_EMPTY"));
        }

        if ((callerPermissions & NexusPermission.Automation) != NexusPermission.Automation)
        {
            return Result<AutomationExecutionRecord>.Failure(
                NexusError.Forbidden("Permissão Automation ausente para executar automação.", "PERMISSION_DENIED"));
        }

        var settingsRes = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsFailure)
        {
            return Result<AutomationExecutionRecord>.Failure(settingsRes.Error!);
        }

        var settings = settingsRes.Value;
        var ruleRes = await _ruleRepository.GetByIdAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (ruleRes.IsFailure)
        {
            return Result<AutomationExecutionRecord>.Failure(ruleRes.Error!);
        }

        var rule = ruleRes.Value;
        if (rule is null)
        {
            return Result<AutomationExecutionRecord>.Failure(NexusError.NotFound($"Regra '{ruleId}' não encontrada.", "AUTO_RULE_NOT_FOUND"));
        }

        var now = _clock.UtcNow;
        if (settings.EmergencyKillSwitchActive || !settings.GlobalAutomationsEnabled)
        {
            var disabledRecord = new AutomationExecutionRecord
            {
                ExecutionId = Guid.NewGuid(),
                RuleId = rule.RuleId,
                RuleName = rule.Name,
                Trigger = AutomationTriggerType.Manual,
                Outcome = AutomationExecutionOutcome.SkippedDisabled,
                Detail = settings.EmergencyKillSwitchActive
                    ? "Execução bloqueada pelo Kill Switch de emergência."
                    : "Motor global de automações desativado.",
                CorrelationId = Guid.NewGuid(),
                DurationMs = 0,
                ExecutedAtUtc = now
            };
            await PersistAndBroadcastExecutionAsync(disabledRecord, cancellationToken).ConfigureAwait(false);
            return Result<AutomationExecutionRecord>.Success(disabledRecord);
        }

        if (!rule.IsEnabled)
        {
            var skipped = new AutomationExecutionRecord
            {
                ExecutionId = Guid.NewGuid(),
                RuleId = rule.RuleId,
                RuleName = rule.Name,
                Trigger = AutomationTriggerType.Manual,
                Outcome = AutomationExecutionOutcome.SkippedDisabled,
                Detail = "Regra desativada individualmente pelo usuário.",
                CorrelationId = Guid.NewGuid(),
                DurationMs = 0,
                ExecutedAtUtc = now
            };
            await PersistAndBroadcastExecutionAsync(skipped, cancellationToken).ConfigureAwait(false);
            return Result<AutomationExecutionRecord>.Success(skipped);
        }

        var battery = await ReadSafeBatteryAsync(cancellationToken).ConfigureAwait(false);
        var manualEvent = new AutomationTriggerEvent
        {
            CorrelationId = Guid.NewGuid(),
            Trigger = rule.Trigger,
            SourceDeviceId = _localDevice.DeviceId,
            IsConnected = _realtimeChannel?.State == ConnectionState.Connected
                          || _routeOrchestrator?.CurrentStatus.State == ConnectionState.Connected,
            ActiveRouteKind = _routeOrchestrator?.CurrentStatus.RouteKind.ToString(),
            Battery = battery,
            Detail = "Execução manual solicitada pelo painel.",
            OccurredAtUtc = now
        };

        var resultRecord = await ExecuteSingleRuleAsync(
            rule,
            manualEvent,
            battery,
            settings,
            callerPermissions,
            approvedSensitiveExecution,
            cancellationToken).ConfigureAwait(false);

        return Result<AutomationExecutionRecord>.Success(resultRecord!);
    }

    private async Task<AutomationExecutionRecord?> ExecuteSingleRuleAsync(
        AutomationRule rule,
        AutomationTriggerEvent triggerEvent,
        BatteryStateSnapshot battery,
        AutomationEngineSettings settings,
        NexusPermission callerPermissions,
        bool approvedSensitiveExecution,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var now = _clock.UtcNow;
        var activeSet = ActiveExecutionChain.Value ?? ImmutableHashSet<Guid>.Empty;
        var effectiveDepth = Math.Max(triggerEvent.ChainDepth, activeSet.Count);

        if (effectiveDepth >= settings.MaxChainDepth
            || triggerEvent.VisitedRuleIds.Contains(rule.RuleId)
            || activeSet.Contains(rule.RuleId))
        {
            sw.Stop();
            var loopRecord = CreateRecord(
                rule,
                triggerEvent,
                AutomationExecutionOutcome.SkippedLoopGuard,
                $"Proteção anti-loop ativada (profundidade={effectiveDepth}/{settings.MaxChainDepth}).",
                sw.ElapsedMilliseconds,
                now);
            await PersistAndBroadcastExecutionAsync(loopRecord, cancellationToken).ConfigureAwait(false);
            return loopRecord;
        }

        if (!EvaluateRuleConditions(rule, triggerEvent, battery, out var conditionReason))
        {
            sw.Stop();
            var unmetRecord = CreateRecord(
                rule,
                triggerEvent,
                AutomationExecutionOutcome.ConditionsNotMet,
                conditionReason,
                sw.ElapsedMilliseconds,
                now);
            await PersistAndBroadcastExecutionAsync(unmetRecord, cancellationToken).ConfigureAwait(false);
            return unmetRecord;
        }

        var missingPermission = FindMissingPermission(rule, callerPermissions);
        if (missingPermission is not null)
        {
            sw.Stop();
            var permRecord = CreateRecord(
                rule,
                triggerEvent,
                AutomationExecutionOutcome.BlockedPermission,
                missingPermission,
                sw.ElapsedMilliseconds,
                now);
            await PersistAndBroadcastExecutionAsync(permRecord, cancellationToken).ConfigureAwait(false);
            return permRecord;
        }

        if (rule.ContainsSensitiveActions
            && rule.RequireSensitiveAuthorization
            && !rule.IsSensitiveAuthorized
            && !approvedSensitiveExecution)
        {
            sw.Stop();
            var authRecord = CreateRecord(
                rule,
                triggerEvent,
                AutomationExecutionOutcome.BlockedAuthorization,
                "Ação sensível bloqueada até receber autorização explícita.",
                sw.ElapsedMilliseconds,
                now);
            await PersistAndBroadcastExecutionAsync(authRecord, cancellationToken).ConfigureAwait(false);
            return authRecord;
        }

        var rateOutcome = await TryAcquireExecutionQuotaAsync(rule, now, cancellationToken).ConfigureAwait(false);
        if (rateOutcome is not null)
        {
            sw.Stop();
            var throttled = CreateRecord(
                rule,
                triggerEvent,
                rateOutcome.Value.Outcome,
                rateOutcome.Value.Reason,
                sw.ElapsedMilliseconds,
                now);
            await PersistAndBroadcastExecutionAsync(throttled, cancellationToken).ConfigureAwait(false);
            return throttled;
        }

        var previousChain = ActiveExecutionChain.Value;
        ActiveExecutionChain.Value = activeSet.Add(rule.RuleId);

        try
        {
            var actionNotes = new List<string>(rule.Actions.Count);
            foreach (var action in rule.Actions)
            {
                var actionRes = await ExecuteActionAsync(
                    rule,
                    action,
                    settings,
                    approvedSensitiveExecution || rule.IsSensitiveAuthorized,
                    cancellationToken).ConfigureAwait(false);

                if (actionRes.IsFailure)
                {
                    sw.Stop();
                    var failDetail = $"{action.ActionType}: {actionRes.Error!.Message} ({actionRes.Error.Code})";
                    _logger.LogWarning("Automation rule {RuleId} ('{Name}') failed: {Detail}", rule.RuleId, rule.Name, failDetail);
                    var failedRecord = CreateRecord(
                        rule,
                        triggerEvent,
                        AutomationExecutionOutcome.Failed,
                        failDetail,
                        sw.ElapsedMilliseconds,
                        _clock.UtcNow);
                    await PersistAndBroadcastExecutionAsync(failedRecord, cancellationToken).ConfigureAwait(false);
                    return failedRecord;
                }

                actionNotes.Add(actionRes.Value);
            }

            sw.Stop();
            var updatedRule = rule with { LastExecutedAtUtc = now, UpdatedAtUtc = now };
            await _ruleRepository.UpsertAsync(updatedRule, cancellationToken).ConfigureAwait(false);

            var okRecord = CreateRecord(
                rule,
                triggerEvent,
                AutomationExecutionOutcome.Succeeded,
                string.Join(" | ", actionNotes),
                sw.ElapsedMilliseconds,
                _clock.UtcNow);
            await PersistAndBroadcastExecutionAsync(okRecord, cancellationToken).ConfigureAwait(false);
            return okRecord;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "Unexpected failure executing automation rule {RuleId}", rule.RuleId);
            var errRecord = CreateRecord(
                rule,
                triggerEvent,
                AutomationExecutionOutcome.Failed,
                $"Exceção inesperada: {ex.Message}",
                sw.ElapsedMilliseconds,
                _clock.UtcNow);
            await PersistAndBroadcastExecutionAsync(errRecord, cancellationToken).ConfigureAwait(false);
            return errRecord;
        }
        finally
        {
            ActiveExecutionChain.Value = previousChain;
        }
    }

    private async Task<Result<string>> ExecuteActionAsync(
        AutomationRule rule,
        AutomationAction action,
        AutomationEngineSettings settings,
        bool confirmedSensitive,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        switch (action.ActionType)
        {
            case AutomationActionType.ActivatePanelProfile:
            {
                if (!action.TargetProfileId.HasValue || action.TargetProfileId.Value == Guid.Empty)
                {
                    return Result<string>.Failure(NexusError.Validation("Perfil de destino inválido.", code: "AUTO_PROFILE_MISSING"));
                }

                var activated = await _panelLayoutService.ActivateProfileAsync(action.TargetProfileId.Value, cancellationToken).ConfigureAwait(false);
                return activated.IsSuccess
                    ? Result<string>.Success($"Perfil '{activated.Value.Name}' ativado.")
                    : Result<string>.Failure(activated.Error!);
            }

            case AutomationActionType.ShowPanelIndicator:
            {
                var indicator = new PanelRuntimeIndicator(
                    Guid.NewGuid(),
                    string.IsNullOrWhiteSpace(action.Title) ? rule.Name : action.Title.Trim(),
                    string.IsNullOrWhiteSpace(action.Message)
                        ? (string.IsNullOrWhiteSpace(action.Label) ? "Automação executada." : action.Label.Trim())
                        : action.Message.Trim(),
                    now,
                    IsAlert: action.IsSensitiveAction);

                RecordIndicator(indicator);
                return Result<string>.Success($"Indicador exibido: {indicator.Title}");
            }

            case AutomationActionType.SendNotification:
            {
                var request = new PushMobileNotificationRequest
                {
                    NotificationId = Guid.NewGuid(),
                    SourceDeviceId = _localDevice.DeviceId,
                    AppName = "NEXUS Automate",
                    PackageName = "nexus.automate",
                    Title = string.IsNullOrWhiteSpace(action.Title) ? rule.Name : action.Title.Trim(),
                    Body = string.IsNullOrWhiteSpace(action.Message)
                        ? $"Regra '{rule.Name}' acionada em {now.LocalDateTime:HH:mm:ss}."
                        : action.Message.Trim(),
                    Urgency = action.IsSensitiveAction ? NotificationUrgency.High : NotificationUrgency.Normal,
                    CreatedAtUtc = now
                };

                var notifyRes = await _notifyService.IngestAsync(request, cancellationToken).ConfigureAwait(false);
                return notifyRes.IsSuccess
                    ? Result<string>.Success($"Notificação '{request.Title}' enviada.")
                    : Result<string>.Failure(notifyRes.Error!);
            }

            case AutomationActionType.ExecuteMediaCommand:
            {
                var mediaCmd = new MediaControlCommandRequest
                {
                    CommandId = Guid.NewGuid(),
                    SourceDeviceId = _localDevice.DeviceId,
                    Action = action.MediaAction ?? MediaControlAction.PlayPause,
                    VolumePercent = action.VolumePercent,
                    RequestedAtUtc = now
                };

                var mediaRes = await _mediaService.ExecuteCommandAsync(mediaCmd, cancellationToken).ConfigureAwait(false);
                return mediaRes.IsSuccess
                    ? Result<string>.Success($"Mídia executada: {mediaCmd.Action}.")
                    : Result<string>.Failure(mediaRes.Error!);
            }

            case AutomationActionType.ExecuteControlCommand:
            {
                var ctrlCmd = new RemoteControlCommandRequest
                {
                    CommandId = Guid.NewGuid(),
                    SourceDeviceId = _localDevice.DeviceId,
                    Action = action.ControlAction ?? RemoteControlActionType.VolumeMuteToggle,
                    HotkeyId = action.HotkeyId,
                    VolumePercent = action.VolumePercent,
                    ConfirmedByUser = confirmedSensitive,
                    RequestedAtUtc = now
                };

                var ctrlRes = await _controlExecutor.ExecuteAsync(ctrlCmd, cancellationToken).ConfigureAwait(false);
                if (ctrlRes.IsFailure)
                {
                    return Result<string>.Failure(ctrlRes.Error!);
                }

                return ctrlRes.Value.Executed
                    ? Result<string>.Success(ctrlRes.Value.Message)
                    : Result<string>.Failure(NexusError.Forbidden(
                        ctrlRes.Value.Message,
                        ctrlRes.Value.ErrorCode ?? "CONTROL_ACTION_BLOCKED"));
            }

            case AutomationActionType.LaunchApprovedApp:
            {
                var shortcut = settings.ApprovedApps.FirstOrDefault(
                    a => string.Equals(a.ShortcutId, action.ShortcutId, StringComparison.OrdinalIgnoreCase));
                if (shortcut is null)
                {
                    return Result<string>.Failure(NexusError.Forbidden(
                        $"Aplicativo '{action.ShortcutId}' não está na allowlist aprovada.",
                        "APP_NOT_ALLOWLISTED"));
                }

                return await _appLauncher.LaunchApprovedAppAsync(shortcut, cancellationToken).ConfigureAwait(false);
            }

            case AutomationActionType.PrepareFavoriteControls:
            {
                var profileRes = await _panelLayoutService.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
                var profileName = profileRes.IsSuccess ? profileRes.Value.Name : "Padrão";
                var mediaNote = "Pronto";
                if (_mediaService.IsSupportedOnCurrentOs)
                {
                    var stateRes = await _mediaService.GetCurrentStateAsync(cancellationToken).ConfigureAwait(false);
                    if (stateRes.IsSuccess)
                    {
                        mediaNote = $"Mídia: {stateRes.Value.PlaybackStatus}";
                    }
                }

                var indicator = new PanelRuntimeIndicator(
                    Guid.NewGuid(),
                    string.IsNullOrWhiteSpace(action.Title) ? "Controles Favoritos Prontos" : action.Title.Trim(),
                    $"Perfil '{profileName}' • {mediaNote}",
                    now,
                    IsAlert: false);
                RecordIndicator(indicator);
                return Result<string>.Success(indicator.Message);
            }

            default:
                return Result<string>.Failure(NexusError.Validation(
                    $"Tipo de ação desconhecido: {action.ActionType}.",
                    code: "AUTO_ACTION_UNSUPPORTED"));
        }
    }

    private static string? FindMissingPermission(AutomationRule rule, NexusPermission callerPermissions)
    {
        foreach (var action in rule.Actions)
        {
            switch (action.ActionType)
            {
                case AutomationActionType.ExecuteControlCommand:
                    if ((callerPermissions & NexusPermission.ControlInput) != NexusPermission.ControlInput)
                    {
                        return "Permissão ControlInput ausente para comando de controle remoto.";
                    }

                    if (action.ControlAction.HasValue
                        && RemoteControlCommandRequest.IsHighImpactAction(action.ControlAction.Value)
                        && (callerPermissions & NexusPermission.SystemPower) != NexusPermission.SystemPower)
                    {
                        return "Permissão SystemPower ausente para ação crítica de energia.";
                    }
                    break;

                case AutomationActionType.LaunchApprovedApp:
                    if ((callerPermissions & NexusPermission.ControlInput) != NexusPermission.ControlInput)
                    {
                        return "Permissão ControlInput ausente para abrir aplicativo aprovado.";
                    }
                    break;

                case AutomationActionType.ExecuteMediaCommand:
                    if ((callerPermissions & NexusPermission.MediaControl) != NexusPermission.MediaControl)
                    {
                        return "Permissão MediaControl ausente para comando de mídia.";
                    }
                    break;

                case AutomationActionType.SendNotification:
                    if ((callerPermissions & NexusPermission.Notifications) != NexusPermission.Notifications)
                    {
                        return "Permissão Notifications ausente para encaminhar notificações.";
                    }
                    break;
            }
        }

        return null;
    }

    private bool EvaluateRuleConditions(
        AutomationRule rule,
        AutomationTriggerEvent triggerEvent,
        BatteryStateSnapshot battery,
        out string reason)
    {
        if (rule.Trigger == AutomationTriggerType.BatteryLevelThreshold
            && rule.BatteryThresholdPercent.HasValue
            && !rule.Conditions.Any(c => c.Operator is AutomationConditionOperator.BatteryAtOrBelow or AutomationConditionOperator.BatteryAtOrAbove))
        {
            if (!battery.IsAvailable || !battery.ChargePercent.HasValue)
            {
                reason = $"Bateria indisponível para limiar {rule.BatteryThresholdPercent.Value}% ({battery.StatusMessage}).";
                return false;
            }

            if (battery.ChargePercent.Value > rule.BatteryThresholdPercent.Value)
            {
                reason = $"Bateria em {battery.ChargePercent.Value}% (acima do limiar {rule.BatteryThresholdPercent.Value}%).";
                return false;
            }
        }

        if (rule.Conditions.Count == 0)
        {
            reason = "Sem condições adicionais.";
            return true;
        }

        var localTime = triggerEvent.OccurredAtUtc.ToLocalTime();
        var results = new List<(bool Passed, string Description)>(rule.Conditions.Count);

        foreach (var condition in rule.Conditions)
        {
            results.Add(EvaluateSingleCondition(condition, triggerEvent, battery, localTime));
        }

        bool overall = rule.ConditionLogic == AutomationLogicMode.All
            ? results.All(r => r.Passed)
            : results.Any(r => r.Passed);

        reason = overall
            ? "Condições atendidas."
            : $"Condições não atendidas ({string.Join("; ", results.Where(r => !r.Passed).Select(r => r.Description))}).";
        return overall;
    }

    private static (bool Passed, string Description) EvaluateSingleCondition(
        AutomationCondition condition,
        AutomationTriggerEvent ev,
        BatteryStateSnapshot battery,
        DateTimeOffset localTime)
    {
        switch (condition.Operator)
        {
            case AutomationConditionOperator.RequireConnected:
            {
                var connected = ev.IsConnected || ev.Trigger == AutomationTriggerType.ConnectedToComputer;
                return (connected, connected ? "Conectado" : "Dispositivo desconectado");
            }

            case AutomationConditionOperator.RequireDisconnected:
            {
                var disconnected = !ev.IsConnected;
                return (disconnected, disconnected ? "Desconectado" : "Dispositivo conectado");
            }

            case AutomationConditionOperator.RouteKindEquals:
            {
                var matched = string.Equals(ev.ActiveRouteKind, condition.ExpectedValue?.Trim(), StringComparison.OrdinalIgnoreCase);
                return (matched, $"Rota atual '{ev.ActiveRouteKind ?? "None"}' != '{condition.ExpectedValue}'");
            }

            case AutomationConditionOperator.BatteryAtOrBelow:
            {
                if (!battery.IsAvailable || !battery.ChargePercent.HasValue ||
                    !int.TryParse(condition.ExpectedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
                {
                    return (false, $"Bateria indisponível para <= {condition.ExpectedValue}%");
                }

                var ok = battery.ChargePercent.Value <= target;
                return (ok, $"Bateria {battery.ChargePercent.Value}% > {target}%");
            }

            case AutomationConditionOperator.BatteryAtOrAbove:
            {
                if (!battery.IsAvailable || !battery.ChargePercent.HasValue ||
                    !int.TryParse(condition.ExpectedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
                {
                    return (false, $"Bateria indisponível para >= {condition.ExpectedValue}%");
                }

                var ok = battery.ChargePercent.Value >= target;
                return (ok, $"Bateria {battery.ChargePercent.Value}% < {target}%");
            }

            case AutomationConditionOperator.IsChargingEquals:
            {
                if (!battery.IsAvailable || !battery.IsCharging.HasValue ||
                    !bool.TryParse(condition.ExpectedValue, out var expectedCharging))
                {
                    return (false, "Estado de carregamento indisponível");
                }

                var ok = battery.IsCharging.Value == expectedCharging;
                return (ok, $"Carregando={battery.IsCharging.Value} != {expectedCharging}");
            }

            case AutomationConditionOperator.TimeBetweenHours:
            {
                if (!TryParseHour(condition.ExpectedValue, out var startHour) ||
                    !TryParseHour(condition.SecondaryValue, out var endHour))
                {
                    return (false, "Janela horária inválida");
                }

                var hour = localTime.Hour;
                var inWindow = startHour <= endHour
                    ? hour >= startHour && hour <= endHour
                    : hour >= startHour || hour <= endHour;
                return (inWindow, $"Hora {hour:00}h fora de {startHour:00}h–{endHour:00}h");
            }

            case AutomationConditionOperator.DeviceIdEquals:
            {
                var ok = Guid.TryParse(condition.ExpectedValue, out var expectedId)
                         && ev.SourceDeviceId.HasValue
                         && ev.SourceDeviceId.Value == expectedId;
                return (ok, $"DeviceId != {condition.ExpectedValue}");
            }

            default:
                return (false, $"Operador não suportado: {condition.Operator}");
        }
    }

    private static bool TryParseHour(string? raw, out int hour)
    {
        hour = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var span = raw.Trim();
        var colon = span.IndexOf(':');
        if (colon > 0)
        {
            span = span[..colon];
        }

        return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)
               && hour >= 0 && hour <= 23;
    }

    private async Task<(AutomationExecutionOutcome Outcome, string Reason)?> TryAcquireExecutionQuotaAsync(
        AutomationRule rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _rateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (rule.CooldownSeconds > 0 && _lastSuccessByRule.TryGetValue(rule.RuleId, out var lastRun))
            {
                var elapsed = now - lastRun;
                if (elapsed < TimeSpan.FromSeconds(rule.CooldownSeconds))
                {
                    var remaining = Math.Ceiling((TimeSpan.FromSeconds(rule.CooldownSeconds) - elapsed).TotalSeconds);
                    return (AutomationExecutionOutcome.SkippedCooldown,
                        $"Cooldown ativo ({remaining:0}s restantes de {rule.CooldownSeconds}s).");
                }
            }

            if (!_executionWindowByRule.TryGetValue(rule.RuleId, out var window))
            {
                window = new List<DateTimeOffset>();
                _executionWindowByRule[rule.RuleId] = window;
            }

            var cutoff = now - TimeSpan.FromMinutes(1);
            window.RemoveAll(t => t <= cutoff);

            if (window.Count >= rule.MaxExecutionsPerMinute)
            {
                return (AutomationExecutionOutcome.SkippedRateLimit,
                    $"Limite por minuto atingido ({window.Count}/{rule.MaxExecutionsPerMinute} execuções nos últimos 60s).");
            }

            window.Add(now);
            _lastSuccessByRule[rule.RuleId] = now;
            return null;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    private async Task<BatteryStateSnapshot> ReadSafeBatteryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _batteryProvider.ReadBatteryStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new BatteryStateSnapshot
            {
                IsBatteryPresent = false,
                IsAvailable = false,
                StatusMessage = $"Erro ao ler bateria: {ex.Message}"
            };
        }
    }

    private async Task PersistAndBroadcastExecutionAsync(
        AutomationExecutionRecord record,
        CancellationToken cancellationToken)
    {
        await _executionRepository.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        ExecutionLogged?.Invoke(this, record);
    }

    private void RecordIndicator(PanelRuntimeIndicator indicator)
    {
        lock (_activeIndicators)
        {
            _activeIndicators.Insert(0, indicator);
            if (_activeIndicators.Count > MaxRuntimeIndicators)
            {
                _activeIndicators.RemoveRange(MaxRuntimeIndicators, _activeIndicators.Count - MaxRuntimeIndicators);
            }
        }

        IndicatorEmitted?.Invoke(this, indicator);
    }

    private static AutomationExecutionRecord CreateRecord(
        AutomationRule rule,
        AutomationTriggerEvent ev,
        AutomationExecutionOutcome outcome,
        string detail,
        long durationMs,
        DateTimeOffset executedAtUtc)
        => new()
        {
            ExecutionId = Guid.NewGuid(),
            RuleId = rule.RuleId,
            RuleName = rule.Name,
            Trigger = ev.Trigger,
            Outcome = outcome,
            Detail = detail,
            CorrelationId = ev.CorrelationId,
            DurationMs = durationMs,
            ExecutedAtUtc = executedAtUtc
        };

    private static AutomationEngineSettings CreateDefaultSettings()
    {
        var defaultApps = OperatingSystem.IsWindows()
            ? new ApprovedAppShortcut[]
            {
                new() { ShortcutId = "notepad", DisplayName = "Bloco de Notas", ExecutablePath = "notepad.exe" },
                new() { ShortcutId = "calc", DisplayName = "Calculadora", ExecutablePath = "calc.exe" }
            }
            : Array.Empty<ApprovedAppShortcut>();

        return new AutomationEngineSettings
        {
            GlobalAutomationsEnabled = true,
            EmergencyKillSwitchActive = false,
            MaxChainDepth = 4,
            ApprovedApps = defaultApps,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private void WireReactiveSources()
    {
        if (_realtimeChannel is not null)
        {
            _realtimeChannel.StateChanged += OnRealtimeStateChanged;
        }

        if (_routeOrchestrator is not null)
        {
            _routeOrchestrator.StatusChanged += OnRouteStatusChanged;
        }

        if (_transferManager is not null)
        {
            _transferManager.TransferUpdated += OnTransferUpdated;
        }
    }

    private void OnRealtimeStateChanged(object? sender, ConnectionStateSnapshot snapshot)
    {
        var previous = _lastConnectionState;
        _lastConnectionState = snapshot.State;

        if (snapshot.State == ConnectionState.Connected && previous != ConnectionState.Connected)
        {
            DispatchBackgroundTrigger(new AutomationTriggerEvent
            {
                Trigger = AutomationTriggerType.ConnectedToComputer,
                SourceDeviceId = snapshot.RemoteDeviceId ?? snapshot.LocalDeviceId,
                IsConnected = true,
                ActiveRouteKind = _routeOrchestrator?.CurrentStatus.RouteKind.ToString(),
                Detail = $"Conexão estabelecida via {snapshot.Transport}."
            });
        }
        else if (snapshot.State is ConnectionState.Disconnected or ConnectionState.Faulted
                 && previous == ConnectionState.Connected)
        {
            DispatchBackgroundTrigger(new AutomationTriggerEvent
            {
                Trigger = AutomationTriggerType.DisconnectedFromComputer,
                SourceDeviceId = snapshot.RemoteDeviceId ?? snapshot.LocalDeviceId,
                IsConnected = false,
                ActiveRouteKind = _routeOrchestrator?.CurrentStatus.RouteKind.ToString(),
                Detail = string.IsNullOrWhiteSpace(snapshot.Reason) ? "Conexão encerrada." : snapshot.Reason
            });
        }
    }

    private void OnRouteStatusChanged(object? sender, ConnectivityStatusReport status)
    {
        var routeKey = $"{status.State}:{status.RouteKind}:{status.ActiveEndpointId}";
        if (string.Equals(_lastRouteKey, routeKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastRouteKey = routeKey;
        if (status.State == ConnectionState.Connected)
        {
            DispatchBackgroundTrigger(new AutomationTriggerEvent
            {
                Trigger = AutomationTriggerType.NetworkRouteChanged,
                SourceDeviceId = status.RemoteDeviceId,
                IsConnected = true,
                ActiveRouteKind = status.RouteKind.ToString(),
                Detail = $"Rota ativa alterada para {status.RouteKind}."
            });
        }
    }

    private void OnTransferUpdated(object? sender, FileTransferRecord transfer)
    {
        var previous = _lastTransferStates.GetOrAdd(transfer.TransferId, TransferState.Pending);
        if (previous == transfer.State)
        {
            return;
        }

        _lastTransferStates[transfer.TransferId] = transfer.State;

        if (transfer.State == TransferState.InProgress && previous != TransferState.InProgress)
        {
            DispatchBackgroundTrigger(new AutomationTriggerEvent
            {
                Trigger = AutomationTriggerType.TransferStarted,
                SourceDeviceId = transfer.SourceDeviceId,
                IsConnected = true,
                ActiveRouteKind = _routeOrchestrator?.CurrentStatus.RouteKind.ToString(),
                TransferFileName = transfer.OriginalFileName,
                Detail = $"Transferência iniciada: {transfer.OriginalFileName}."
            });
        }
        else if (transfer.State == TransferState.Completed)
        {
            DispatchBackgroundTrigger(new AutomationTriggerEvent
            {
                Trigger = AutomationTriggerType.TransferCompleted,
                SourceDeviceId = transfer.SourceDeviceId,
                IsConnected = true,
                ActiveRouteKind = _routeOrchestrator?.CurrentStatus.RouteKind.ToString(),
                TransferFileName = transfer.OriginalFileName,
                Detail = $"Transferência concluída: {transfer.OriginalFileName}."
            });
        }
    }

    private void DispatchBackgroundTrigger(AutomationTriggerEvent triggerEvent)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EvaluateTriggerAsync(triggerEvent, NexusPermission.All, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background trigger dispatch failed for {Trigger}", triggerEvent.Trigger);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_realtimeChannel is not null)
        {
            _realtimeChannel.StateChanged -= OnRealtimeStateChanged;
        }

        if (_routeOrchestrator is not null)
        {
            _routeOrchestrator.StatusChanged -= OnRouteStatusChanged;
        }

        if (_transferManager is not null)
        {
            _transferManager.TransferUpdated -= OnTransferUpdated;
        }

        _settingsLock.Dispose();
        _rateLock.Dispose();
    }
}

