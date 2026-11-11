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

    private long _profileReloadGeneration;
    private long _rulesReloadGeneration;

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
        IRealtimeClientChannel realtimeChannel,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _panelService = panelService;
        _engine = engine;
        _batteryProvider = batteryProvider;
        _remoteClient = remoteClient;
        _realtimeChannel = realtimeChannel;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync, uiDispatcher: uiDispatcher);
        ToggleKillSwitchCommand = new AsyncRelayCommand(ToggleKillSwitchAsync, uiDispatcher: uiDispatcher);
        ToggleGlobalAutomationsCommand = new AsyncRelayCommand(ToggleGlobalAutomationsAsync, uiDispatcher: uiDispatcher);

        CreateProfileCommand = new AsyncRelayCommand(CreateProfileAsync, uiDispatcher: uiDispatcher);
        ActivateProfileCommand = new AsyncRelayCommand<PanelProfile>(ActivateProfileAsync, uiDispatcher: uiDispatcher);
        DeleteProfileCommand = new AsyncRelayCommand<PanelProfile>(DeleteProfileAsync, uiDispatcher: uiDispatcher);
        ApplyAppearanceCommand = new AsyncRelayCommand(ApplyAppearanceAsync, uiDispatcher: uiDispatcher);
        AddWidgetCommand = new AsyncRelayCommand(AddWidgetAsync, uiDispatcher: uiDispatcher);
        RemoveWidgetCommand = new AsyncRelayCommand<PanelWidgetItem>(RemoveWidgetAsync, uiDispatcher: uiDispatcher);
        CycleWidgetSizeCommand = new AsyncRelayCommand<PanelWidgetItem>(CycleWidgetSizeAsync, uiDispatcher: uiDispatcher);
        MoveWidgetUpCommand = new AsyncRelayCommand<PanelWidgetItem>((w, ct) => MoveWidgetRelativeAsync(w, -1, ct), uiDispatcher: uiDispatcher);
        MoveWidgetDownCommand = new AsyncRelayCommand<PanelWidgetItem>((w, ct) => MoveWidgetRelativeAsync(w, 1, ct), uiDispatcher: uiDispatcher);
        SyncPanelRemoteCommand = new AsyncRelayCommand(SyncPanelRemoteAsync, uiDispatcher: uiDispatcher);

        AddApprovedAppCommand = new AsyncRelayCommand(AddApprovedAppAsync, uiDispatcher: uiDispatcher);
        RemoveApprovedAppCommand = new AsyncRelayCommand<ApprovedAppShortcut>(RemoveApprovedAppAsync, uiDispatcher: uiDispatcher);

        SaveRuleCommand = new AsyncRelayCommand(SaveRuleAsync, uiDispatcher: uiDispatcher);
        ClearRuleEditorCommand = new AsyncRelayCommand(ct => RunOnUiAsync(ResetRuleEditor, ct), uiDispatcher: uiDispatcher);
        EditRuleCommand = new AsyncRelayCommand<AutomationRule>((r, ct) =>
            r is null ? Task.CompletedTask : RunOnUiAsync(() => LoadRuleIntoEditor(r), ct), uiDispatcher: uiDispatcher);
        DuplicateRuleCommand = new AsyncRelayCommand<AutomationRule>(DuplicateRuleAsync, uiDispatcher: uiDispatcher);
        ToggleRuleEnabledCommand = new AsyncRelayCommand<AutomationRule>(ToggleRuleEnabledAsync, uiDispatcher: uiDispatcher);
        DeleteRuleCommand = new AsyncRelayCommand<AutomationRule>(DeleteRuleAsync, uiDispatcher: uiDispatcher);
        TestRunRuleCommand = new AsyncRelayCommand<AutomationRule>(TestRunRuleAsync, uiDispatcher: uiDispatcher);
        ConfirmSensitiveRuleCommand = new AsyncRelayCommand(ConfirmSensitiveRuleAsync, uiDispatcher: uiDispatcher);
        CancelSensitivePromptCommand = new AsyncRelayCommand(ct => RunOnUiAsync(() => PendingSensitiveRule = null, ct), uiDispatcher: uiDispatcher);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync, uiDispatcher: uiDispatcher);

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
        var settingsRes = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var indicatorsSnapshot = _engine.ActiveIndicators.ToArray();

        await RunOnUiAsync(() =>
        {
            HostBatterySummary = battery.StatusMessage;
            if (settingsRes.IsSuccess)
            {
                ApplySettings(settingsRes.Value);
            }

            ReplaceCollection(ActiveIndicators, indicatorsSnapshot);
        }, cancellationToken).ConfigureAwait(false);

        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);

        await RunOnUiAsync(
            () => StatusMessage = $"Painel '{ActiveProfileName}' carregado · {Rules.Count} regra(s) de automação.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ToggleKillSwitchAsync(CancellationToken cancellationToken)
    {
        bool currentKillSwitch = false;
        await RunOnUiAsync(() => currentKillSwitch = IsKillSwitchActive, cancellationToken).ConfigureAwait(false);

        var res = await _engine.SetEmergencyKillSwitchAsync(!currentKillSwitch, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            var settings = res.Value;
            await RunOnUiAsync(() =>
            {
                ApplySettings(settings);
                StatusMessage = settings.EmergencyKillSwitchActive
                    ? "Kill Switch de emergência ATIVADO — nenhuma automação será executada."
                    : "Kill Switch desativado.";
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ToggleGlobalAutomationsAsync(CancellationToken cancellationToken)
    {
        var cur = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (cur.IsFailure) return;

        var res = await _engine.SaveSettingsAsync(cur.Value with { GlobalAutomationsEnabled = !cur.Value.GlobalAutomationsEnabled }, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            var settings = res.Value;
            await RunOnUiAsync(() =>
            {
                ApplySettings(settings);
                StatusMessage = settings.GlobalAutomationsEnabled ? "Automações globais ativadas." : "Automações globais desativadas.";
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateProfileAsync(CancellationToken cancellationToken)
    {
        string name = string.Empty;
        PanelThemePreset theme = PanelThemePreset.MidnightNavy;
        PanelAccentColor accent = PanelAccentColor.Cyan;

        await RunOnUiAsync(() =>
        {
            name = string.IsNullOrWhiteSpace(NewProfileName) ? $"Perfil {Profiles.Count + 1}" : NewProfileName.Trim();
            theme = SelectedTheme;
            accent = SelectedAccent;
        }, cancellationToken).ConfigureAwait(false);

        var created = await _panelService.CreateProfileAsync(name, theme, accent, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            var errMsg = created.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunOnUiAsync(() => NewProfileName = string.Empty, cancellationToken).ConfigureAwait(false);
        await _panelService.ActivateProfileAsync(created.Value.ProfileId, cancellationToken).ConfigureAwait(false);
        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);

        await RunOnUiAsync(
            () => StatusMessage = $"Perfil '{created.Value.Name}' criado e ativado.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ActivateProfileAsync(PanelProfile? profile, CancellationToken cancellationToken)
    {
        if (profile is null) return;
        var res = await _panelService.ActivateProfileAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(
            () => StatusMessage = $"Perfil '{res.Value.Name}' ativado e persistido.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteProfileAsync(PanelProfile? profile, CancellationToken cancellationToken)
    {
        if (profile is null) return;
        var res = await _panelService.DeleteProfileAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReloadPanelProfilesAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(
            () => StatusMessage = $"Perfil '{profile.Name}' removido.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAppearanceAsync(CancellationToken cancellationToken)
    {
        PanelThemePreset theme = PanelThemePreset.MidnightNavy;
        PanelAccentColor accent = PanelAccentColor.Cyan;
        int cols = 2;

        await RunOnUiAsync(() =>
        {
            theme = SelectedTheme;
            accent = SelectedAccent;
            cols = SelectedGridColumns;
        }, cancellationToken).ConfigureAwait(false);

        var res = await _panelService.UpdateAppearanceAsync(theme, accent, cols, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var updatedProfile = res.Value;
        await RunOnUiAsync(() =>
        {
            ApplyActiveProfile(updatedProfile);
            StatusMessage = $"Aparência salva ({theme} · {accent} · {cols} col.).";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddWidgetAsync(CancellationToken cancellationToken)
    {
        string? title = null;
        PanelWidgetType widgetType = PanelWidgetType.ConnectionStatus;
        PanelWidgetSize widgetSize = PanelWidgetSize.Compact1x1;

        await RunOnUiAsync(() =>
        {
            title = string.IsNullOrWhiteSpace(NewWidgetTitle) ? null : NewWidgetTitle.Trim();
            widgetType = SelectedWidgetToAdd;
            widgetSize = SelectedWidgetSize;
        }, cancellationToken).ConfigureAwait(false);

        var res = await _panelService.AddWidgetAsync(widgetType, title, widgetSize, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var updatedProfile = res.Value;
        await RunOnUiAsync(() =>
        {
            NewWidgetTitle = string.Empty;
            ApplyActiveProfile(updatedProfile);
            StatusMessage = $"Widget '{widgetType}' adicionado ao painel.";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveWidgetAsync(PanelWidgetItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        var res = await _panelService.RemoveWidgetAsync(item.WidgetId, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            var updatedProfile = res.Value;
            await RunOnUiAsync(() =>
            {
                ApplyActiveProfile(updatedProfile);
                StatusMessage = $"Widget '{item.Title}' removido.";
            }, cancellationToken).ConfigureAwait(false);
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
            var updatedProfile = res.Value;
            await RunOnUiAsync(() =>
            {
                ApplyActiveProfile(updatedProfile);
                StatusMessage = $"Widget '{item.Title}' alterado para {next}.";
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MoveWidgetRelativeAsync(PanelWidgetItem? item, int delta, CancellationToken cancellationToken)
    {
        if (item is null) return;

        int target = -1;
        await RunOnUiAsync(() =>
        {
            var idx = ActiveWidgets.IndexOf(item);
            if (idx < 0) return;

            var candidate = Math.Clamp(idx + delta, 0, Math.Max(0, ActiveWidgets.Count - 1));
            if (candidate != idx)
            {
                target = candidate;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (target < 0) return;

        var res = await _panelService.MoveWidgetAsync(item.WidgetId, target, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            var updatedProfile = res.Value;
            await RunOnUiAsync(() =>
            {
                ApplyActiveProfile(updatedProfile);
                StatusMessage = $"Widget '{item.Title}' movido para posição {target + 1}.";
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SyncPanelRemoteAsync(CancellationToken cancellationToken)
    {
        PanelProfile? profileSnapshot = null;
        await RunOnUiAsync(() => profileSnapshot = _activeProfile, cancellationToken).ConfigureAwait(false);

        if (profileSnapshot is null) return;
        var res = await _remoteClient.SaveRemoteProfileAsync(profileSnapshot, cancellationToken).ConfigureAwait(false);
        var msg = res.IsSuccess
            ? $"Perfil '{res.Value.Name}' publicado para dispositivos conectados."
            : $"Sincronização remota indisponível: {res.Error!.Message}";

        await RunOnUiAsync(() => StatusMessage = msg, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddApprovedAppAsync(CancellationToken cancellationToken)
    {
        string rawId = string.Empty;
        string rawName = string.Empty;
        string rawPath = string.Empty;
        bool requireConfirm = true;

        await RunOnUiAsync(() =>
        {
            rawId = NewShortcutId;
            rawName = NewShortcutName;
            rawPath = NewShortcutPath;
            requireConfirm = NewShortcutRequireConfirmation;
        }, cancellationToken).ConfigureAwait(false);

        var cur = await _engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (cur.IsFailure) return;

        var id = string.IsNullOrWhiteSpace(rawId)
            ? $"app-{cur.Value.ApprovedApps.Count + 1}"
            : rawId.Trim();
        var name = string.IsNullOrWhiteSpace(rawName) ? id : rawName.Trim();
        var path = rawPath.Trim();

        var entry = new ApprovedAppShortcut
        {
            ShortcutId = id,
            DisplayName = name,
            ExecutablePath = path,
            RequireConfirmation = requireConfirm
        };

        var updatedList = cur.Value.ApprovedApps
            .Where(a => !string.Equals(a.ShortcutId, id, StringComparison.OrdinalIgnoreCase))
            .Append(entry)
            .ToArray();

        var saved = await _engine.SaveSettingsAsync(cur.Value with { ApprovedApps = updatedList }, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            var errMsg = saved.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var settings = saved.Value;
        await RunOnUiAsync(() =>
        {
            NewShortcutId = string.Empty;
            NewShortcutName = string.Empty;
            NewShortcutPath = string.Empty;
            ApplySettings(settings);
            StatusMessage = $"Atalho aprovado '{entry.DisplayName}' adicionado à allowlist.";
        }, cancellationToken).ConfigureAwait(false);
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
            var settings = saved.Value;
            await RunOnUiAsync(() =>
            {
                ApplySettings(settings);
                StatusMessage = $"Atalho '{shortcut.DisplayName}' removido da allowlist.";
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveRuleAsync(CancellationToken cancellationToken)
    {
        AutomationRule rule = new();
        await RunOnUiAsync(() =>
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

            rule = new AutomationRule
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
        }, cancellationToken).ConfigureAwait(false);

        var res = await _engine.SaveRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunOnUiAsync(ResetRuleEditor, cancellationToken).ConfigureAwait(false);
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(
            () => StatusMessage = $"Regra '{res.Value.Name}' salva.",
            cancellationToken).ConfigureAwait(false);
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
            await RunOnUiAsync(
                () => StatusMessage = $"Regra '{saved.Value.Name}' criada.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ToggleRuleEnabledAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null) return;
        var toggled = await _engine.SetRuleEnabledAsync(rule.RuleId, !rule.IsEnabled, cancellationToken).ConfigureAwait(false);
        if (toggled.IsSuccess)
        {
            await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
            var msg = $"Regra '{toggled.Value.Name}' {(toggled.Value.IsEnabled ? "ativada" : "desativada imediatamente")}.";
            await RunOnUiAsync(() => StatusMessage = msg, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteRuleAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null) return;
        var res = await _engine.DeleteRuleAsync(rule.RuleId, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUiAsync(
                () => StatusMessage = $"Regra '{rule.Name}' excluída.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TestRunRuleAsync(AutomationRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null) return;
        var res = await _engine.TriggerRuleManuallyAsync(rule.RuleId, NexusPermission.All, approvedSensitiveExecution: false, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        var rec = res.Value;
        await RunOnUiAsync(() =>
        {
            if (rec.Outcome == AutomationExecutionOutcome.BlockedAuthorization)
            {
                PendingSensitiveRule = rule;
                PendingSensitivePrompt = $"Regra '{rule.Name}' possui ação sensível. Confirmar execução imediata?";
            }

            StatusMessage = $"{rec.RuleName}: {rec.Outcome} — {rec.Detail}";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmSensitiveRuleAsync(CancellationToken cancellationToken)
    {
        AutomationRule? rule = null;
        await RunOnUiAsync(() =>
        {
            rule = PendingSensitiveRule;
            PendingSensitiveRule = null;
        }, cancellationToken).ConfigureAwait(false);

        if (rule is null) return;
        var res = await _engine.TriggerRuleManuallyAsync(rule.RuleId, NexusPermission.All, approvedSensitiveExecution: true, cancellationToken).ConfigureAwait(false);
        await ReloadRulesAndHistoryAsync(cancellationToken).ConfigureAwait(false);
        var msg = res.IsSuccess ? $"{res.Value.Outcome}: {res.Value.Detail}" : res.Error!.Message;
        await RunOnUiAsync(() => StatusMessage = msg, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        var cleared = await _engine.ClearExecutionHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (cleared.IsSuccess)
        {
            var count = cleared.Value;
            await RunOnUiAsync(() =>
            {
                ExecutionHistory.Clear();
                StatusMessage = $"{count} registros removidos.";
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReloadPanelProfilesAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _profileReloadGeneration);
        var list = await _panelService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        var active = await _panelService.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _profileReloadGeneration))
            {
                return;
            }

            if (list.IsSuccess)
            {
                ReplaceCollection(Profiles, list.Value);
            }

            if (active.IsSuccess)
            {
                ApplyActiveProfile(active.Value);
                SelectedTargetProfile ??= Profiles.FirstOrDefault(p => p.ProfileId == active.Value.ProfileId) ?? Profiles.FirstOrDefault();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReloadRulesAndHistoryAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _rulesReloadGeneration);
        var rules = await _engine.GetRulesAsync(cancellationToken).ConfigureAwait(false);
        var hist = await _engine.GetExecutionHistoryAsync(80, cancellationToken).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _rulesReloadGeneration))
            {
                return;
            }

            if (rules.IsSuccess)
            {
                ReplaceCollection(Rules, rules.Value);
            }

            if (hist.IsSuccess)
            {
                ReplaceCollection(ExecutionHistory, hist.Value);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyActiveProfile(PanelProfile profile)
    {
        _activeProfile = profile;
        SelectedTheme = profile.Theme;
        SelectedAccent = profile.Accent;
        SelectedGridColumns = profile.GridColumns;
        OnPropertyChanged(nameof(ActiveProfileName));

        ReplaceCollection(ActiveWidgets, profile.Widgets.OrderBy(x => x.SortOrder).ToArray());
    }

    private void ApplySettings(AutomationEngineSettings settings)
    {
        IsKillSwitchActive = settings.EmergencyKillSwitchActive;
        IsGlobalAutomationsEnabled = settings.GlobalAutomationsEnabled;
        AndroidLimitationsNote = settings.AndroidBackgroundLimitationNote;

        ReplaceCollection(ApprovedApps, settings.ApprovedApps);
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
        => RunOnUi(() => PrependCapped(ExecutionHistory, record, 80));

    private void OnIndicatorEmitted(object? sender, PanelRuntimeIndicator indicator)
        => RunOnUi(() => PrependCapped(ActiveIndicators, indicator, 15));

    private void OnSettingsChanged(object? sender, AutomationEngineSettings settings)
        => RunOnUi(() => ApplySettings(settings));

    private void OnRemoteProfileUpdated(object? sender, PanelProfile profile)
        => _ = SaveRemoteProfileSafelyAsync(profile);

    private async Task SaveRemoteProfileSafelyAsync(PanelProfile profile)
    {
        try
        {
            await _panelService.SaveProfileAsync(profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Mantém resiliência em atualizações assíncronas recebidas em segundo plano.
        }
    }

    private void OnRemoteExecutionReceived(object? sender, AutomationExecutionRecord record)
        => OnExecutionLogged(sender, record);

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
