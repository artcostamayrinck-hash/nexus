using System.Collections.ObjectModel;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Modules;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Core.Modules;
using Nexus.Core.Sync;
using Nexus.Core.Transfers;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

public sealed class PanelWidgetDisplayCard : MobileObservableObject
{
    public required Guid WidgetId { get; init; }
    public required PanelWidgetType WidgetType { get; init; }
    public required string Title { get; init; }
    public required PanelWidgetSize Size { get; init; }
    public required int GridColumn { get; init; }
    public required int GridRow { get; init; }
    public required int ColumnSpan { get; init; }
    public required int RowSpan { get; init; }
    public required string PrimaryValue { get; init; }
    public required string SecondaryValue { get; init; }
    public required string AccentHex { get; init; }
    public required string CardBackgroundHex { get; init; }
    public string? ActionTargetId { get; init; }

    public string SizeLabel => Size switch
    {
        PanelWidgetSize.Wide2x1 => "2x1 Largo",
        PanelWidgetSize.Tall1x2 => "1x2 Alto",
        PanelWidgetSize.Large2x2 => "2x2 Grande",
        _ => "1x1 Compacto"
    };

    public string WidgetTypeLabel => WidgetType switch
    {
        PanelWidgetType.ConnectionStatus => "Conexão",
        PanelWidgetType.ComputerInfo => "PC Windows",
        PanelWidgetType.CpuAndRam => "CPU & RAM",
        PanelWidgetType.VolumeControl => "Volume",
        PanelWidgetType.MediaControls => "Mídia",
        PanelWidgetType.AppShortcuts => "Atalhos Apps",
        PanelWidgetType.QuickActions => "Ações Rápidas",
        PanelWidgetType.TransferStatus => "Transferências",
        PanelWidgetType.RecentNotes => "Notas Recentes",
        PanelWidgetType.NetworkInfo => "Rede & Rota",
        _ => WidgetType.ToString()
    };

    public bool IsVolumeWidget => WidgetType == PanelWidgetType.VolumeControl;
    public bool IsMediaWidget => WidgetType == PanelWidgetType.MediaControls;
    public bool IsQuickActionsWidget => WidgetType == PanelWidgetType.QuickActions;
    public bool IsAppShortcutsWidget => WidgetType == PanelWidgetType.AppShortcuts;
}

public sealed class MobilePanelViewModel : MobileObservableObject, IDisposable
{
    private readonly IPanelLayoutService _panelService;
    private readonly IAutomationEngine _automationEngine;
    private readonly IPanelAndAutomationClient _remoteClient;
    private readonly IIntegrationModulesClient _modulesClient;
    private readonly INexusRouteOrchestrator _routeOrchestrator;
    private readonly IRealtimeClientChannel _realtimeChannel;
    private readonly ITransferManager _transferManager;
    private readonly INotesSyncService _notesService;
    private readonly ILocalDeviceContext _localDevice;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    private PanelProfile? _activeProfile;
    private string _statusMessage = "Painel NEXUS pronto.";
    private string _newProfileName = string.Empty;
    private PanelThemePreset _selectedTheme = PanelThemePreset.MidnightNavy;
    private PanelAccentColor _selectedAccent = PanelAccentColor.Cyan;
    private int _selectedGridColumns = 2;
    private PanelWidgetType _selectedWidgetToAdd = PanelWidgetType.ConnectionStatus;
    private PanelWidgetSize _selectedWidgetSize = PanelWidgetSize.Compact1x1;
    private string _newWidgetTitle = string.Empty;
    private string _panelBackgroundHex = "#07101F";
    private string _panelAccentHex = "#22D3EE";
    private bool _isEditMode;
    private bool _suppressAppearanceAutoSave;

    private HostTelemetrySnapshot? _lastHostSnapshot;
    private MediaPlaybackStateContract? _lastMediaState;
    private string _recentNotesSummary = "Nenhuma nota recente";
    private string _recentNotesDetail = "Toque em atualizar";

    public MobilePanelViewModel(
        IPanelLayoutService panelService,
        IAutomationEngine automationEngine,
        IPanelAndAutomationClient remoteClient,
        IIntegrationModulesClient modulesClient,
        INexusRouteOrchestrator routeOrchestrator,
        IRealtimeClientChannel realtimeChannel,
        ITransferManager transferManager,
        INotesSyncService notesService,
        ILocalDeviceContext localDevice)
    {
        _panelService = panelService;
        _automationEngine = automationEngine;
        _remoteClient = remoteClient;
        _modulesClient = modulesClient;
        _routeOrchestrator = routeOrchestrator;
        _realtimeChannel = realtimeChannel;
        _transferManager = transferManager;
        _notesService = notesService;
        _localDevice = localDevice;

        InitializeCommand = new MobileAsyncCommand(InitializeAsync);
        RefreshLiveDataCommand = new MobileAsyncCommand(RefreshLiveDataAsync);
        ToggleEditModeCommand = new MobileAsyncCommand(_ =>
        {
            IsEditMode = !IsEditMode;
            return Task.CompletedTask;
        });

        CreateProfileCommand = new MobileAsyncCommand(CreateProfileAsync);
        ActivateProfileCommand = new MobileAsyncCommand<PanelProfile>(ActivateProfileAsync);
        DeleteProfileCommand = new MobileAsyncCommand<PanelProfile>(DeleteProfileAsync);

        AddWidgetCommand = new MobileAsyncCommand(AddWidgetAsync);
        RemoveWidgetCommand = new MobileAsyncCommand<PanelWidgetDisplayCard>(RemoveWidgetAsync);
        CycleWidgetSizeCommand = new MobileAsyncCommand<PanelWidgetDisplayCard>(CycleWidgetSizeAsync);
        MoveWidgetUpCommand = new MobileAsyncCommand<PanelWidgetDisplayCard>((w, ct) => MoveWidgetRelativeAsync(w, -1, ct));
        MoveWidgetDownCommand = new MobileAsyncCommand<PanelWidgetDisplayCard>((w, ct) => MoveWidgetRelativeAsync(w, 1, ct));
        ApplyAppearanceCommand = new MobileAsyncCommand(ApplyAppearanceAsync);
        SyncRemoteProfileCommand = new MobileAsyncCommand(SyncRemoteProfileAsync);

        VolumeUpCommand = new MobileAsyncCommand(ct => SendMediaActionAsync(MediaControlAction.VolumeUp, ct));
        VolumeDownCommand = new MobileAsyncCommand(ct => SendMediaActionAsync(MediaControlAction.VolumeDown, ct));
        ToggleMuteCommand = new MobileAsyncCommand(ct => SendMediaActionAsync(MediaControlAction.ToggleMute, ct));
        MediaPlayPauseCommand = new MobileAsyncCommand(ct => SendMediaActionAsync(MediaControlAction.PlayPause, ct));
        MediaNextCommand = new MobileAsyncCommand(ct => SendMediaActionAsync(MediaControlAction.Next, ct));
        MediaPreviousCommand = new MobileAsyncCommand(ct => SendMediaActionAsync(MediaControlAction.Previous, ct));
        QuickHotkeyShowDesktopCommand = new MobileAsyncCommand(ct => SendQuickHotkeyAsync("show-desktop", ct));
        LaunchApprovedAppCommand = new MobileAsyncCommand<ApprovedAppShortcut>(LaunchApprovedAppAsync);
        ClearIndicatorsCommand = new MobileAsyncCommand(_ =>
        {
            ActiveIndicators.Clear();
            OnPropertyChanged(nameof(HasActiveIndicators));
            return Task.CompletedTask;
        });

        _panelService.ActiveProfileChanged += OnLocalActiveProfileChanged;
        _automationEngine.IndicatorEmitted += OnAutomationIndicatorEmitted;
        _realtimeChannel.PanelProfileUpdatedReceived += OnRemoteProfileUpdated;
        _realtimeChannel.MediaStateReceived += OnRealtimeMediaStateReceived;
        _realtimeChannel.StateChanged += OnRealtimeStateChanged;
        _transferManager.TransferUpdated += OnTransferUpdated;
    }

    public ObservableCollection<PanelProfile> Profiles { get; } = new();
    public ObservableCollection<PanelWidgetDisplayCard> Widgets { get; } = new();
    public ObservableCollection<PanelRuntimeIndicator> ActiveIndicators { get; } = new();
    public ObservableCollection<ApprovedAppShortcut> ApprovedApps { get; } = new();

    public IReadOnlyList<PanelWidgetType> AvailableWidgetTypes { get; } = Enum.GetValues<PanelWidgetType>();
    public IReadOnlyList<PanelWidgetSize> AvailableWidgetSizes { get; } = Enum.GetValues<PanelWidgetSize>();
    public IReadOnlyList<PanelThemePreset> AvailableThemes { get; } = Enum.GetValues<PanelThemePreset>();
    public IReadOnlyList<PanelAccentColor> AvailableAccents { get; } = Enum.GetValues<PanelAccentColor>();
    public IReadOnlyList<int> AvailableGridColumnCounts { get; } = [1, 2, 3, 4];

    public MobileAsyncCommand InitializeCommand { get; }
    public MobileAsyncCommand RefreshLiveDataCommand { get; }
    public MobileAsyncCommand ToggleEditModeCommand { get; }
    public MobileAsyncCommand CreateProfileCommand { get; }
    public MobileAsyncCommand<PanelProfile> ActivateProfileCommand { get; }
    public MobileAsyncCommand<PanelProfile> DeleteProfileCommand { get; }
    public MobileAsyncCommand AddWidgetCommand { get; }
    public MobileAsyncCommand<PanelWidgetDisplayCard> RemoveWidgetCommand { get; }
    public MobileAsyncCommand<PanelWidgetDisplayCard> CycleWidgetSizeCommand { get; }
    public MobileAsyncCommand<PanelWidgetDisplayCard> MoveWidgetUpCommand { get; }
    public MobileAsyncCommand<PanelWidgetDisplayCard> MoveWidgetDownCommand { get; }
    public MobileAsyncCommand ApplyAppearanceCommand { get; }
    public MobileAsyncCommand SyncRemoteProfileCommand { get; }
    public MobileAsyncCommand VolumeUpCommand { get; }
    public MobileAsyncCommand VolumeDownCommand { get; }
    public MobileAsyncCommand ToggleMuteCommand { get; }
    public MobileAsyncCommand MediaPlayPauseCommand { get; }
    public MobileAsyncCommand MediaNextCommand { get; }
    public MobileAsyncCommand MediaPreviousCommand { get; }
    public MobileAsyncCommand QuickHotkeyShowDesktopCommand { get; }
    public MobileAsyncCommand<ApprovedAppShortcut> LaunchApprovedAppCommand { get; }
    public MobileAsyncCommand ClearIndicatorsCommand { get; }

    public string ActiveProfileName => _activeProfile?.Name ?? "Carregando...";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string NewProfileName { get => _newProfileName; set => SetProperty(ref _newProfileName, value); }
    public string PanelBackgroundHex { get => _panelBackgroundHex; private set => SetProperty(ref _panelBackgroundHex, value); }
    public string PanelAccentHex { get => _panelAccentHex; private set => SetProperty(ref _panelAccentHex, value); }
    public bool IsEditMode { get => _isEditMode; set => SetProperty(ref _isEditMode, value); }
    public bool HasActiveIndicators => ActiveIndicators.Count > 0;

    public PanelThemePreset SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value) && !_suppressAppearanceAutoSave)
            {
                _ = ApplyAppearanceAsync(CancellationToken.None);
            }
        }
    }

    public PanelAccentColor SelectedAccent
    {
        get => _selectedAccent;
        set
        {
            if (SetProperty(ref _selectedAccent, value) && !_suppressAppearanceAutoSave)
            {
                _ = ApplyAppearanceAsync(CancellationToken.None);
            }
        }
    }

    public int SelectedGridColumns
    {
        get => _selectedGridColumns;
        set
        {
            var clamped = Math.Clamp(value, 1, 4);
            if (SetProperty(ref _selectedGridColumns, clamped) && !_suppressAppearanceAutoSave)
            {
                _ = ApplyAppearanceAsync(CancellationToken.None);
            }
        }
    }

    public PanelWidgetType SelectedWidgetToAdd
    {
        get => _selectedWidgetToAdd;
        set => SetProperty(ref _selectedWidgetToAdd, value);
    }

    public PanelWidgetSize SelectedWidgetSize
    {
        get => _selectedWidgetSize;
        set => SetProperty(ref _selectedWidgetSize, value);
    }

    public string NewWidgetTitle
    {
        get => _newWidgetTitle;
        set => SetProperty(ref _newWidgetTitle, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var profilesRes = await _panelService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (profilesRes.IsFailure)
        {
            StatusMessage = profilesRes.Error!.Message;
            return;
        }

        var activeRes = await _panelService.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        if (activeRes.IsFailure)
        {
            StatusMessage = activeRes.Error!.Message;
            return;
        }

        ReplaceProfiles(profilesRes.Value);
        ApplyActiveProfile(activeRes.Value);

        var settingsRes = await _automationEngine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsSuccess)
        {
            ApprovedApps.Clear();
            foreach (var app in settingsRes.Value.ApprovedApps)
            {
                ApprovedApps.Add(app);
            }
        }

        ActiveIndicators.Clear();
        foreach (var indicator in _automationEngine.ActiveIndicators)
        {
            ActiveIndicators.Add(indicator);
        }
        OnPropertyChanged(nameof(HasActiveIndicators));

        await RefreshLiveDataAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = $"Perfil '{activeRes.Value.Name}' ativo ({activeRes.Value.Widgets.Count} widgets).";
    }

    public async Task RefreshLiveDataAsync(CancellationToken cancellationToken = default)
    {
        var notesRes = await _notesService.ListNotesAsync(null, false, cancellationToken).ConfigureAwait(false);
        if (notesRes.IsSuccess && notesRes.Value.Count > 0)
        {
            var topNote = notesRes.Value.OrderByDescending(n => n.ModifiedAtUtc).First();
            _recentNotesSummary = $"{notesRes.Value.Count} notas";
            _recentNotesDetail = topNote.Title;
        }
        else
        {
            _recentNotesSummary = "0 notas";
            _recentNotesDetail = "Crie ou sincronize notas no app";
        }

        if (_realtimeChannel.State == ConnectionState.Connected || _routeOrchestrator.CurrentStatus.State == ConnectionState.Connected)
        {
            var monitorRes = await _modulesClient.GetRemoteMonitorSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (monitorRes.IsSuccess)
            {
                _lastHostSnapshot = monitorRes.Value;
            }

            var mediaRes = await _modulesClient.GetRemoteMediaStateAsync(cancellationToken).ConfigureAwait(false);
            if (mediaRes.IsSuccess)
            {
                _lastMediaState = mediaRes.Value;
            }
        }

        if (_activeProfile is not null)
        {
            RebuildWidgetCards(_activeProfile);
        }
    }

    private async Task CreateProfileAsync(CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName)
            ? $"Perfil {Profiles.Count + 1}"
            : NewProfileName.Trim();

        var created = await _panelService.CreateProfileAsync(name, SelectedTheme, SelectedAccent, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            StatusMessage = created.Error!.Message;
            return;
        }

        NewProfileName = string.Empty;
        var list = await _panelService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            ReplaceProfiles(list.Value);
        }

        var activated = await _panelService.ActivateProfileAsync(created.Value.ProfileId, cancellationToken).ConfigureAwait(false);
        if (activated.IsSuccess)
        {
            ApplyActiveProfile(activated.Value);
            StatusMessage = $"Perfil '{activated.Value.Name}' criado e ativado.";
        }
    }

    private async Task ActivateProfileAsync(PanelProfile? profile, CancellationToken cancellationToken)
    {
        if (profile is null)
        {
            return;
        }

        var res = await _panelService.ActivateProfileAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        var list = await _panelService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            ReplaceProfiles(list.Value);
        }

        ApplyActiveProfile(res.Value);
        StatusMessage = $"Perfil '{res.Value.Name}' ativado e salvo automaticamente.";
    }

    private async Task DeleteProfileAsync(PanelProfile? profile, CancellationToken cancellationToken)
    {
        if (profile is null)
        {
            return;
        }

        var res = await _panelService.DeleteProfileAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        var list = await _panelService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            ReplaceProfiles(list.Value);
        }

        var active = await _panelService.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        if (active.IsSuccess)
        {
            ApplyActiveProfile(active.Value);
        }

        StatusMessage = $"Perfil '{profile.Name}' removido.";
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
        StatusMessage = $"Widget '{SelectedWidgetToAdd}' adicionado e salvo automaticamente.";
    }

    private async Task RemoveWidgetAsync(PanelWidgetDisplayCard? card, CancellationToken cancellationToken)
    {
        if (card is null)
        {
            return;
        }

        var res = await _panelService.RemoveWidgetAsync(card.WidgetId, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        ApplyActiveProfile(res.Value);
        StatusMessage = $"Widget '{card.Title}' removido.";
    }

    private async Task CycleWidgetSizeAsync(PanelWidgetDisplayCard? card, CancellationToken cancellationToken)
    {
        if (card is null)
        {
            return;
        }

        var nextSize = card.Size switch
        {
            PanelWidgetSize.Compact1x1 => PanelWidgetSize.Wide2x1,
            PanelWidgetSize.Wide2x1 => PanelWidgetSize.Tall1x2,
            PanelWidgetSize.Tall1x2 => PanelWidgetSize.Large2x2,
            _ => PanelWidgetSize.Compact1x1
        };

        var res = await _panelService.ResizeWidgetAsync(card.WidgetId, nextSize, cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
            return;
        }

        ApplyActiveProfile(res.Value);
        StatusMessage = $"'{card.Title}' redimensionado para {nextSize}.";
    }

    private async Task MoveWidgetRelativeAsync(PanelWidgetDisplayCard? card, int delta, CancellationToken cancellationToken)
    {
        if (card is null || _activeProfile is null)
        {
            return;
        }

        var currentIndex = -1;
        for (var i = 0; i < Widgets.Count; i++)
        {
            if (Widgets[i].WidgetId == card.WidgetId)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = Math.Clamp(currentIndex + delta, 0, Math.Max(0, Widgets.Count - 1));
        if (targetIndex == currentIndex)
        {
            return;
        }

        var res = await _panelService.MoveWidgetAsync(card.WidgetId, targetIndex, cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            ApplyActiveProfile(res.Value);
            StatusMessage = $"'{card.Title}' movido para posição {targetIndex + 1}.";
        }
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
        StatusMessage = $"Tema {SelectedTheme} / destaque {SelectedAccent} / {SelectedGridColumns} colunas salvo.";
    }

    private async Task SyncRemoteProfileAsync(CancellationToken cancellationToken)
    {
        if (_activeProfile is null)
        {
            return;
        }

        var res = await _remoteClient.SaveRemoteProfileAsync(_activeProfile, cancellationToken).ConfigureAwait(false);
        StatusMessage = res.IsSuccess
            ? $"Perfil '{res.Value.Name}' sincronizado com o PC Windows (rev. {res.Value.Revision})."
            : $"Sincronização remota indisponível: {res.Error!.Message}";
    }

    private async Task SendMediaActionAsync(MediaControlAction action, CancellationToken cancellationToken)
    {
        var res = await _modulesClient.SendMediaCommandAsync(new MediaControlCommandRequest
        {
            CommandId = Guid.NewGuid(),
            SourceDeviceId = _localDevice.DeviceId,
            Action = action,
            RequestedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        if (res.IsSuccess)
        {
            _lastMediaState = res.Value;
            if (_activeProfile is not null)
            {
                RebuildWidgetCards(_activeProfile);
            }

            StatusMessage = $"Comando '{action}' enviado ao PC.";
        }
        else
        {
            StatusMessage = res.Error!.Message;
        }
    }

    private async Task SendQuickHotkeyAsync(string hotkeyId, CancellationToken cancellationToken)
    {
        var res = await _modulesClient.SendControlCommandAsync(new RemoteControlCommandRequest
        {
            CommandId = Guid.NewGuid(),
            SourceDeviceId = _localDevice.DeviceId,
            Action = RemoteControlActionType.Hotkey,
            HotkeyId = hotkeyId,
            RequestedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        StatusMessage = res.IsSuccess
            ? res.Value.Message
            : res.Error!.Message;
    }

    private async Task LaunchApprovedAppAsync(ApprovedAppShortcut? shortcut, CancellationToken cancellationToken)
    {
        if (shortcut is null)
        {
            return;
        }

        var rule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = $"Abrir {shortcut.DisplayName}",
            Trigger = AutomationTriggerType.Manual,
            IsSensitiveAuthorized = !shortcut.RequireConfirmation,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.LaunchApprovedApp,
                    Label = shortcut.DisplayName,
                    ShortcutId = shortcut.ShortcutId,
                    IsSensitiveAction = shortcut.RequireConfirmation
                }
            ]
        };

        var saved = await _automationEngine.SaveRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            StatusMessage = saved.Error!.Message;
            return;
        }

        var exec = await _automationEngine.TriggerRuleManuallyAsync(saved.Value.RuleId, approvedSensitiveExecution: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _automationEngine.DeleteRuleAsync(saved.Value.RuleId, cancellationToken).ConfigureAwait(false);
        StatusMessage = exec.IsSuccess ? exec.Value.Detail : exec.Error!.Message;
    }

    private void ReplaceProfiles(IReadOnlyList<PanelProfile> items)
    {
        Profiles.Clear();
        foreach (var item in items)
        {
            Profiles.Add(item);
        }
    }

    private void ApplyActiveProfile(PanelProfile profile)
    {
        _activeProfile = profile;
        _suppressAppearanceAutoSave = true;
        try
        {
            SelectedTheme = profile.Theme;
            SelectedAccent = profile.Accent;
            SelectedGridColumns = profile.GridColumns;
            PanelBackgroundHex = profile.BackgroundHex;
            PanelAccentHex = profile.AccentHex;
        }
        finally
        {
            _suppressAppearanceAutoSave = false;
        }

        OnPropertyChanged(nameof(ActiveProfileName));
        RebuildWidgetCards(profile);
    }

    private void RebuildWidgetCards(PanelProfile profile)
    {
        var cardBg = profile.Theme switch
        {
            PanelThemePreset.OledBlack => "#0B0B0D",
            PanelThemePreset.SlateGlass => "#1E293B",
            PanelThemePreset.HighContrast => "#111827",
            _ => "#111E36"
        };

        Widgets.Clear();
        foreach (var w in profile.Widgets.OrderBy(x => x.SortOrder))
        {
            var (primary, secondary) = ResolveWidgetContent(w);
            Widgets.Add(new PanelWidgetDisplayCard
            {
                WidgetId = w.WidgetId,
                WidgetType = w.WidgetType,
                Title = w.Title,
                Size = w.Size,
                GridColumn = w.GridColumn,
                GridRow = w.GridRow,
                ColumnSpan = Math.Min(w.ColumnSpan, profile.GridColumns),
                RowSpan = w.RowSpan,
                PrimaryValue = primary,
                SecondaryValue = secondary,
                AccentHex = profile.AccentHex,
                CardBackgroundHex = cardBg,
                ActionTargetId = w.ActionTargetId
            });
        }
    }

    private (string Primary, string Secondary) ResolveWidgetContent(PanelWidgetItem w)
    {
        var conn = _routeOrchestrator.CurrentStatus;
        var isConnected = _realtimeChannel.State == ConnectionState.Connected || conn.State == ConnectionState.Connected;

        return w.WidgetType switch
        {
            PanelWidgetType.ConnectionStatus => (
                isConnected ? $"Conectado · {conn.DeviceName}" : "Desconectado",
                conn.ActiveEndpoint ?? "Configure uma rota LAN ou Tailscale"),

            PanelWidgetType.ComputerInfo => _lastHostSnapshot is not null
                ? (_lastHostSnapshot.HostName, $"{_lastHostSnapshot.OsDescription} · Uptime {_lastHostSnapshot.Uptime:hh\\:mm\\:ss}")
                : (isConnected ? conn.DeviceName : "Host não conectado", "Atualize para ler info do PC"),

            PanelWidgetType.CpuAndRam => _lastHostSnapshot is not null
                ? ($"CPU {(_lastHostSnapshot.Cpu.UsagePercent.IsAvailable ? $"{_lastHostSnapshot.Cpu.UsagePercent.Value:F0}%" : "--")}",
                    _lastHostSnapshot.Memory.UsagePercent.IsAvailable
                        ? $"RAM {_lastHostSnapshot.Memory.UsagePercent.Value:F0}% ({_lastHostSnapshot.Memory.UsedMb.Value:F0}/{_lastHostSnapshot.Memory.TotalMb.Value:F0} MB)"
                        : "RAM indisponível")
                : ("Aguardando telemetria", "Conecte ao PC Windows"),

            PanelWidgetType.VolumeControl => _lastMediaState is not null && _lastMediaState.VolumePercent.IsAvailable
                ? ($"Volume {_lastMediaState.VolumePercent.Value}%",
                    _lastMediaState.IsMuted.IsAvailable && _lastMediaState.IsMuted.Value ? "Mudo ativo" : "Áudio ativo")
                : ("Volume Windows", isConnected ? "Use Vol - / Vol + abaixo" : "Desconectado"),

            PanelWidgetType.MediaControls => _lastMediaState is not null
                ? ($"{_lastMediaState.PlaybackStatus}", _lastMediaState.MetadataAvailabilityNote)
                : ("Controle de Mídia", "Play/Pause, Próxima, Anterior"),

            PanelWidgetType.AppShortcuts => (
                ApprovedApps.Count > 0 ? $"{ApprovedApps.Count} atalho(s) aprovado(s)" : "Nenhum atalho aprovado",
                ApprovedApps.Count > 0 ? string.Join(" · ", ApprovedApps.Take(3).Select(a => a.DisplayName)) : "Configure no host Windows"),

            PanelWidgetType.QuickActions => (
                isConnected ? "Pronto para ações rápidas" : "Modo local",
                "Área de Trabalho (Win+D) · Volume · Mídia"),

            PanelWidgetType.TransferStatus => FormatTransferWidget(),

            PanelWidgetType.RecentNotes => (_recentNotesSummary, _recentNotesDetail),

            PanelWidgetType.NetworkInfo => (
                $"Rota: {conn.RouteKind} · VPN: {conn.VpnStatus}",
                conn.LatencyMs.HasValue ? $"Latência: {conn.LatencyMs.Value} ms" : "Latência não medida"),

            _ => (w.Title, string.Empty)
        };
    }

    private (string Primary, string Secondary) FormatTransferWidget()
    {
        var active = _transferManager.ActiveTransfers;
        if (active.Count > 0)
        {
            var first = active[0];
            return ($"{active.Count} ativa(s): {first.OriginalFileName}", $"{first.PercentComplete:F0}% · {first.State}");
        }

        var latest = _transferManager.History.FirstOrDefault();
        return latest is not null
            ? ($"Última: {latest.OriginalFileName}", $"{latest.State}")
            : ("0 transferências ativas", "Fila limpa");
    }

    private void OnLocalActiveProfileChanged(object? sender, PanelProfile profile)
        => RunOnUi(() => ApplyActiveProfile(profile));

    private void OnAutomationIndicatorEmitted(object? sender, PanelRuntimeIndicator indicator)
    {
        RunOnUi(() =>
        {
            ActiveIndicators.Insert(0, indicator);
            while (ActiveIndicators.Count > 10)
            {
                ActiveIndicators.RemoveAt(ActiveIndicators.Count - 1);
            }

            OnPropertyChanged(nameof(HasActiveIndicators));
        });
    }

    private void OnRemoteProfileUpdated(object? sender, PanelProfile profile)
        => _ = _panelService.SaveProfileAsync(profile, CancellationToken.None);

    private void OnRealtimeMediaStateReceived(object? sender, MediaPlaybackStateContract state)
    {
        RunOnUi(() =>
        {
            _lastMediaState = state;
            if (_activeProfile is not null)
            {
                RebuildWidgetCards(_activeProfile);
            }
        });
    }

    private void OnRealtimeStateChanged(object? sender, ConnectionStateSnapshot _)
    {
        RunOnUi(() =>
        {
            if (_activeProfile is not null)
            {
                RebuildWidgetCards(_activeProfile);
            }
        });
    }

    private void OnTransferUpdated(object? sender, Nexus.Contracts.Transfers.FileTransferRecord _)
    {
        RunOnUi(() =>
        {
            if (_activeProfile is not null)
            {
                RebuildWidgetCards(_activeProfile);
            }
        });
    }

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
        _panelService.ActiveProfileChanged -= OnLocalActiveProfileChanged;
        _automationEngine.IndicatorEmitted -= OnAutomationIndicatorEmitted;
        _realtimeChannel.PanelProfileUpdatedReceived -= OnRemoteProfileUpdated;
        _realtimeChannel.MediaStateReceived -= OnRealtimeMediaStateReceived;
        _realtimeChannel.StateChanged -= OnRealtimeStateChanged;
        _transferManager.TransferUpdated -= OnTransferUpdated;
    }
}
