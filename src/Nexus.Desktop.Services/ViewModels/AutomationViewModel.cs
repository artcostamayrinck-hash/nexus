using System.Collections.ObjectModel;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Security;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class AutomationViewModel : ObservableObject, IDisposable
{
    private readonly IPanelLayoutService _panelService;
    private readonly IAutomationEngine _engine;
    private readonly IDeviceBatteryProvider _batteryProvider;
    private readonly IPanelAndAutomationClient _remoteClient;
    private readonly IRealtimeClientChannel _realtimeChannel;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    private string _statusMessage = "Painel & Automações NEXUS prontos.";
    private string _hostBatterySummary = "Verificando bateria do computador...";
    private string _androidLimitationsNote = string.Empty;
    private bool _isKillSwitchActive;
    private bool _isGlobalAutomationsEnabled = true;

    private PanelProfile? _activeProfile;
    private string _newProfileName = string.Empty;
    private PanelThemePreset _selectedTheme = PanelThemePreset.MidnightNavy;
    private PanelAccentColor _selectedAccent = PanelAccentColor.Cyan;
    private int _selectedGridColumns = 2;
    private PanelWidgetType _selectedWidgetToAdd = PanelWidgetType.ConnectionStatus;
    private PanelWidgetSize _selectedWidgetSize = PanelWidgetSize.Compact1x1;
    private string _newWidgetTitle = string.Empty;

    private string _newShortcutId = string.Empty;
    private string _newShortcutName = string.Empty;
    private string _newShortcutPath = string.Empty;
    private bool _newShortcutRequireConfirmation = true;

    private Guid? _editingRuleId;
    private string _ruleName = string.Empty;
    private string _ruleDescription = string.Empty;
    private AutomationTriggerType _selectedTrigger = AutomationTriggerType.ConnectedToComputer;
    private int _batteryThresholdPercent = 20;
    private string _selectedRouteFilter = string.Empty;
    private bool _requireConnectedCondition;
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
    private AutomationRule? _pendingSensitiveRule;
    private string _pendingSensitivePrompt = string.Empty;

    public AutomationViewModel(
        IPanelLayoutService panelService,
        IAutomationEngine engine,
        IDeviceBatteryProvider batteryProvider,
        IPanelAndAutomationClient remoteClient,
        IRealtimeClientChannel realtimeChannel)
    {
        _panelService = panelService;
        _engine = engine;
        _batteryProvider = batteryProvider;
        _remoteClient = remoteClient;
        _realtimeChannel = realtimeChannel;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        ToggleKillSwitchCommand = new AsyncRelayCommand(ToggleKillSwitchAsync);
        ToggleGlobalAutomationsCommand = new AsyncRelayCommand(ToggleGlobalAutomationsAsync);

        CreateProfileCommand = new AsyncRelayCommand(CreateProfileAsync);
        ActivateProfileCommand = new AsyncRelayCommand<PanelProfile>(ActivateProfileAsync);
        DeleteProfileCommand = new AsyncRelayCommand<PanelProfile>(DeleteProfileAsync);
        ApplyAppearanceCommand = new AsyncRelayCommand(ApplyAppearanceAsync);
        AddWidgetCommand = new AsyncRelayCommand(AddWidgetAsync);
        RemoveWidgetCommand = new AsyncRelayCommand<PanelWidgetItem>(RemoveWidgetAsync);
        CycleWidgetSizeCommand = new AsyncRelayCommand<PanelWidgetItem>(CycleWidgetSizeAsync);
        MoveWidgetUpCommand = new AsyncRelayCommand<PanelWidgetItem>((w, ct) => MoveWidgetRelativeAsync(w, -1, ct));
        MoveWidgetDownCommand = new AsyncRelayCommand<PanelWidgetItem>((w, ct) => MoveWidgetRelativeAsync(w, 1, ct));
        SyncPanelRemoteCommand = new AsyncRelayCommand(SyncPanelRemoteAsync);

        AddApprovedAppCommand = new AsyncRelayCommand(AddApprovedAppAsync);
        RemoveApprovedAppCommand = new AsyncRelayCommand<ApprovedAppShortcut>(RemoveApprovedAppAsync);

        SaveRuleCommand = new AsyncRelayCommand(SaveRuleAsync);
        ClearRuleEditorCommand = new AsyncRelayCommand(_ =>
        {
            ResetRuleEditor();
            return Task.CompletedTask;
        });
        EditRuleCommand = new AsyncRelayCommand<AutomationRule>((r, _) =>
        {
            if (r is not null) LoadRuleIntoEditor(r);
            return Task.CompletedTask;
        });
        DuplicateRuleCommand = new AsyncRelayCommand<AutomationRule>(DuplicateRuleAsync);
        ToggleRuleEnabledCommand = new AsyncRelayCommand<AutomationRule>(ToggleRuleEnabledAsync);
        DeleteRuleCommand = new AsyncRelayCommand<AutomationRule>(DeleteRuleAsync);
        TestRunRuleCommand = new AsyncRelayCommand<AutomationRule>(TestRunRuleAsync);
        ConfirmSensitiveRuleCommand = new AsyncRelayCommand(ConfirmSensitiveRuleAsync);
        CancelSensitivePromptCommand = new AsyncRelayCommand(_ =>
        {
            PendingSensitiveRule = null;
            return Task.CompletedTask;
        });
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);

        _panelService.ActiveProfileChanged += OnActiveProfileChanged;
        _engine.ExecutionLogged += OnExecutionLogged;
        _engine.IndicatorEmitted += OnIndicatorEmitted;
        _engine.SettingsChanged += OnSettingsChanged;
        _realtimeChannel.PanelProfileUpdatedReceived += OnRemoteProfileUpdated;
        _realtimeChannel.AutomationExecutionReceived += OnRemoteExecutionReceived;
    }

    public ObservableCollection<PanelProfile> Profiles { get; } = [];
    public ObservableCollection<PanelWidgetItem> ActiveWidgets { get; } = [];
    public ObservableCollection<ApprovedAppShortcut> ApprovedApps { get; } = [];
    public ObservableCollection<AutomationRule> Rules { get; } = [];
    public ObservableCollection<AutomationExecutionRecord> ExecutionHistory { get; } = [];
    public ObservableCollection<PanelRuntimeIndicator> ActiveIndicators { get; } = [];

    public IReadOnlyList<PanelThemePreset> AvailableThemes { get; } = Enum.GetValues<PanelThemePreset>();
    public IReadOnlyList<PanelAccentColor> AvailableAccents { get; } = Enum.GetValues<PanelAccentColor>();
    public IReadOnlyList<int> AvailableGridColumns { get; } = [1, 2, 3, 4];
    public IReadOnlyList<PanelWidgetType> AvailableWidgetTypes { get; } = Enum.GetValues<PanelWidgetType>();
    public IReadOnlyList<PanelWidgetSize> AvailableWidgetSizes { get; } = Enum.GetValues<PanelWidgetSize>();
    public IReadOnlyList<AutomationTriggerType> AvailableTriggers { get; } = Enum.GetValues<AutomationTriggerType>();
    public IReadOnlyList<AutomationActionType> AvailableActionTypes { get; } = Enum.GetValues<AutomationActionType>();
    public IReadOnlyList<MediaControlAction> AvailableMediaActions { get; } = Enum.GetValues<MediaControlAction>();
    public IReadOnlyList<RemoteControlActionType> AvailableControlActions { get; } = Enum.GetValues<RemoteControlActionType>();
    public IReadOnlyList<string> AvailableRouteFilters { get; } = ["", "LocalLan", "TailscaleVpn", "ManualDirect"];

    public AsyncRelayCommand InitializeCommand { get; }
    public AsyncRelayCommand ToggleKillSwitchCommand { get; }
    public AsyncRelayCommand ToggleGlobalAutomationsCommand { get; }
    public AsyncRelayCommand CreateProfileCommand { get; }
    public AsyncRelayCommand<PanelProfile> ActivateProfileCommand { get; }
    public AsyncRelayCommand<PanelProfile> DeleteProfileCommand { get; }
    public AsyncRelayCommand ApplyAppearanceCommand { get; }
    public AsyncRelayCommand AddWidgetCommand { get; }
    public AsyncRelayCommand<PanelWidgetItem> RemoveWidgetCommand { get; }
    public AsyncRelayCommand<PanelWidgetItem> CycleWidgetSizeCommand { get; }
    public AsyncRelayCommand<PanelWidgetItem> MoveWidgetUpCommand { get; }
    public AsyncRelayCommand<PanelWidgetItem> MoveWidgetDownCommand { get; }
    public AsyncRelayCommand SyncPanelRemoteCommand { get; }
    public AsyncRelayCommand AddApprovedAppCommand { get; }
    public AsyncRelayCommand<ApprovedAppShortcut> RemoveApprovedAppCommand { get; }
    public AsyncRelayCommand SaveRuleCommand { get; }
    public AsyncRelayCommand ClearRuleEditorCommand { get; }
    public AsyncRelayCommand<AutomationRule> EditRuleCommand { get; }
    public AsyncRelayCommand<AutomationRule> DuplicateRuleCommand { get; }
    public AsyncRelayCommand<AutomationRule> ToggleRuleEnabledCommand { get; }
    public AsyncRelayCommand<AutomationRule> DeleteRuleCommand { get; }
    public AsyncRelayCommand<AutomationRule> TestRunRuleCommand { get; }
    public AsyncRelayCommand ConfirmSensitiveRuleCommand { get; }
    public AsyncRelayCommand CancelSensitivePromptCommand { get; }
    public AsyncRelayCommand ClearHistoryCommand { get; }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string HostBatterySummary { get => _hostBatterySummary; private set => SetProperty(ref _hostBatterySummary, value); }
    public string AndroidLimitationsNote { get => _androidLimitationsNote; private set => SetProperty(ref _androidLimitationsNote, value); }
    public bool IsKillSwitchActive { get => _isKillSwitchActive; private set => SetProperty(ref _isKillSwitchActive, value); }
    public bool IsGlobalAutomationsEnabled { get => _isGlobalAutomationsEnabled; private set => SetProperty(ref _isGlobalAutomationsEnabled, value); }
    public string ActiveProfileName => _activeProfile?.Name ?? "Padrão";

    public string NewProfileName { get => _newProfileName; set => SetProperty(ref _newProfileName, value); }
    public PanelThemePreset SelectedTheme { get => _selectedTheme; set => SetProperty(ref _selectedTheme, value); }
    public PanelAccentColor SelectedAccent { get => _selectedAccent; set => SetProperty(ref _selectedAccent, value); }
    public int SelectedGridColumns { get => _selectedGridColumns; set => SetProperty(ref _selectedGridColumns, Math.Clamp(value, 1, 4)); }
    public PanelWidgetType SelectedWidgetToAdd { get => _selectedWidgetToAdd; set => SetProperty(ref _selectedWidgetToAdd, value); }
    public PanelWidgetSize SelectedWidgetSize { get => _selectedWidgetSize; set => SetProperty(ref _selectedWidgetSize, value); }
    public string NewWidgetTitle { get => _newWidgetTitle; set => SetProperty(ref _newWidgetTitle, value); }

    public string NewShortcutId { get => _newShortcutId; set => SetProperty(ref _newShortcutId, value); }
    public string NewShortcutName { get => _newShortcutName; set => SetProperty(ref _newShortcutName, value); }
    public string NewShortcutPath { get => _newShortcutPath; set => SetProperty(ref _newShortcutPath, value); }
    public bool NewShortcutRequireConfirmation { get => _newShortcutRequireConfirmation; set => SetProperty(ref _newShortcutRequireConfirmation, value); }

    public string RuleName { get => _ruleName; set => SetProperty(ref _ruleName, value); }
    public string RuleDescription { get => _ruleDescription; set => SetProperty(ref _ruleDescription, value); }
    public AutomationTriggerType SelectedTrigger { get => _selectedTrigger; set => SetProperty(ref _selectedTrigger, value); }
    public int BatteryThresholdPercent { get => _batteryThresholdPercent; set => SetProperty(ref _batteryThresholdPercent, Math.Clamp(value, 1, 100)); }
    public string SelectedRouteFilter { get => _selectedRouteFilter; set => SetProperty(ref _selectedRouteFilter, value ?? string.Empty); }
    public bool RequireConnectedCondition { get => _requireConnectedCondition; set => SetProperty(ref _requireConnectedCondition, value); }
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

    public AutomationRule? PendingSensitiveRule
    {
        get => _pendingSensitiveRule;
        private set
        {
            if (SetProperty(ref _pendingSensitiveRule, value))
            {
                OnPropertyChanged(nameof(HasPendingSensitivePrompt));
            }
        }
    }

    public bool HasPendingSensitivePrompt => _pendingSensitiveRule is not null;
    public string PendingSensitivePrompt { get => _pendingSensitivePrompt; private set => SetProperty(ref _pendingSensitivePrompt, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var battery = await _batteryProvider.ReadBatteryStateAsync(cancellationToken).ConfigureAwait(false);
        HostBatterySummary = battery.StatusMessage;

        var settingsRes = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsSuccess)
        {
            ApplySettings(settingsRes.Value);
        }

        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);

        ActiveIndicators.Clear();
        foreach (var ind in _engine.ActiveIndicators)
        {
            ActiveIndicators.Add(ind);
        }

        StatusMessage = $"Painel '{ActiveProfileName}' carregado · {Rules.Count} regra(s) de automação.";
    }

    private async Task ToggleKillSwitchAsync(CancellationToken cancellationToken)
    {
        var res = await _engine.SetEmergencyKillSwitchAsync(!IsKillSwitchActive, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            ApplySettings(res.Value);
            StatusMessage = res.Value.EmergencyKillSwitchActive
                ? "Kill Switch de emergência ATIVADO — nenhuma automação será executada."
                : "Kill Switch desativado.";
        }
    }

    private async Task ToggleGlobalAutomationsAsync(CancellationToken cancellationToken)
    {
        var cur = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (cur.IsFailure) return;

        var res = await _engine.SaveSettingsAsync(cur.Value with { GlobalAutomationsEnabled = !cur.Value.GlobalAutomationsEnabled }, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            ApplySettings(res.Value);
            StatusMessage = res.Value.GlobalAutomationsEnabled ? "Automações globais ativadas." : "Automações globais desativadas.";
        }
    }

    private async Task CreateProfileAsync(CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"Perfil {Profiles.Count + 1}" : NewProfileName.Trim();
        var created = await _panelService.CreateProfileAsync(name, SelectedTheme, SelectedAccent, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            StatusMessage = created.Error!.Message;
            return;
        }

        NewProfileName = string.Empty;
        await _panelService.ActivateProfileAsync(created.Value.ProfileId, cancellationToken).ConfigureAwait(false);
        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = $"Perfil '{created.Value.Name}' criado e ativado.";
    }

    private async Task ActivateProfileAsync(PanelProfile? profile, CancellationToken cancellationToken)
    {
        if (profile is null) return;
        var res = await _panelService.ActivateProfileAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = $"Perfil '{res.Value.Name}' ativado e persistido.";
    }

    private async Task DeleteProfileAsync(PanelProfile? profile, CancellationToken cancellationToken)
    {
        if (profile is null) return;
        var res = await _panelService.DeleteProfileAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = $"Perfil '{profile.Name}' removido.";
    }

    private async Task ApplyAppearanceAsync(CancellationToken cancellationToken)
    {
        var res = await _panelService.UpdateAppearanceAsync(SelectedTheme, SelectedAccent, SelectedGridColumns, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        ApplyActiveProfile(res.Value);
        StatusMessage = $"Aparência salva ({SelectedTheme} · {SelectedAccent} · {SelectedGridColumns} col.).";
    }

    private async Task AddWidgetAsync(CancellationToken cancellationToken)
    {
        var title = string.IsNullOrWhiteSpace(NewWidgetTitle) ? null : NewWidgetTitle.Trim();
        var res = await _panelService.AddWidgetAsync(SelectedWidgetToAdd, title, SelectedWidgetSize, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        NewWidgetTitle = string.Empty;
        ApplyActiveProfile(res.Value);
        StatusMessage = $"Widget '{SelectedWidgetToAdd}' adicionado ao painel.";
    }

    private async Task RemoveWidgetAsync(PanelWidgetItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        var res = await _panelService.RemoveWidgetAsync(item.WidgetId, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            ApplyActiveProfile(res.Value);
            StatusMessage = $"Widget '{item.Title}' removido.";
        }
    }

    private async Task CycleWidgetSizeAsync(PanelWidgetItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        var next = item.Size switch
        {
            PanelWidgetSize.Compact1x1 => PanelWidgetSize.Wide2x1,
            PanelWidgetSize.Wide2x1 => PanelWidgetSize.Tall1x2,
            PanelWidgetSize.Tall1x2 => PanelWidgetSize.Large2x2,
            _ => PanelWidgetSize.Compact1x1
        };

        var res = await _panelService.ResizeWidgetAsync(item.WidgetId, next, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            ApplyActiveProfile(res.Value);
            StatusMessage = $"Widget '{item.Title}' alterado para {next}.";
        }
    }

    private async Task MoveWidgetRelativeAsync(PanelWidgetItem? item, int delta, CancellationToken cancellationToken)
    {
        if (item is null) return;
        var idx = ActiveWidgets.IndexOf(item);
        if (idx < 0) return;

        var target = Math.Clamp(idx + delta, 0, Math.Max(0, ActiveWidgets.Count - 1));
        if (target == idx) return;

        var res = await _panelService.MoveWidgetAsync(item.WidgetId, target, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            ApplyActiveProfile(res.Value);
            StatusMessage = $"Widget '{item.Title}' movido para posição {target + 1}.";
        }
    }

    private async Task SyncPanelRemoteAsync(CancellationToken cancellationToken)
    {
        if (_activeProfile is null) return;
        var res = await _remoteClient.SaveRemoteProfileAsync(_activeProfile, cancellationToken).ConfigureAwait(false);
        StatusMessage = res.IsSuccess
            ? $"Perfil '{res.Value.Name}' publicado para dispositivos conectados."
            : $"Sincronização remota indisponível: {res.Error!.Message}";
    }

    private async Task AddApprovedAppAsync(CancellationToken cancellationToken)
    {
        var cur = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (cur.IsFailure) return;

        var id = string.IsNullOrWhiteSpace(NewShortcutId)
            ? $"app-{cur.Value.ApprovedApps.Count + 1}"
            : NewShortcutId.Trim();
        var name = string.IsNullOrWhiteSpace(NewShortcutName) ? id : NewShortcutName.Trim();
        var path = NewShortcutPath.Trim();

        var entry = new ApprovedAppShortcut
        {
            ShortcutId = id,
            DisplayName = name,
            ExecutablePath = path,
            RequireConfirmation = NewShortcutRequireConfirmation
        };

        var updatedList = cur.Value.ApprovedApps
            .Where(a => !string.Equals(a.ShortcutId, id, StringComparison.OrdinalIgnoreCase))
            .Append(entry)
            .ToArray();

        var saved = await _engine.SaveSettingsAsync(cur.Value with { ApprovedApps = updatedList }, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            StatusMessage = saved.Error!.Message;
            return;
        }

        NewShortcutId = string.Empty;
        NewShortcutName = string.Empty;
        NewShortcutPath = string.Empty;
        ApplySettings(saved.Value);
        StatusMessage = $"Atalho aprovado '{entry.DisplayName}' adicionado à allowlist.";
    }

    private async Task RemoveApprovedAppAsync(ApprovedAppShortcut? shortcut, CancellationToken cancellationToken)
    {
        if (shortcut is null) return;
        var cur = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (cur.IsFailure) return;

        var updated = cur.Value with
        {
            ApprovedApps = cur.Value.ApprovedApps.Where(a => !string.Equals(a.ShortcutId, shortcut.ShortcutId, StringComparison.OrdinalIgnoreCase)).ToArray()
        };

        var saved = await _engine.SaveSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            ApplySettings(saved.Value);
            StatusMessage = $"Atalho '{shortcut.DisplayName}' removido da allowlist.";
        }
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

        var isSensitive = SelectedActionType == AutomationActionType.LaunchApprovedApp
                          || (SelectedActionType == AutomationActionType.ExecuteControlCommand
                              && RemoteControlCommandRequest.IsHighImpactAction(SelectedControlAction));

        var rule = new AutomationRule
        {
            RuleId = _editingRuleId ?? Guid.NewGuid(),
            Name = name,
            Description = RuleDescription?.Trim() ?? string.Empty,
            IsEnabled = IsRuleEnabled,
            Trigger = SelectedTrigger,
            BatteryThresholdPercent = SelectedTrigger == AutomationTriggerType.BatteryLevelThreshold ? BatteryThresholdPercent : null,
            ExpectedRouteFilter = string.IsNullOrWhiteSpace(SelectedRouteFilter) ? null : SelectedRouteFilter.Trim(),
            Conditions = conditions,
            Actions =
            [
                new AutomationAction
                {
                    ActionId = Guid.NewGuid(),
                    ActionType = SelectedActionType,
                    Label = SelectedActionType.ToString(),
                    TargetProfileId = SelectedActionType == AutomationActionType.ActivatePanelProfile ? SelectedTargetProfile?.ProfileId : null,
                    Title = string.IsNullOrWhiteSpace(ActionTitle) ? null : ActionTitle.Trim(),
                    Message = string.IsNullOrWhiteSpace(ActionMessage) ? null : ActionMessage.Trim(),
                    MediaAction = SelectedActionType == AutomationActionType.ExecuteMediaCommand ? SelectedMediaAction : null,
                    ControlAction = SelectedActionType == AutomationActionType.ExecuteControlCommand ? SelectedControlAction : null,
                    ShortcutId = SelectedActionType == AutomationActionType.LaunchApprovedApp ? SelectedShortcut?.ShortcutId : null,
                    IsSensitiveAction = isSensitive
                }
            ],
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

        ResetRuleEditor();
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = $"Regra '{res.Value.Name}' salva.";
    }

    private async Task DuplicateRuleAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null) return;
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
            StatusMessage = $"Regra '{saved.Value.Name}' criada.";
        }
    }

    private async Task ToggleRuleEnabledAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null) return;
        var toggled = await _engine.SetRuleEnabledAsync(rule.RuleId, !rule.IsEnabled, cancellationToken).ConfigureAwait(false);
        if (toggled.IsSuccess)
        {
            await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Regra '{toggled.Value.Name}' {(toggled.Value.IsEnabled ? "ativada" : "desativada imediatamente")}.";
        }
    }

    private async Task DeleteRuleAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null) return;
        var res = await _engine.DeleteRuleAsync(rule.RuleId, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Regra '{rule.Name}' excluída.";
        }
    }

    private async Task TestRunRuleAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null) return;
        var res = await _engine.TriggerRuleManuallyAsync(rule.RuleId, NexusPermission.All, approvedSensitiveExecution: false, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (res.Value.Outcome == AutomationExecutionOutcome.BlockedAuthorization)
        {
            PendingSensitiveRule = rule;
            PendingSensitivePrompt = $"Regra '{rule.Name}' possui ação sensível. Confirmar execução imediata?";
        }

        StatusMessage = $"{res.Value.RuleName}: {res.Value.Outcome} — {res.Value.Detail}";
    }

    private async Task ConfirmSensitiveRuleAsync(CancellationToken cancellationToken)
    {
        if (PendingSensitiveRule is null) return;
        var rule = PendingSensitiveRule;
        PendingSensitiveRule = null;
        var res = await _engine.TriggerRuleManuallyAsync(rule.RuleId, NexusPermission.All, approvedSensitiveExecution: true, cancellationToken).ConfigureAwait(false);
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = res.IsSuccess ? $"{res.Value.Outcome}: {res.Value.Detail}" : res.Error!.Message;
    }

    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        var cleared = await _engine.ClearExecutionHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (cleared.IsSuccess)
        {
            ExecutionHistory.Clear();
            StatusMessage = $"{cleared.Value} registros removidos.";
        }
    }

    private async Task ReloadPanelProfilesAsync(CancellationToken cancellationToken)
    {
        var list = await _panelService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            Profiles.Clear();
            foreach (var p in list.Value)
            {
                Profiles.Add(p);
            }
        }

        var active = await _panelService.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsSuccess)
        {
            ApplyActiveProfile(active.Value);
            SelectedTargetProfile ??= Profiles.FirstOrDefault(p => p.ProfileId == active.Value.ProfileId) ?? Profiles.FirstOrDefault();
        }
    }

    private async Task ReloadRulesAndHistoryAsync(CancellationToken cancellationToken)
    {
        var rules = await _engine.GetRulesAsync(cancellationToken).ConfigureAwait(false);
        if (rules.IsSuccess)
        {
            Rules.Clear();
            foreach (var r in rules.Value)
            {
                Rules.Add(r);
            }
        }

        var hist = await _engine.GetExecutionHistoryAsync(80, cancellationToken).ConfigureAwait(false);
        if (hist.IsSuccess)
        {
            ExecutionHistory.Clear();
            foreach (var item in hist.Value)
            {
                ExecutionHistory.Add(item);
            }
        }
    }

    private void ApplyActiveProfile(PanelProfile profile)
    {
        _activeProfile = profile;
        SelectedTheme = profile.Theme;
        SelectedAccent = profile.Accent;
        SelectedGridColumns = profile.GridColumns;
        OnPropertyChanged(nameof(ActiveProfileName));

        ActiveWidgets.Clear();
        foreach (var w in profile.Widgets.OrderBy(x => x.SortOrder))
        {
            ActiveWidgets.Add(w);
        }
    }

    private void ApplySettings(AutomationEngineSettings settings)
    {
        IsKillSwitchActive = settings.EmergencyKillSwitchActive;
        IsGlobalAutomationsEnabled = settings.GlobalAutomationsEnabled;
        AndroidLimitationsNote = settings.AndroidBackgroundLimitationNote;

        ApprovedApps.Clear();
        foreach (var app in settings.ApprovedApps)
        {
            ApprovedApps.Add(app);
        }

        SelectedShortcut ??= ApprovedApps.FirstOrDefault();
    }

    private void LoadRuleIntoEditor(AutomationRule rule)
    {
        _editingRuleId = rule.RuleId;
        RuleName = rule.Name;
        RuleDescription = rule.Description;
        SelectedTrigger = rule.Trigger;
        BatteryThresholdPercent = rule.BatteryThresholdPercent ?? 20;
        SelectedRouteFilter = rule.ExpectedRouteFilter ?? string.Empty;
        CooldownSeconds = rule.CooldownSeconds;
        MaxExecutionsPerMinute = rule.MaxExecutionsPerMinute;
        AllowSensitiveExecution = rule.IsSensitiveAuthorized;
        IsRuleEnabled = rule.IsEnabled;
        RequireConnectedCondition = rule.Conditions.Any(c => c.Operator == AutomationConditionOperator.RequireConnected);

        var act = rule.Actions.FirstOrDefault();
        if (act is not null)
        {
            SelectedActionType = act.ActionType;
            ActionTitle = act.Title ?? string.Empty;
            ActionMessage = act.Message ?? string.Empty;
            if (act.TargetProfileId.HasValue)
            {
                SelectedTargetProfile = Profiles.FirstOrDefault(p => p.ProfileId == act.TargetProfileId.Value);
            }
        }

        StatusMessage = $"Editando regra '{rule.Name}'.";
    }

    private void ResetRuleEditor()
    {
        _editingRuleId = null;
        RuleName = string.Empty;
        RuleDescription = string.Empty;
        ActionTitle = string.Empty;
        ActionMessage = string.Empty;
        RequireConnectedCondition = false;
        AllowSensitiveExecution = false;
        IsRuleEnabled = true;
    }

    private void OnActiveProfileChanged(object? sender, PanelProfile profile)
        => RunOnUi(() => ApplyActiveProfile(profile));

    private void OnExecutionLogged(object? sender, AutomationExecutionRecord record)
    {
        RunOnUi(() =>
        {
            ExecutionHistory.Insert(0, record);
            while (ExecutionHistory.Count > 80)
            {
                ExecutionHistory.RemoveAt(ExecutionHistory.Count - 1);
            }
        });
    }

    private void OnIndicatorEmitted(object? sender, PanelRuntimeIndicator indicator)
    {
        RunOnUi(() =>
        {
            ActiveIndicators.Insert(0, indicator);
            while (ActiveIndicators.Count > 15)
            {
                ActiveIndicators.RemoveAt(ActiveIndicators.Count - 1);
            }
        });
    }

    private void OnSettingsChanged(object? sender, AutomationEngineSettings settings)
        => RunOnUi(() => ApplySettings(settings));

    private void OnRemoteProfileUpdated(object? sender, PanelProfile profile)
        => _ = _panelService.SaveProfileAsync(profile, CancellationToken.None);

    private void OnRemoteExecutionReceived(object? sender, AutomationExecutionRecord record)
        => OnExecutionLogged(sender, record);

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
        _panelService.ActiveProfileChanged -= OnActiveProfileChanged;
        _engine.ExecutionLogged -= OnExecutionLogged;
        _engine.IndicatorEmitted -= OnIndicatorEmitted;
        _engine.SettingsChanged -= OnSettingsChanged;
        _realtimeChannel.PanelProfileUpdatedReceived -= OnRemoteProfileUpdated;
        _realtimeChannel.AutomationExecutionReceived -= OnRemoteExecutionReceived;
    }
}
