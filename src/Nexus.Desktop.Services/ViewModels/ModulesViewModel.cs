using System.Collections.ObjectModel;
using Nexus.Contracts.Modules;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Modules;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class ModulesViewModel : ObservableObject, IDisposable
{
    private readonly IModuleSettingsService _settingsService;
    private readonly IWindowsControlExecutor _controlExecutor;
    private readonly IHostMonitorService _hostMonitor;
    private readonly IWindowsMediaService _mediaService;
    private readonly INexusNotifyService _notifyService;
    private readonly IRealtimeClientChannel _realtimeClient;
    private readonly ILocalDeviceContext _localDevice;

    private long _statusGeneration;
    private long _monitorGeneration;
    private long _notificationsGeneration;

    private string _statusMessage = "Módulos de integração prontos.";
    private bool _isControlEnabled = true;
    private bool _requirePowerConfirmation = true;
    private bool _allowPowerActions = true;
    private bool _isNotifyEnabled = true;
    private NotificationFilterMode _notifyFilterMode = NotificationFilterMode.AllowAll;
    private string _allowedAppsText = string.Empty;
    private string _blockedAppsText = string.Empty;
    private string _androidNotifyNote = "Android requer permissão NotificationListenerService.";
    private bool _isMonitorEnabled = true;
    private int _monitorRefreshSeconds = 3;
    private bool _isMediaEnabled = true;

    private string _newHotkeyId = string.Empty;
    private string _newHotkeyName = string.Empty;
    private string _newHotkeyKeys = "Ctrl+Alt+N";
    private string _controlFeedback = "Nenhuma ação executada ainda.";

    private string _cpuNameText = "Aguardando coleta...";
    private string _cpuUsageText = "--";
    private string _memorySummaryText = "--";
    private string _gpuSummaryText = "Verificando...";
    private string _gpuAvailabilityNote = string.Empty;
    private string _temperatureSummaryText = "Verificando...";
    private string _temperatureStatusNote = string.Empty;
    private string _uptimeText = "--";

    private string _mediaPlaybackBadge = "Desconhecido";
    private string _mediaTrackText = "Sem metadados de faixa ativos";
    private string _mediaMetadataNote = string.Empty;
    private string _mediaVolumeText = "--";
    private string _mediaMuteBadge = "--";
    private int _targetVolumePercent = 50;

    public ModulesViewModel(
        IModuleSettingsService settingsService,
        IWindowsControlExecutor controlExecutor,
        IHostMonitorService hostMonitor,
        IWindowsMediaService mediaService,
        INexusNotifyService notifyService,
        IRealtimeClientChannel realtimeClient,
        ILocalDeviceContext localDevice,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _settingsService = settingsService;
        _controlExecutor = controlExecutor;
        _hostMonitor = hostMonitor;
        _mediaService = mediaService;
        _notifyService = notifyService;
        _realtimeClient = realtimeClient;
        _localDevice = localDevice;

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, uiDispatcher: uiDispatcher);
        ToggleControlCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Control, IsControlEnabled, ct), uiDispatcher: uiDispatcher);
        ToggleNotifyCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Notify, IsNotifyEnabled, ct), uiDispatcher: uiDispatcher);
        ToggleMonitorCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Monitor, IsMonitorEnabled, ct), uiDispatcher: uiDispatcher);
        ToggleMediaCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Media, IsMediaEnabled, ct), uiDispatcher: uiDispatcher);
        SaveControlSettingsCommand = new AsyncRelayCommand(SaveControlSettingsAsync, uiDispatcher: uiDispatcher);
        SaveNotifySettingsCommand = new AsyncRelayCommand(SaveNotifySettingsAsync, uiDispatcher: uiDispatcher);
        AddHotkeyCommand = new AsyncRelayCommand(AddHotkeyAsync, uiDispatcher: uiDispatcher);
        RemoveHotkeyCommand = new AsyncRelayCommand<RemoteHotkeyDefinition>(RemoveHotkeyAsync, uiDispatcher: uiDispatcher);
        ExecuteHotkeyCommand = new AsyncRelayCommand<RemoteHotkeyDefinition>(ExecuteHotkeyAsync, uiDispatcher: uiDispatcher);
        DismissNotificationCommand = new AsyncRelayCommand<MobileNotificationRecord>(DismissNotificationAsync, uiDispatcher: uiDispatcher);
        ClearNotificationsCommand = new AsyncRelayCommand(ClearNotificationsAsync, uiDispatcher: uiDispatcher);
        RefreshMonitorCommand = new AsyncRelayCommand(RefreshMonitorAsync, uiDispatcher: uiDispatcher);
        MediaPlayPauseCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.PlayPause, null, ct), uiDispatcher: uiDispatcher);
        MediaNextCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.Next, null, ct), uiDispatcher: uiDispatcher);
        MediaPreviousCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.Previous, null, ct), uiDispatcher: uiDispatcher);
        MediaStopCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.Stop, null, ct), uiDispatcher: uiDispatcher);
        VolumeUpCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.VolumeUp, null, ct), uiDispatcher: uiDispatcher);
        VolumeDownCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.VolumeDown, null, ct), uiDispatcher: uiDispatcher);
        ToggleMuteCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.ToggleMute, null, ct), uiDispatcher: uiDispatcher);
        ApplyVolumeCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.SetVolume, TargetVolumePercent, ct), uiDispatcher: uiDispatcher);

        _notifyService.NotificationReceived += OnLocalNotificationReceived;
        _realtimeClient.MobileNotificationReceived += OnRealtimeNotificationReceived;
        _realtimeClient.MediaStateReceived += OnRealtimeMediaStateReceived;
        _realtimeClient.ModuleStatusChangedReceived += OnRealtimeModuleStatusChanged;
    }

    public ObservableCollection<IntegrationModuleStatusSnapshot> ModuleStatuses { get; } = [];
    public ObservableCollection<RemoteHotkeyDefinition> Hotkeys { get; } = [];
    public ObservableCollection<MobileNotificationRecord> NotificationFeed { get; } = [];
    public ObservableCollection<DiskTelemetryItem> Disks { get; } = [];
    public ObservableCollection<NetworkInterfaceTelemetryItem> NetworkInterfaces { get; } = [];
    public ObservableCollection<ProcessTelemetryItem> TopProcesses { get; } = [];

    public IReadOnlyList<NotificationFilterMode> NotifyFilterModes { get; } = Enum.GetValues<NotificationFilterMode>();

    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand ToggleControlCommand { get; }
    public AsyncRelayCommand ToggleNotifyCommand { get; }
    public AsyncRelayCommand ToggleMonitorCommand { get; }
    public AsyncRelayCommand ToggleMediaCommand { get; }
    public AsyncRelayCommand SaveControlSettingsCommand { get; }
    public AsyncRelayCommand SaveNotifySettingsCommand { get; }
    public AsyncRelayCommand AddHotkeyCommand { get; }
    public AsyncRelayCommand<RemoteHotkeyDefinition> RemoveHotkeyCommand { get; }
    public AsyncRelayCommand<RemoteHotkeyDefinition> ExecuteHotkeyCommand { get; }
    public AsyncRelayCommand<MobileNotificationRecord> DismissNotificationCommand { get; }
    public AsyncRelayCommand ClearNotificationsCommand { get; }
    public AsyncRelayCommand RefreshMonitorCommand { get; }
    public AsyncRelayCommand MediaPlayPauseCommand { get; }
    public AsyncRelayCommand MediaNextCommand { get; }
    public AsyncRelayCommand MediaPreviousCommand { get; }
    public AsyncRelayCommand MediaStopCommand { get; }
    public AsyncRelayCommand VolumeUpCommand { get; }
    public AsyncRelayCommand VolumeDownCommand { get; }
    public AsyncRelayCommand ToggleMuteCommand { get; }
    public AsyncRelayCommand ApplyVolumeCommand { get; }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsControlEnabled { get => _isControlEnabled; set => SetProperty(ref _isControlEnabled, value); }
    public bool RequirePowerConfirmation { get => _requirePowerConfirmation; set => SetProperty(ref _requirePowerConfirmation, value); }
    public bool AllowPowerActions { get => _allowPowerActions; set => SetProperty(ref _allowPowerActions, value); }
    public bool IsNotifyEnabled { get => _isNotifyEnabled; set => SetProperty(ref _isNotifyEnabled, value); }
    public NotificationFilterMode NotifyFilterMode { get => _notifyFilterMode; set => SetProperty(ref _notifyFilterMode, value); }
    public string AllowedAppsText { get => _allowedAppsText; set => SetProperty(ref _allowedAppsText, value); }
    public string BlockedAppsText { get => _blockedAppsText; set => SetProperty(ref _blockedAppsText, value); }
    public string AndroidNotifyNote { get => _androidNotifyNote; private set => SetProperty(ref _androidNotifyNote, value); }
    public bool IsMonitorEnabled { get => _isMonitorEnabled; set => SetProperty(ref _isMonitorEnabled, value); }
    public int MonitorRefreshSeconds { get => _monitorRefreshSeconds; set => SetProperty(ref _monitorRefreshSeconds, value); }
    public bool IsMediaEnabled { get => _isMediaEnabled; set => SetProperty(ref _isMediaEnabled, value); }

    public string NewHotkeyId { get => _newHotkeyId; set => SetProperty(ref _newHotkeyId, value); }
    public string NewHotkeyName { get => _newHotkeyName; set => SetProperty(ref _newHotkeyName, value); }
    public string NewHotkeyKeys { get => _newHotkeyKeys; set => SetProperty(ref _newHotkeyKeys, value); }
    public string ControlFeedback { get => _controlFeedback; private set => SetProperty(ref _controlFeedback, value); }

    public string CpuNameText { get => _cpuNameText; private set => SetProperty(ref _cpuNameText, value); }
    public string CpuUsageText { get => _cpuUsageText; private set => SetProperty(ref _cpuUsageText, value); }
    public string MemorySummaryText { get => _memorySummaryText; private set => SetProperty(ref _memorySummaryText, value); }
    public string GpuSummaryText { get => _gpuSummaryText; private set => SetProperty(ref _gpuSummaryText, value); }
    public string GpuAvailabilityNote { get => _gpuAvailabilityNote; private set => SetProperty(ref _gpuAvailabilityNote, value); }
    public string TemperatureSummaryText { get => _temperatureSummaryText; private set => SetProperty(ref _temperatureSummaryText, value); }
    public string TemperatureStatusNote { get => _temperatureStatusNote; private set => SetProperty(ref _temperatureStatusNote, value); }
    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }

    public string MediaPlaybackBadge { get => _mediaPlaybackBadge; private set => SetProperty(ref _mediaPlaybackBadge, value); }
    public string MediaTrackText { get => _mediaTrackText; private set => SetProperty(ref _mediaTrackText, value); }
    public string MediaMetadataNote { get => _mediaMetadataNote; private set => SetProperty(ref _mediaMetadataNote, value); }
    public string MediaVolumeText { get => _mediaVolumeText; private set => SetProperty(ref _mediaVolumeText, value); }
    public string MediaMuteBadge { get => _mediaMuteBadge; private set => SetProperty(ref _mediaMuteBadge, value); }
    public int TargetVolumePercent { get => _targetVolumePercent; set => SetProperty(ref _targetVolumePercent, Math.Clamp(value, 0, 100)); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
        => await RefreshAllAsync(cancellationToken).ConfigureAwait(false);

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await RefreshStatusesAndSettingsAsync(cancellationToken).ConfigureAwait(false);
        await RefreshNotificationsAsync(cancellationToken).ConfigureAwait(false);
        await RefreshMonitorAsync(cancellationToken).ConfigureAwait(false);
        await RefreshMediaAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(
            () => StatusMessage = $"Módulos atualizados ({ModuleStatuses.Count} integrações).",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshStatusesAndSettingsAsync(CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _statusGeneration);

        var statusesRes = await _settingsService.GetModuleStatusesAsync(ct).ConfigureAwait(false);
        var controlRes = await _settingsService.GetControlSettingsAsync(ct).ConfigureAwait(false);
        var notifyRes = await _settingsService.GetNotifySettingsAsync(ct).ConfigureAwait(false);
        var monitorRes = await _settingsService.GetMonitorSettingsAsync(ct).ConfigureAwait(false);
        var mediaRes = await _settingsService.GetMediaSettingsAsync(ct).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _statusGeneration))
            {
                return;
            }

            if (statusesRes.IsSuccess)
            {
                ReplaceCollection(ModuleStatuses, statusesRes.Value);
            }

            if (controlRes.IsSuccess)
            {
                IsControlEnabled = controlRes.Value.IsEnabled;
                RequirePowerConfirmation = controlRes.Value.RequirePowerActionConfirmation;
                AllowPowerActions = controlRes.Value.AllowPowerActions;
                ReplaceCollection(Hotkeys, controlRes.Value.Hotkeys);
            }

            if (notifyRes.IsSuccess)
            {
                IsNotifyEnabled = notifyRes.Value.IsEnabled;
                NotifyFilterMode = notifyRes.Value.FilterMode;
                AllowedAppsText = string.Join(", ", notifyRes.Value.AllowedApps);
                BlockedAppsText = string.Join(", ", notifyRes.Value.BlockedApps);
                AndroidNotifyNote = notifyRes.Value.AndroidPermission.PlatformNote;
            }

            if (monitorRes.IsSuccess)
            {
                IsMonitorEnabled = monitorRes.Value.IsEnabled;
                MonitorRefreshSeconds = monitorRes.Value.RefreshIntervalSeconds;
            }

            if (mediaRes.IsSuccess)
            {
                IsMediaEnabled = mediaRes.Value.IsEnabled;
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task ToggleModuleAsync(IntegrationModuleKind kind, bool isEnabled, CancellationToken ct)
    {
        bool requireConfirm = false;
        await RunOnUiAsync(() => requireConfirm = RequirePowerConfirmation, ct).ConfigureAwait(false);

        var req = new UpdateModuleToggleRequest(kind, isEnabled, kind == IntegrationModuleKind.Control ? requireConfirm : null);
        var res = await _settingsService.SetModuleEnabledAsync(req, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, ct).ConfigureAwait(false);
            return;
        }

        await RefreshStatusesAndSettingsAsync(ct).ConfigureAwait(false);
        if (kind == IntegrationModuleKind.Monitor && isEnabled)
        {
            await RefreshMonitorAsync(ct).ConfigureAwait(false);
        }
        else if (kind == IntegrationModuleKind.Media && isEnabled)
        {
            await RefreshMediaAsync(ct).ConfigureAwait(false);
        }

        var toggleMsg = $"{res.Value.DisplayName}: {(res.Value.IsEnabled ? "ativado" : "desativado")}.";
        await RunOnUiAsync(() => StatusMessage = toggleMsg, ct).ConfigureAwait(false);
    }

    public async Task SaveControlSettingsAsync(CancellationToken ct = default)
    {
        bool enabled = true;
        bool reqConfirm = true;
        bool allowPower = true;
        RemoteHotkeyDefinition[] hotkeysSnapshot = Array.Empty<RemoteHotkeyDefinition>();

        await RunOnUiAsync(() =>
        {
            enabled = IsControlEnabled;
            reqConfirm = RequirePowerConfirmation;
            allowPower = AllowPowerActions;
            hotkeysSnapshot = Hotkeys.ToArray();
        }, ct).ConfigureAwait(false);

        var current = await _settingsService.GetControlSettingsAsync(ct).ConfigureAwait(false);
        var baseSettings = current.IsSuccess ? current.Value : new ControlModuleSettings();
        var updated = baseSettings with
        {
            IsEnabled = enabled,
            RequirePowerActionConfirmation = reqConfirm,
            AllowPowerActions = allowPower,
            Hotkeys = hotkeysSnapshot
        };

        var saved = await _settingsService.SaveControlSettingsAsync(updated, ct).ConfigureAwait(false);
        var msg = saved.IsSuccess
            ? "Configurações do Nexus Control salvas."
            : saved.Error!.Message;

        await RunOnUiAsync(() => StatusMessage = msg, ct).ConfigureAwait(false);
        await RefreshStatusesAndSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task AddHotkeyAsync(CancellationToken ct = default)
    {
        string rawKeys = string.Empty;
        string rawName = string.Empty;
        string rawId = string.Empty;

        await RunOnUiAsync(() =>
        {
            rawKeys = NewHotkeyKeys;
            rawName = NewHotkeyName;
            rawId = NewHotkeyId;
        }, ct).ConfigureAwait(false);

        var keys = ParseCsvOrPlus(rawKeys);
        if (string.IsNullOrWhiteSpace(rawName) || keys.Length == 0)
        {
            await RunOnUiAsync(
                () => ControlFeedback = "Informe nome e teclas (ex.: Ctrl+Alt+N).",
                ct).ConfigureAwait(false);
            return;
        }

        var id = string.IsNullOrWhiteSpace(rawId)
            ? rawName.Trim().ToLowerInvariant().Replace(' ', '-')
            : rawId.Trim();

        var hotkey = new RemoteHotkeyDefinition
        {
            HotkeyId = id,
            DisplayName = rawName.Trim(),
            Keys = keys,
            Description = $"Atalho personalizado ({string.Join("+", keys)})"
        };

        await RunOnUiAsync(() =>
        {
            Hotkeys.Add(hotkey);
            NewHotkeyId = string.Empty;
            NewHotkeyName = string.Empty;
        }, ct).ConfigureAwait(false);

        await SaveControlSettingsAsync(ct).ConfigureAwait(false);
        await RunOnUiAsync(() => ControlFeedback = $"Atalho '{id}' adicionado.", ct).ConfigureAwait(false);
    }

    private async Task RemoveHotkeyAsync(RemoteHotkeyDefinition? hotkey, CancellationToken ct)
    {
        if (hotkey is null) return;
        await RunOnUiAsync(() => Hotkeys.Remove(hotkey), ct).ConfigureAwait(false);
        await SaveControlSettingsAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteHotkeyAsync(RemoteHotkeyDefinition? hotkey, CancellationToken ct)
    {
        if (hotkey is null) return;
        var res = await _controlExecutor.ExecuteAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            Action = RemoteControlActionType.Hotkey,
            HotkeyId = hotkey.HotkeyId,
            Keys = hotkey.Keys
        }, ct).ConfigureAwait(false);

        var feedback = res.IsSuccess ? res.Value.Message : res.Error!.Message;
        await RunOnUiAsync(() => ControlFeedback = feedback, ct).ConfigureAwait(false);
    }

    public async Task SaveNotifySettingsAsync(CancellationToken ct = default)
    {
        bool enabled = true;
        NotificationFilterMode mode = NotificationFilterMode.AllowAll;
        string allowedRaw = string.Empty;
        string blockedRaw = string.Empty;

        await RunOnUiAsync(() =>
        {
            enabled = IsNotifyEnabled;
            mode = NotifyFilterMode;
            allowedRaw = AllowedAppsText;
            blockedRaw = BlockedAppsText;
        }, ct).ConfigureAwait(false);

        var current = await _settingsService.GetNotifySettingsAsync(ct).ConfigureAwait(false);
        var baseSettings = current.IsSuccess ? current.Value : new NotifyModuleSettings();
        var updated = baseSettings with
        {
            IsEnabled = enabled,
            FilterMode = mode,
            AllowedApps = ParseCsvApps(allowedRaw),
            BlockedApps = ParseCsvApps(blockedRaw)
        };

        var saved = await _settingsService.SaveNotifySettingsAsync(updated, ct).ConfigureAwait(false);
        var msg = saved.IsSuccess
            ? "Preferências de filtro do Nexus Notify salvas."
            : saved.Error!.Message;

        await RunOnUiAsync(() => StatusMessage = msg, ct).ConfigureAwait(false);
        await RefreshStatusesAndSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task RefreshNotificationsAsync(CancellationToken ct = default)
    {
        var generation = Interlocked.Increment(ref _notificationsGeneration);
        var res = await _notifyService.ListHistoryAsync(80, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            var items = res.Value;
            await RunOnUiAsync(() =>
            {
                if (generation == Volatile.Read(ref _notificationsGeneration))
                {
                    ReplaceCollection(NotificationFeed, items);
                }
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task DismissNotificationAsync(MobileNotificationRecord? record, CancellationToken ct)
    {
        if (record is null) return;
        var res = await _notifyService.DismissAsync(record.NotificationId, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            await RefreshNotificationsAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ClearNotificationsAsync(CancellationToken ct)
    {
        var res = await _notifyService.ClearHistoryAsync(ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            var count = res.Value;
            await RunOnUiAsync(() =>
            {
                NotificationFeed.Clear();
                StatusMessage = $"{count} notificação(ões) removidas do histórico.";
            }, ct).ConfigureAwait(false);
        }
    }

    public async Task RefreshMonitorAsync(CancellationToken ct = default)
    {
        var generation = Interlocked.Increment(ref _monitorGeneration);
        var snapRes = await _hostMonitor.CollectSnapshotAsync(ct).ConfigureAwait(false);
        if (snapRes.IsFailure)
        {
            var errMsg = snapRes.Error!.Message;
            await RunOnUiAsync(() =>
            {
                if (generation != Volatile.Read(ref _monitorGeneration))
                {
                    return;
                }

                CpuUsageText = "Desativado";
                MemorySummaryText = errMsg;
                Disks.Clear();
                NetworkInterfaces.Clear();
                TopProcesses.Clear();
            }, ct).ConfigureAwait(false);
            return;
        }

        var s = snapRes.Value;
        var clockSuffix = s.Cpu.NominalClockMhz.IsAvailable ? $" · {s.Cpu.NominalClockMhz.Value} MHz" : string.Empty;
        var cpuUsage = s.Cpu.UsagePercent.IsAvailable
            ? $"{s.Cpu.UsagePercent.Value:F1}% ({s.Cpu.LogicalCores} threads{clockSuffix})"
            : $"Indisponível ({s.Cpu.UsagePercent.StatusMessage})";
        var memSummary = s.Memory.UsagePercent.IsAvailable
            ? $"{s.Memory.UsagePercent.Value:F1}% · {s.Memory.UsedMb.Value:F0} / {s.Memory.TotalMb.Value:F0} MB"
            : s.Memory.UsagePercent.StatusMessage;
        var gpuSummary = s.Gpu.IsAvailable
            ? (s.Gpu.AdapterRamMb.IsAvailable ? $"{s.Gpu.AdapterName} ({s.Gpu.AdapterRamMb.Value:F0} MB VRAM)" : s.Gpu.AdapterName)
            : "Indisponível";
        var tempSummary = s.Temperatures.IsAvailable
            ? $"{s.Temperatures.PrimaryCelsius.Value:F1} °C"
            : "Indisponível no hardware/SO atual";
        var uptimeText = $"Tempo ativo: {(int)s.Uptime.TotalHours}h {s.Uptime.Minutes}m · {s.OsDescription}";

        await RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _monitorGeneration))
            {
                return;
            }

            CpuNameText = s.Cpu.ProcessorName;
            CpuUsageText = cpuUsage;
            MemorySummaryText = memSummary;
            GpuSummaryText = gpuSummary;
            GpuAvailabilityNote = s.Gpu.StatusMessage;
            TemperatureSummaryText = tempSummary;
            TemperatureStatusNote = s.Temperatures.StatusMessage;
            UptimeText = uptimeText;

            ReplaceCollection(Disks, s.Disks);
            ReplaceCollection(NetworkInterfaces, s.NetworkInterfaces);
            ReplaceCollection(TopProcesses, s.TopProcesses);
        }, ct).ConfigureAwait(false);
    }

    public async Task RefreshMediaAsync(CancellationToken ct = default)
    {
        var res = await _mediaService.GetCurrentStateAsync(ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() =>
            {
                MediaPlaybackBadge = "Desativado";
                MediaVolumeText = "--";
                MediaMuteBadge = "--";
                MediaMetadataNote = errMsg;
            }, ct).ConfigureAwait(false);
            return;
        }

        var state = res.Value;
        await RunOnUiAsync(() => ApplyMediaState(state), ct).ConfigureAwait(false);
    }

    private async Task ExecuteMediaActionAsync(MediaControlAction action, int? volumePercent, CancellationToken ct)
    {
        var res = await _mediaService.ExecuteCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            Action = action,
            VolumePercent = volumePercent
        }, ct).ConfigureAwait(false);

        if (res.IsFailure)
        {
            var errMsg = res.Error!.Message;
            await RunOnUiAsync(() => StatusMessage = errMsg, ct).ConfigureAwait(false);
            return;
        }

        var state = res.Value;
        await RunOnUiAsync(() =>
        {
            ApplyMediaState(state);
            StatusMessage = $"Mídia: comando {action} executado.";
        }, ct).ConfigureAwait(false);
    }

    private void ApplyMediaState(MediaPlaybackStateContract state)
    {
        MediaPlaybackBadge = state.PlaybackStatus.ToString();
        MediaMetadataNote = state.MetadataAvailabilityNote;
        MediaTrackText = state.IsMetadataAvailable && !string.IsNullOrWhiteSpace(state.Title)
            ? $"{state.Title} — {state.Artist}"
            : "Controles nativos ativos (sem metadados SMTC)";

        MediaVolumeText = state.VolumePercent.IsAvailable
            ? $"{state.VolumePercent.Value}%"
            : state.VolumePercent.StatusMessage;

        if (state.VolumePercent.IsAvailable)
        {
            _targetVolumePercent = state.VolumePercent.Value;
            OnPropertyChanged(nameof(TargetVolumePercent));
        }

        MediaMuteBadge = state.IsMuted.IsAvailable
            ? (state.IsMuted.Value ? "Silenciado" : "Áudio Ativo")
            : state.IsMuted.StatusMessage;
    }

    private void OnLocalNotificationReceived(object? sender, MobileNotificationRecord record)
    {
        RunOnUi(() =>
        {
            if (!NotificationFeed.Any(n => n.NotificationId == record.NotificationId))
            {
                PrependCapped(NotificationFeed, record, 100);
            }
        });
    }

    private void OnRealtimeNotificationReceived(object? sender, MobileNotificationRecord record)
    {
        RunOnUi(() =>
        {
            if (!NotificationFeed.Any(n => n.NotificationId == record.NotificationId))
            {
                PrependCapped(NotificationFeed, record, 100);
            }
        });
    }

    private void OnRealtimeMediaStateReceived(object? sender, MediaPlaybackStateContract state)
        => RunOnUi(() => ApplyMediaState(state));

    private void OnRealtimeModuleStatusChanged(object? sender, IntegrationModuleStatusSnapshot status)
    {
        RunOnUi(() =>
        {
            var existing = ModuleStatuses.FirstOrDefault(s => s.ModuleKind == status.ModuleKind);
            if (existing is not null)
            {
                var idx = ModuleStatuses.IndexOf(existing);
                ModuleStatuses[idx] = status;
            }
        });
    }

    private static string[] ParseCsvApps(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] ParseCsvOrPlus(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose()
    {
        _notifyService.NotificationReceived -= OnLocalNotificationReceived;
        _realtimeClient.MobileNotificationReceived -= OnRealtimeNotificationReceived;
        _realtimeClient.MediaStateReceived -= OnRealtimeMediaStateReceived;
        _realtimeClient.ModuleStatusChangedReceived -= OnRealtimeModuleStatusChanged;
    }
}
