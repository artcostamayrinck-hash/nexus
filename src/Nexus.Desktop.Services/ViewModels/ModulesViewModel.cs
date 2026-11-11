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
        ILocalDeviceContext localDevice)
    {
        _settingsService = settingsService;
        _controlExecutor = controlExecutor;
        _hostMonitor = hostMonitor;
        _mediaService = mediaService;
        _notifyService = notifyService;
        _realtimeClient = realtimeClient;
        _localDevice = localDevice;

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        ToggleControlCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Control, IsControlEnabled, ct));
        ToggleNotifyCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Notify, IsNotifyEnabled, ct));
        ToggleMonitorCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Monitor, IsMonitorEnabled, ct));
        ToggleMediaCommand = new AsyncRelayCommand(ct => ToggleModuleAsync(IntegrationModuleKind.Media, IsMediaEnabled, ct));
        SaveControlSettingsCommand = new AsyncRelayCommand(SaveControlSettingsAsync);
        SaveNotifySettingsCommand = new AsyncRelayCommand(SaveNotifySettingsAsync);
        AddHotkeyCommand = new AsyncRelayCommand(AddHotkeyAsync);
        RemoveHotkeyCommand = new AsyncRelayCommand<RemoteHotkeyDefinition>(RemoveHotkeyAsync);
        ExecuteHotkeyCommand = new AsyncRelayCommand<RemoteHotkeyDefinition>(ExecuteHotkeyAsync);
        DismissNotificationCommand = new AsyncRelayCommand<MobileNotificationRecord>(DismissNotificationAsync);
        ClearNotificationsCommand = new AsyncRelayCommand(ClearNotificationsAsync);
        RefreshMonitorCommand = new AsyncRelayCommand(RefreshMonitorAsync);
        MediaPlayPauseCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.PlayPause, null, ct));
        MediaNextCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.Next, null, ct));
        MediaPreviousCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.Previous, null, ct));
        MediaStopCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.Stop, null, ct));
        VolumeUpCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.VolumeUp, null, ct));
        VolumeDownCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.VolumeDown, null, ct));
        ToggleMuteCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.ToggleMute, null, ct));
        ApplyVolumeCommand = new AsyncRelayCommand(ct => ExecuteMediaActionAsync(MediaControlAction.SetVolume, TargetVolumePercent, ct));

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
        StatusMessage = $"Módulos atualizados ({ModuleStatuses.Count} integrações).";
    }

    private async Task RefreshStatusesAndSettingsAsync(CancellationToken ct)
    {
        var statusesRes = await _settingsService.GetModuleStatusesAsync(ct).ConfigureAwait(false);
        if (statusesRes.IsSuccess)
        {
            ModuleStatuses.Clear();
            foreach (var status in statusesRes.Value)
            {
                ModuleStatuses.Add(status);
            }
        }

        var controlRes = await _settingsService.GetControlSettingsAsync(ct).ConfigureAwait(false);
        if (controlRes.IsSuccess)
        {
            IsControlEnabled = controlRes.Value.IsEnabled;
            RequirePowerConfirmation = controlRes.Value.RequirePowerActionConfirmation;
            AllowPowerActions = controlRes.Value.AllowPowerActions;
            Hotkeys.Clear();
            foreach (var hk in controlRes.Value.Hotkeys)
            {
                Hotkeys.Add(hk);
            }
        }

        var notifyRes = await _settingsService.GetNotifySettingsAsync(ct).ConfigureAwait(false);
        if (notifyRes.IsSuccess)
        {
            IsNotifyEnabled = notifyRes.Value.IsEnabled;
            NotifyFilterMode = notifyRes.Value.FilterMode;
            AllowedAppsText = string.Join(", ", notifyRes.Value.AllowedApps);
            BlockedAppsText = string.Join(", ", notifyRes.Value.BlockedApps);
            AndroidNotifyNote = notifyRes.Value.AndroidPermission.PlatformNote;
        }

        var monitorRes = await _settingsService.GetMonitorSettingsAsync(ct).ConfigureAwait(false);
        if (monitorRes.IsSuccess)
        {
            IsMonitorEnabled = monitorRes.Value.IsEnabled;
            MonitorRefreshSeconds = monitorRes.Value.RefreshIntervalSeconds;
        }

        var mediaRes = await _settingsService.GetMediaSettingsAsync(ct).ConfigureAwait(false);
        if (mediaRes.IsSuccess)
        {
            IsMediaEnabled = mediaRes.Value.IsEnabled;
        }
    }

    private async Task ToggleModuleAsync(IntegrationModuleKind kind, bool isEnabled, CancellationToken ct)
    {
        var req = new UpdateModuleToggleRequest(kind, isEnabled, kind == IntegrationModuleKind.Control ? RequirePowerConfirmation : null);
        var res = await _settingsService.SetModuleEnabledAsync(req, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error!.Message;
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

        StatusMessage = $"{res.Value.DisplayName}: {(res.Value.IsEnabled ? "ativado" : "desativado")}.";
    }

    public async Task SaveControlSettingsAsync(CancellationToken ct = default)
    {
        var current = await _settingsService.GetControlSettingsAsync(ct).ConfigureAwait(false);
        var baseSettings = current.IsSuccess ? current.Value : new ControlModuleSettings();
        var updated = baseSettings with
        {
            IsEnabled = IsControlEnabled,
            RequirePowerActionConfirmation = RequirePowerConfirmation,
            AllowPowerActions = AllowPowerActions,
            Hotkeys = Hotkeys.ToArray()
        };

        var saved = await _settingsService.SaveControlSettingsAsync(updated, ct).ConfigureAwait(false);
        StatusMessage = saved.IsSuccess
            ? "Configurações do Nexus Control salvas."
            : saved.Error!.Message;
        await RefreshStatusesAndSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task AddHotkeyAsync(CancellationToken ct = default)
    {
        var keys = ParseCsvOrPlus(NewHotkeyKeys);
        if (string.IsNullOrWhiteSpace(NewHotkeyName) || keys.Length == 0)
        {
            ControlFeedback = "Informe nome e teclas (ex.: Ctrl+Alt+N).";
            return;
        }

        var id = string.IsNullOrWhiteSpace(NewHotkeyId)
            ? NewHotkeyName.Trim().ToLowerInvariant().Replace(' ', '-')
            : NewHotkeyId.Trim();

        Hotkeys.Add(new RemoteHotkeyDefinition
        {
            HotkeyId = id,
            DisplayName = NewHotkeyName.Trim(),
            Keys = keys,
            Description = $"Atalho personalizado ({string.Join("+", keys)})"
        });

        NewHotkeyId = string.Empty;
        NewHotkeyName = string.Empty;
        await SaveControlSettingsAsync(ct).ConfigureAwait(false);
        ControlFeedback = $"Atalho '{id}' adicionado.";
    }

    private async Task RemoveHotkeyAsync(RemoteHotkeyDefinition? hotkey, CancellationToken ct)
    {
        if (hotkey is null) return;
        Hotkeys.Remove(hotkey);
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

        ControlFeedback = res.IsSuccess
            ? res.Value.Message
            : res.Error!.Message;
    }

    public async Task SaveNotifySettingsAsync(CancellationToken ct = default)
    {
        var current = await _settingsService.GetNotifySettingsAsync(ct).ConfigureAwait(false);
        var baseSettings = current.IsSuccess ? current.Value : new NotifyModuleSettings();
        var updated = baseSettings with
        {
            IsEnabled = IsNotifyEnabled,
            FilterMode = NotifyFilterMode,
            AllowedApps = ParseCsvApps(AllowedAppsText),
            BlockedApps = ParseCsvApps(BlockedAppsText)
        };

        var saved = await _settingsService.SaveNotifySettingsAsync(updated, ct).ConfigureAwait(false);
        StatusMessage = saved.IsSuccess
            ? "Preferências de filtro do Nexus Notify salvas."
            : saved.Error!.Message;
        await RefreshStatusesAndSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task RefreshNotificationsAsync(CancellationToken ct = default)
    {
        var res = await _notifyService.ListHistoryAsync(80, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            NotificationFeed.Clear();
            foreach (var n in res.Value)
            {
                NotificationFeed.Add(n);
            }
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
            NotificationFeed.Clear();
            StatusMessage = $"{res.Value} notificação(ões) removidas do histórico.";
        }
    }

    public async Task RefreshMonitorAsync(CancellationToken ct = default)
    {
        var snapRes = await _hostMonitor.CollectSnapshotAsync(ct).ConfigureAwait(false);
        if (snapRes.IsFailure)
        {
            CpuUsageText = "Desativado";
            MemorySummaryText = snapRes.Error!.Message;
            Disks.Clear();
            NetworkInterfaces.Clear();
            TopProcesses.Clear();
            return;
        }

        var s = snapRes.Value;
        CpuNameText = s.Cpu.ProcessorName;
        var clockSuffix = s.Cpu.NominalClockMhz.IsAvailable ? $" · {s.Cpu.NominalClockMhz.Value} MHz" : string.Empty;
        CpuUsageText = s.Cpu.UsagePercent.IsAvailable
            ? $"{s.Cpu.UsagePercent.Value:F1}% ({s.Cpu.LogicalCores} threads{clockSuffix})"
            : $"Indisponível ({s.Cpu.UsagePercent.StatusMessage})";

        MemorySummaryText = s.Memory.UsagePercent.IsAvailable
            ? $"{s.Memory.UsagePercent.Value:F1}% · {s.Memory.UsedMb.Value:F0} / {s.Memory.TotalMb.Value:F0} MB"
            : s.Memory.UsagePercent.StatusMessage;

        GpuSummaryText = s.Gpu.IsAvailable
            ? (s.Gpu.AdapterRamMb.IsAvailable ? $"{s.Gpu.AdapterName} ({s.Gpu.AdapterRamMb.Value:F0} MB VRAM)" : s.Gpu.AdapterName)
            : "Indisponível";
        GpuAvailabilityNote = s.Gpu.StatusMessage;

        TemperatureSummaryText = s.Temperatures.IsAvailable
            ? $"{s.Temperatures.PrimaryCelsius.Value:F1} °C"
            : "Indisponível no hardware/SO atual";
        TemperatureStatusNote = s.Temperatures.StatusMessage;

        UptimeText = $"Tempo ativo: {(int)s.Uptime.TotalHours}h {s.Uptime.Minutes}m · {s.OsDescription}";

        Disks.Clear();
        foreach (var d in s.Disks) Disks.Add(d);

        NetworkInterfaces.Clear();
        foreach (var nic in s.NetworkInterfaces) NetworkInterfaces.Add(nic);

        TopProcesses.Clear();
        foreach (var p in s.TopProcesses) TopProcesses.Add(p);
    }

    public async Task RefreshMediaAsync(CancellationToken ct = default)
    {
        var res = await _mediaService.GetCurrentStateAsync(ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            MediaPlaybackBadge = "Desativado";
            MediaVolumeText = "--";
            MediaMuteBadge = "--";
            MediaMetadataNote = res.Error!.Message;
            return;
        }

        ApplyMediaState(res.Value);
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
            StatusMessage = res.Error!.Message;
            return;
        }

        ApplyMediaState(res.Value);
        StatusMessage = $"Mídia: comando {action} executado.";
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
        NotificationFeed.Insert(0, record);
        while (NotificationFeed.Count > 100) NotificationFeed.RemoveAt(NotificationFeed.Count - 1);
    }

    private void OnRealtimeNotificationReceived(object? sender, MobileNotificationRecord record)
    {
        if (!NotificationFeed.Any(n => n.NotificationId == record.NotificationId))
        {
            NotificationFeed.Insert(0, record);
        }
    }

    private void OnRealtimeMediaStateReceived(object? sender, MediaPlaybackStateContract state)
        => ApplyMediaState(state);

    private void OnRealtimeModuleStatusChanged(object? sender, IntegrationModuleStatusSnapshot status)
    {
        var existing = ModuleStatuses.FirstOrDefault(s => s.ModuleKind == status.ModuleKind);
        if (existing is not null)
        {
            var idx = ModuleStatuses.IndexOf(existing);
            ModuleStatuses[idx] = status;
        }
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

