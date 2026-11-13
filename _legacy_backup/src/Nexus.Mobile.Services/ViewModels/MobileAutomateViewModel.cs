using System.Collections.ObjectModel;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Security;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

public sealed class MobileAutomateViewModel : MobileObservableObject, IDisposable
{
    private readonly IAutomationEngine _engine;
    private readonly IPanelLayoutService _panelService;
    private readonly IDeviceBatteryProvider _batteryProvider;
    private readonly IPanelAndAutomationClient _remoteClient;
    private readonly IRealtimeClientChannel _realtimeChannel;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    private string _statusMessage = "Nexus Automate pronto.";
    private string _batterySummary = "Verificando bateria...";
    private int? _batteryChargePercent;
    private string _androidLimitationNotice = string.Empty;
    private bool _isKillSwitchActive;
    private bool _isGlobalAutomationsEnabled = true;

    private Guid? _editingRuleId;
    private string _ruleName = string.Empty;
    private string _ruleDescription = string.Empty;
    private AutomationTriggerType _selectedTrigger = AutomationTriggerType.ConnectedToComputer;
    private int _batteryThresholdPercent = 20;
    private string _selectedRouteFilter = string.Empty;
    private AutomationLogicMode _selectedConditionLogic = AutomationLogicMode.All;
    private bool _requireConnectedCondition;
    private bool _useTimeWindowCondition;
    private int _windowStartHour = 22;
    private int _windowEndHour = 6;
    private AutomationActionType _selectedActionType = AutomationActionType.ActivatePanelProfile;
    private PanelProfile? _selectedTargetProfile;
    private string _actionTitle = string.Empty;
    private string _actionMessage = string.Empty;
    private MediaControlAction _selectedMediaAction = MediaControlAction.PlayPause;
    private RemoteControlActionType _selectedControlAction = RemoteControlActionType.VolumeMuteToggle;
    private ApprovedAppShortcut? _selectedShortcut;
    private int _cooldownSeconds = 10;
    private int _maxExecutionsPerMinute = 6;
    private bool _allowSensitiveExecution;
    private bool _isRuleEnabled = true;
    private AutomationRule? _pendingSensitiveApprovalRule;
    private string _sensitiveApprovalPrompt = string.Empty;

    public MobileAutomateViewModel(
        IAutomationEngine engine,
        IPanelLayoutService panelService,
        IDeviceBatteryProvider batteryProvider,
        IPanelAndAutomationClient remoteClient,
        IRealtimeClientChannel realtimeChannel)
    {
        _engine = engine;
        _panelService = panelService;
        _batteryProvider = batteryProvider;
        _remoteClient = remoteClient;
        _realtimeChannel = realtimeChannel;

        InitializeCommand = new MobileAsyncCommand(InitializeAsync);
        RefreshBatteryCommand = new MobileAsyncCommand(RefreshBatteryAsync);
        SaveRuleCommand = new MobileAsyncCommand(SaveRuleAsync);
        ClearEditorCommand = new MobileAsyncCommand(_ =>
        {
            ResetEditor();
            return Task.CompletedTask;
        });

        EditRuleCommand = new MobileAsyncCommand<AutomationRule>((r, _) =>
        {
            if (r is not null) LoadRuleIntoEditor(r);
            return Task.CompletedTask;
        });
        DuplicateRuleCommand = new MobileAsyncCommand<AutomationRule>(DuplicateRuleAsync);
        ToggleRuleEnabledCommand = new MobileAsyncCommand<AutomationRule>(ToggleRuleEnabledAsync);
        DeleteRuleCommand = new MobileAsyncCommand<AutomationRule>(DeleteRuleAsync);
        RunRuleManuallyCommand = new MobileAsyncCommand<AutomationRule>(RunRuleManuallyAsync);
        ApproveAndRunSensitiveRuleCommand = new MobileAsyncCommand(ApproveAndRunSensitiveRuleAsync);
        DismissSensitiveApprovalCommand = new MobileAsyncCommand(_ =>
        {
            PendingSensitiveApprovalRule = null;
            return Task.CompletedTask;
        });

        ToggleKillSwitchCommand = new MobileAsyncCommand(ToggleKillSwitchAsync);
        ToggleGlobalAutomationsCommand = new MobileAsyncCommand(ToggleGlobalAutomationsAsync);
        SyncRemoteAutomationsCommand = new MobileAsyncCommand(SyncRemoteAutomationsAsync);
        ClearHistoryCommand = new MobileAsyncCommand(ClearHistoryAsync);

        ApplyConnectedProfileTemplateCommand = new MobileAsyncCommand(_ => ApplyStarterTemplateAsync("connect-profile"));
        ApplyTransferIndicatorTemplateCommand = new MobileAsyncCommand(_ => ApplyStarterTemplateAsync("transfer-indicator"));
        ApplyDisconnectNotifyTemplateCommand = new MobileAsyncCommand(_ => ApplyStarterTemplateAsync("disconnect-notify"));
        ApplyLowBatteryTemplateCommand = new MobileAsyncCommand(_ => ApplyStarterTemplateAsync("low-battery"));

        _engine.ExecutionLogged += OnEngineExecutionLogged;
        _engine.SettingsChanged += OnEngineSettingsChanged;
        _realtimeChannel.AutomationExecutionReceived += OnRemoteExecutionReceived;
    }

    public ObservableCollection<AutomationRule> Rules { get; } = new();
    public ObservableCollection<AutomationExecutionRecord> ExecutionHistory { get; } = new();
    public ObservableCollection<PanelProfile> AvailableProfiles { get; } = new();
    public ObservableCollection<ApprovedAppShortcut> ApprovedApps { get; } = new();

    public IReadOnlyList<AutomationTriggerType> AvailableTriggers { get; } = Enum.GetValues<AutomationTriggerType>();
    public IReadOnlyList<AutomationActionType> AvailableActionTypes { get; } = Enum.GetValues<AutomationActionType>();
    public IReadOnlyList<AutomationLogicMode> AvailableLogicModes { get; } = Enum.GetValues<AutomationLogicMode>();
    public IReadOnlyList<MediaControlAction> AvailableMediaActions { get; } = Enum.GetValues<MediaControlAction>();
    public IReadOnlyList<RemoteControlActionType> AvailableControlActions { get; } = Enum.GetValues<RemoteControlActionType>();
    public IReadOnlyList<string> AvailableRouteFilters { get; } = ["", "LocalLan", "TailscaleVpn", "ManualDirect"];

    public MobileAsyncCommand InitializeCommand { get; }
    public MobileAsyncCommand RefreshBatteryCommand { get; }
    public MobileAsyncCommand SaveRuleCommand { get; }
    public MobileAsyncCommand ClearEditorCommand { get; }
    public MobileAsyncCommand<AutomationRule> EditRuleCommand { get; }
    public MobileAsyncCommand<AutomationRule> DuplicateRuleCommand { get; }
    public MobileAsyncCommand<AutomationRule> ToggleRuleEnabledCommand { get; }
    public MobileAsyncCommand<AutomationRule> DeleteRuleCommand { get; }
    public MobileAsyncCommand<AutomationRule> RunRuleManuallyCommand { get; }
    public MobileAsyncCommand ApproveAndRunSensitiveRuleCommand { get; }
    public MobileAsyncCommand DismissSensitiveApprovalCommand { get; }
    public MobileAsyncCommand ToggleKillSwitchCommand { get; }
    public MobileAsyncCommand ToggleGlobalAutomationsCommand { get; }
    public MobileAsyncCommand SyncRemoteAutomationsCommand { get; }
    public MobileAsyncCommand ClearHistoryCommand { get; }
    public MobileAsyncCommand ApplyConnectedProfileTemplateCommand { get; }
    public MobileAsyncCommand ApplyTransferIndicatorTemplateCommand { get; }
    public MobileAsyncCommand ApplyDisconnectNotifyTemplateCommand { get; }
    public MobileAsyncCommand ApplyLowBatteryTemplateCommand { get; }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string BatterySummary { get => _batterySummary; private set => SetProperty(ref _batterySummary, value); }
    public int? BatteryChargePercent { get => _batteryChargePercent; private set => SetProperty(ref _batteryChargePercent, value); }
    public string AndroidLimitationNotice { get => _androidLimitationNotice; private set => SetProperty(ref _androidLimitationNotice, value); }
    public bool IsKillSwitchActive { get => _isKillSwitchActive; private set => SetProperty(ref _isKillSwitchActive, value); }
    public bool IsGlobalAutomationsEnabled { get => _isGlobalAutomationsEnabled; private set => SetProperty(ref _isGlobalAutomationsEnabled, value); }
    public bool IsEditingExistingRule => _editingRuleId.HasValue;

    public string RuleName { get => _ruleName; set => SetProperty(ref _ruleName, value); }
    public string RuleDescription { get => _ruleDescription; set => SetProperty(ref _ruleDescription, value); }
    public AutomationTriggerType SelectedTrigger { get => _selectedTrigger; set => SetProperty(ref _selectedTrigger, value); }
    public int BatteryThresholdPercent { get => _batteryThresholdPercent; set => SetProperty(ref _batteryThresholdPercent, Math.Clamp(value, 1, 100)); }
    public string SelectedRouteFilter { get => _selectedRouteFilter; set => SetProperty(ref _selectedRouteFilter, value ?? string.Empty); }
    public AutomationLogicMode SelectedConditionLogic { get => _selectedConditionLogic; set => SetProperty(ref _selectedConditionLogic, value); }
    public bool RequireConnectedCondition { get => _requireConnectedCondition; set => SetProperty(ref _requireConnectedCondition, value); }
    public bool UseTimeWindowCondition { get => _useTimeWindowCondition; set => SetProperty(ref _useTimeWindowCondition, value); }
    public int WindowStartHour { get => _windowStartHour; set => SetProperty(ref _windowStartHour, Math.Clamp(value, 0, 23)); }
    public int WindowEndHour { get => _windowEndHour; set => SetProperty(ref _windowEndHour, Math.Clamp(value, 0, 23)); }
    public AutomationActionType SelectedActionType { get => _selectedActionType; set => SetProperty(ref _selectedActionType, value); }
    public PanelProfile? SelectedTargetProfile { get => _selectedTargetProfile; set => SetProperty(ref _selectedTargetProfile, value); }
    public string ActionTitle { get => _actionTitle; set => SetProperty(ref _actionTitle, value); }
    public string ActionMessage { get => _actionMessage; set => SetProperty(ref _actionMessage, value); }
    public MediaControlAction SelectedMediaAction { get => _selectedMediaAction; set => SetProperty(ref _selectedMediaAction, value); }
    public RemoteControlActionType SelectedControlAction { get => _selectedControlAction; set => SetProperty(ref _selectedControlAction, value); }
    public ApprovedAppShortcut? SelectedShortcut { get => _selectedShortcut; set => SetProperty(ref _selectedShortcut, value); }
    public int CooldownSeconds { get => _cooldownSeconds; set => SetProperty(ref _cooldownSeconds, Math.Clamp(value, 0, 86_400)); }
    public int MaxExecutionsPerMinute { get => _maxExecutionsPerMinute; set => SetProperty(ref _maxExecutionsPerMinute, Math.Clamp(value, 1, 120)); }
    public bool AllowSensitiveExecution { get => _allowSensitiveExecution; set => SetProperty(ref _allowSensitiveExecution, value); }
    public bool IsRuleEnabled { get => _isRuleEnabled; set => SetProperty(ref _isRuleEnabled, value); }

    public AutomationRule? PendingSensitiveApprovalRule
    {
        get => _pendingSensitiveApprovalRule;
        private set
        {
            if (SetProperty(ref _pendingSensitiveApprovalRule, value))
            {
                OnPropertyChanged(nameof(HasPendingSensitiveApproval));
            }
        }
    }

    public bool HasPendingSensitiveApproval => _pendingSensitiveApprovalRule is not null;
    public string SensitiveApprovalPrompt { get => _sensitiveApprovalPrompt; private set => SetProperty(ref _sensitiveApprovalPrompt, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settingsRes = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsSuccess)
        {
            ApplySettings(settingsRes.Value);
        }

        var profilesRes = await _panelService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (profilesRes.IsSuccess)
        {
            AvailableProfiles.Clear();
            foreach (var p in profilesRes.Value)
            {
                AvailableProfiles.Add(p);
            }

            SelectedTargetProfile ??= AvailableProfiles.FirstOrDefault(p => p.IsActive) ?? AvailableProfiles.FirstOrDefault();
        }

        await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = $"{Rules.Count} regra(s) configurada(s) · {ExecutionHistory.Count} registro(s) no histórico.";
    }

    private async Task RefreshBatteryAsync(CancellationToken cancellationToken)
    {
        var battery = await _batteryProvider.ReadBatteryStateAsync(cancellationToken).ConfigureAwait(false);
        BatteryChargePercent = battery.ChargePercent;
        BatterySummary = battery.IsAvailable && battery.ChargePercent.HasValue
            ? $"Bateria: {battery.ChargePercent.Value}% · {(battery.IsCharging == true ? "Carregando" : "Em uso")}"
            : battery.StatusMessage;
    }

    private async Task SaveRuleAsync(CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(RuleName)
            ? $"Regra {SelectedTrigger} -> {SelectedActionType}"
            : RuleName.Trim();

        var conditions = new List<AutomationCondition>();
        if (RequireConnectedCondition)
        {
            conditions.Add(new AutomationCondition { Operator = AutomationConditionOperator.RequireConnected, ExpectedValue = "true" });
        }

        if (UseTimeWindowCondition)
        {
            conditions.Add(new AutomationCondition
            {
                Operator = AutomationConditionOperator.TimeBetweenHours,
                ExpectedValue = WindowStartHour.ToString(),
                SecondaryValue = WindowEndHour.ToString()
            });
        }

        var action = BuildActionFromEditor();
        var rule = new AutomationRule
        {
            RuleId = _editingRuleId ?? Guid.NewGuid(),
            Name = name,
            Description = RuleDescription?.Trim() ?? string.Empty,
            IsEnabled = IsRuleEnabled,
            Trigger = SelectedTrigger,
            BatteryThresholdPercent = SelectedTrigger == AutomationTriggerType.BatteryLevelThreshold ? BatteryThresholdPercent : null,
            ExpectedRouteFilter = string.IsNullOrWhiteSpace(SelectedRouteFilter) ? null : SelectedRouteFilter.Trim(),
            ConditionLogic = SelectedConditionLogic,
            Conditions = conditions,
            Actions = [action],
            CooldownSeconds = CooldownSeconds,
            MaxExecutionsPerMinute = MaxExecutionsPerMinute,
            RequireSensitiveAuthorization = true,
            IsSensitiveAuthorized = AllowSensitiveExecution
        };

        var res = await _engine.SaveRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        ResetEditor();
        StatusMessage = $"Regra '{res.Value.Name}' salva no SQLite.";
    }

    private AutomationAction BuildActionFromEditor()
    {
        var targetProfile = SelectedTargetProfile ?? AvailableProfiles.FirstOrDefault();
        var isSensitive = SelectedActionType == AutomationActionType.LaunchApprovedApp
                          || (SelectedActionType == AutomationActionType.ExecuteControlCommand
                              && RemoteControlCommandRequest.IsHighImpactAction(SelectedControlAction));

        return new AutomationAction
        {
            ActionId = Guid.NewGuid(),
            ActionType = SelectedActionType,
            Label = SelectedActionType.ToString(),
            TargetProfileId = SelectedActionType == AutomationActionType.ActivatePanelProfile ? targetProfile?.ProfileId : null,
            Title = string.IsNullOrWhiteSpace(ActionTitle) ? null : ActionTitle.Trim(),
            Message = string.IsNullOrWhiteSpace(ActionMessage) ? null : ActionMessage.Trim(),
            MediaAction = SelectedActionType == AutomationActionType.ExecuteMediaCommand ? SelectedMediaAction : null,
            ControlAction = SelectedActionType == AutomationActionType.ExecuteControlCommand ? SelectedControlAction : null,
            ShortcutId = SelectedActionType == AutomationActionType.LaunchApprovedApp ? SelectedShortcut?.ShortcutId : null,
            IsSensitiveAction = isSensitive
        };
    }

    private async Task DuplicateRuleAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null)
        {
            return;
        }

        var copy = rule with
        {
            RuleId = Guid.NewGuid(),
            Name = $"{rule.Name} (Cópia)",
            IsSensitiveAuthorized = false,
            LastExecutedAtUtc = null
        };

        var saved = await _engine.SaveRuleAsync(copy, cancellationToken).ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Regra duplicada como '{saved.Value.Name}'.";
        }
    }

    private async Task ToggleRuleEnabledAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null)
        {
            return;
        }

        var toggled = await _engine.SetRuleEnabledAsync(rule.RuleId, !rule.IsEnabled, cancellationToken).ConfigureAwait(false);
        if (toggled.IsFailure)
        {
            StatusMessage = toggled.Error!.Message;
            return;
        }

        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = toggled.Value.IsEnabled
            ? $"Regra '{toggled.Value.Name}' ativada."
            : $"Regra '{toggled.Value.Name}' desativada imediatamente.";
    }

    private async Task DeleteRuleAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null)
        {
            return;
        }

        var res = await _engine.DeleteRuleAsync(rule.RuleId, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Regra '{rule.Name}' excluída.";
        }
        else
        {
            StatusMessage = res.Error!.Message;
        }
    }

    private async Task RunRuleManuallyAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null)
        {
            return;
        }

        var res = await _engine.TriggerRuleManuallyAsync(rule.RuleId, NexusPermission.All, approvedSensitiveExecution: false, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (res.Value.Outcome == AutomationExecutionOutcome.BlockedAuthorization)
        {
            PendingSensitiveApprovalRule = rule;
            SensitiveApprovalPrompt = $"A regra '{rule.Name}' executa ação sensível. Confirmar execução imediata?";
        }

        StatusMessage = $"{res.Value.RuleName}: {res.Value.Outcome} — {res.Value.Detail}";
    }

    private async Task ApproveAndRunSensitiveRuleAsync(CancellationToken cancellationToken)
    {
        if (PendingSensitiveApprovalRule is null)
        {
            return;
        }

        var rule = PendingSensitiveApprovalRule;
        PendingSensitiveApprovalRule = null;

        var res = await _engine.TriggerRuleManuallyAsync(rule.RuleId, NexusPermission.All, approvedSensitiveExecution: true, cancellationToken).ConfigureAwait(false);
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = res.IsSuccess
            ? $"Execução autorizada: {res.Value.Outcome} — {res.Value.Detail}"
            : res.Error!.Message;
    }

    private async Task ToggleKillSwitchAsync(CancellationToken cancellationToken)
    {
        var updated = await _engine.SetEmergencyKillSwitchAsync(!IsKillSwitchActive, cancellationToken).ConfigureAwait(false);
        if (updated.IsFailure)
        {
            StatusMessage = updated.Error!.Message;
            return;
        }

        ApplySettings(updated.Value);
        StatusMessage = updated.Value.EmergencyKillSwitchActive
            ? "Kill Switch de emergência ATIVADO — todas as automações foram interrompidas."
            : "Kill Switch desativado.";
    }

    private async Task ToggleGlobalAutomationsAsync(CancellationToken cancellationToken)
    {
        var cur = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (cur.IsFailure)
        {
            return;
        }

        var saved = await _engine.SaveSettingsAsync(cur.Value with { GlobalAutomationsEnabled = !cur.Value.GlobalAutomationsEnabled }, cancellationToken).ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            ApplySettings(saved.Value);
            StatusMessage = saved.Value.GlobalAutomationsEnabled ? "Automações globais ativadas." : "Automações globais pausadas.";
        }
    }

    private async Task SyncRemoteAutomationsAsync(CancellationToken cancellationToken)
    {
        var rulesRes = await _remoteClient.GetRemoteRulesAsync(cancellationToken).ConfigureAwait(false);
        var histRes = await _remoteClient.GetRemoteHistoryAsync(50, cancellationToken).ConfigureAwait(false);

        StatusMessage = rulesRes.IsSuccess
            ? $"Sincronizado com host PC: {rulesRes.Value.Count} regra(s) remotas e {(histRes.IsSuccess ? histRes.Value.Count : 0)} execuções."
            : $"Sincronização remota indisponível: {rulesRes.Error!.Message}";
    }

    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        var cleared = await _engine.ClearExecutionHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (cleared.IsSuccess)
        {
            ExecutionHistory.Clear();
            StatusMessage = $"{cleared.Value} registro(s) limpo(s) do histórico.";
        }
    }

    private Task ApplyStarterTemplateAsync(string templateKey)
    {
        _editingRuleId = null;
        OnPropertyChanged(nameof(IsEditingExistingRule));
        var firstProfile = AvailableProfiles.FirstOrDefault();

        switch (templateKey)
        {
            case "connect-profile":
                RuleName = "Ao conectar ao PC ativar perfil";
                RuleDescription = "Ativa automaticamente o perfil principal do painel quando o NEXUS conecta ao Windows.";
                SelectedTrigger = AutomationTriggerType.ConnectedToComputer;
                SelectedActionType = AutomationActionType.ActivatePanelProfile;
                SelectedTargetProfile = firstProfile;
                RequireConnectedCondition = false;
                break;

            case "transfer-indicator":
                RuleName = "Indicador ao iniciar transferência";
                RuleDescription = "Mostra alerta visual no painel sempre que uma transferência de arquivo começa.";
                SelectedTrigger = AutomationTriggerType.TransferStarted;
                SelectedActionType = AutomationActionType.ShowPanelIndicator;
                ActionTitle = "Transferência em andamento";
                ActionMessage = "Envio ou recebimento de arquivo ativo no NEXUS Transfer.";
                break;

            case "disconnect-notify":
                RuleName = "Aviso de desconexão do PC";
                RuleDescription = "Envia notificação local quando a conexão com o computador é encerrada.";
                SelectedTrigger = AutomationTriggerType.DisconnectedFromComputer;
                SelectedActionType = AutomationActionType.SendNotification;
                ActionTitle = "NEXUS desconectado";
                ActionMessage = "Conexão com o host Windows foi encerrada.";
                break;

            case "low-battery":
                RuleName = "Alerta de bateria baixa";
                RuleDescription = "Exibe aviso quando o nível real da bateria atinge o limiar configurado.";
                SelectedTrigger = AutomationTriggerType.BatteryLevelThreshold;
                BatteryThresholdPercent = 20;
                SelectedActionType = AutomationActionType.ShowPanelIndicator;
                ActionTitle = "Bateria baixa";
                ActionMessage = "Nível de bateria atingiu 20% ou menos.";
                break;
        }

        StatusMessage = $"Modelo '{RuleName}' carregado no editor.";
        return Task.CompletedTask;
    }

    private void LoadRuleIntoEditor(AutomationRule rule)
    {
        _editingRuleId = rule.RuleId;
        OnPropertyChanged(nameof(IsEditingExistingRule));

        RuleName = rule.Name;
        RuleDescription = rule.Description;
        SelectedTrigger = rule.Trigger;
        BatteryThresholdPercent = rule.BatteryThresholdPercent ?? 20;
        SelectedRouteFilter = rule.ExpectedRouteFilter ?? string.Empty;
        SelectedConditionLogic = rule.ConditionLogic;
        CooldownSeconds = rule.CooldownSeconds;
        MaxExecutionsPerMinute = rule.MaxExecutionsPerMinute;
        AllowSensitiveExecution = rule.IsSensitiveAuthorized;
        IsRuleEnabled = rule.IsEnabled;

        RequireConnectedCondition = rule.Conditions.Any(c => c.Operator == AutomationConditionOperator.RequireConnected);
        var timeCond = rule.Conditions.FirstOrDefault(c => c.Operator == AutomationConditionOperator.TimeBetweenHours);
        UseTimeWindowCondition = timeCond is not null;
        if (timeCond is not null)
        {
            if (int.TryParse(timeCond.ExpectedValue, out var s)) WindowStartHour = s;
            if (int.TryParse(timeCond.SecondaryValue, out var e)) WindowEndHour = e;
        }

        var firstAction = rule.Actions.FirstOrDefault();
        if (firstAction is not null)
        {
            SelectedActionType = firstAction.ActionType;
            ActionTitle = firstAction.Title ?? string.Empty;
            ActionMessage = firstAction.Message ?? string.Empty;
            if (firstAction.TargetProfileId.HasValue)
            {
                SelectedTargetProfile = AvailableProfiles.FirstOrDefault(p => p.ProfileId == firstAction.TargetProfileId.Value);
            }

            if (firstAction.MediaAction.HasValue) SelectedMediaAction = firstAction.MediaAction.Value;
            if (firstAction.ControlAction.HasValue) SelectedControlAction = firstAction.ControlAction.Value;
            if (!string.IsNullOrWhiteSpace(firstAction.ShortcutId))
            {
                SelectedShortcut = ApprovedApps.FirstOrDefault(a => a.ShortcutId == firstAction.ShortcutId);
            }
        }

        StatusMessage = $"Editando regra '{rule.Name}'.";
    }

    private void ResetEditor()
    {
        _editingRuleId = null;
        OnPropertyChanged(nameof(IsEditingExistingRule));
        RuleName = string.Empty;
        RuleDescription = string.Empty;
        ActionTitle = string.Empty;
        ActionMessage = string.Empty;
        RequireConnectedCondition = false;
        UseTimeWindowCondition = false;
        AllowSensitiveExecution = false;
        IsRuleEnabled = true;
    }

    private async Task ReloadRulesAndHistoryAsync(CancellationToken cancellationToken)
    {
        var rulesRes = await _engine.GetRulesAsync(cancellationToken).ConfigureAwait(false);
        if (rulesRes.IsSuccess)
        {
            Rules.Clear();
            foreach (var r in rulesRes.Value)
            {
                Rules.Add(r);
            }
        }

        var histRes = await _engine.GetExecutionHistoryAsync(60, cancellationToken).ConfigureAwait(false);
        if (histRes.IsSuccess)
        {
            ExecutionHistory.Clear();
            foreach (var item in histRes.Value)
            {
                ExecutionHistory.Add(item);
            }
        }
    }

    private void ApplySettings(AutomationEngineSettings settings)
    {
        IsKillSwitchActive = settings.EmergencyKillSwitchActive;
        IsGlobalAutomationsEnabled = settings.GlobalAutomationsEnabled;
        AndroidLimitationNotice = settings.AndroidBackgroundLimitationNote;

        ApprovedApps.Clear();
        foreach (var app in settings.ApprovedApps)
        {
            ApprovedApps.Add(app);
        }

        SelectedShortcut ??= ApprovedApps.FirstOrDefault();
    }

    private void OnEngineExecutionLogged(object? sender, AutomationExecutionRecord record)
    {
        RunOnUi(() =>
        {
            ExecutionHistory.Insert(0, record);
            while (ExecutionHistory.Count > 60)
            {
                ExecutionHistory.RemoveAt(ExecutionHistory.Count - 1);
            }
        });
    }

    private void OnEngineSettingsChanged(object? sender, AutomationEngineSettings settings)
        => RunOnUi(() => ApplySettings(settings));

    private void OnRemoteExecutionReceived(object? sender, AutomationExecutionRecord record)
        => OnEngineExecutionLogged(sender, record);

    private void RunOnUi(Action action)
    {
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    public void Dispose()
    {
        _engine.ExecutionLogged -= OnEngineExecutionLogged;
        _engine.SettingsChanged -= OnEngineSettingsChanged;
        _realtimeChannel.AutomationExecutionReceived -= OnRemoteExecutionReceived;
    }
}
