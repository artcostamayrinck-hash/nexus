using System.Collections.ObjectModel;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Notifications;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Modules;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

public sealed class MobileModulesViewModel : MobileObservableObject, IDisposable
{
    private readonly IIntegrationModulesClient _modulesClient;
    private readonly IModuleSettingsService _localSettings;
    private readonly IRealtimeClientChannel _realtimeClient;
    private readonly ILocalDeviceContext _localDevice;

    private string _statusMessage = "Módulos NEXUS prontos para controle remoto.";
    private string _controlResultMessage = "Toque em um comando para controlar o Windows.";
    private bool _isControlEnabled = true;
    private bool _isNotifyEnabled = true;
    private bool _isMonitorEnabled = true;
    private bool _isMediaEnabled = true;

    private int _mouseDeltaX = 24;
    private int _mouseDeltaY;
    private int _scrollDelta = 120;
    private string _remoteTextInput = string.Empty;
    private string _selectedKeyName = "Enter";
    private RemoteControlActionType? _pendingPowerAction;
    private string _powerConfirmationPrompt = string.Empty;

    private string _allowedAppsCsv = string.Empty;
    private string _androidListenerNotice = "Acesso às notificações do Android (NotificationListenerService) requer permissão explícita do usuário nas configurações do SO.";
    private string _testNotifyAppName = "NEXUS Mobile";
    private string _testNotifyTitle = "Notificação encaminhada";
    private string _testNotifyBody = "Mensagem autorizada enviada pelo dispositivo Android.";

    private string _hostCpuSummary = "Toque em Atualizar Monitor para ler hardware Windows";
    private string _hostMemorySummary = "--";
    private string _hostGpuSummary = "--";
    private string _hostTemperatureSummary = "--";

    private string _remoteMediaStatusText = "Aguardando estado";
    private string _remoteMediaVolumeText = "--";
    private string _remoteMediaMetadataNote = string.Empty;
    private int _targetVolumePercent = 50;

    public MobileModulesViewModel(
        IIntegrationModulesClient modulesClient,
        IModuleSettingsService localSettings,
        IRealtimeClientChannel realtimeClient,
        ILocalDeviceContext localDevice)
    {
        _modulesClient = modulesClient;
        _localSettings = localSettings;
        _realtimeClient = realtimeClient;
        _localDevice = localDevice;

        RefreshAllCommand = new MobileAsyncCommand(RefreshAllAsync);
        ToggleControlCommand = new MobileAsyncCommand(ct => ToggleRemoteModuleAsync(IntegrationModuleKind.Control, IsControlEnabled, ct));
        ToggleNotifyCommand = new MobileAsyncCommand(ct => ToggleRemoteModuleAsync(IntegrationModuleKind.Notify, IsNotifyEnabled, ct));
        ToggleMonitorCommand = new MobileAsyncCommand(ct => ToggleRemoteModuleAsync(IntegrationModuleKind.Monitor, IsMonitorEnabled, ct));
        ToggleMediaCommand = new MobileAsyncCommand(ct => ToggleRemoteModuleAsync(IntegrationModuleKind.Media, IsMediaEnabled, ct));

        MouseMoveDeltaCommand = new MobileAsyncCommand(ct => SendControlAsync(RemoteControlActionType.MouseRelativeMove, ct: ct));
        MouseLeftClickCommand = new MobileAsyncCommand(ct => SendControlAsync(RemoteControlActionType.MouseLeftClick, ct: ct));
        MouseRightClickCommand = new MobileAsyncCommand(ct => SendControlAsync(RemoteControlActionType.MouseRightClick, ct: ct));
        MouseDoubleClickCommand = new MobileAsyncCommand(ct => SendControlAsync(RemoteControlActionType.MouseDoubleClick, ct: ct));
        MouseScrollUpCommand = new MobileAsyncCommand(ct => SendControlAsync(RemoteControlActionType.MouseScroll, scrollDelta: Math.Abs(ScrollDelta), ct: ct));
        MouseScrollDownCommand = new MobileAsyncCommand(ct => SendControlAsync(RemoteControlActionType.MouseScroll, scrollDelta: -Math.Abs(ScrollDelta), ct: ct));
        SendTextCommand = new MobileAsyncCommand(SendTextAsync);
        SendKeyCommand = new MobileAsyncCommand(SendKeyAsync);
        SendHotkeyCommand = new MobileAsyncCommand<RemoteHotkeyDefinition>(SendHotkeyAsync);

        RequestLockCommand = new MobileAsyncCommand(_ => StagePowerActionAsync(RemoteControlActionType.PowerLock));
        RequestSleepCommand = new MobileAsyncCommand(_ => StagePowerActionAsync(RemoteControlActionType.PowerSleep));
        RequestShutdownCommand = new MobileAsyncCommand(_ => StagePowerActionAsync(RemoteControlActionType.PowerShutdown));
        ConfirmPowerActionCommand = new MobileAsyncCommand(ConfirmAndExecutePowerAsync);
        CancelPowerConfirmationCommand = new MobileAsyncCommand(_ => ClearPowerConfirmationAsync());

        PushNotificationCommand = new MobileAsyncCommand(PushNotificationAsync);
        RefreshMonitorCommand = new MobileAsyncCommand(RefreshMonitorAsync);

        MediaPlayPauseCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.PlayPause, null, ct));
        MediaNextCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.Next, null, ct));
        MediaPreviousCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.Previous, null, ct));
        MediaStopCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.Stop, null, ct));
        VolumeUpCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.VolumeUp, null, ct));
        VolumeDownCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.VolumeDown, null, ct));
        ToggleMuteCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.ToggleMute, null, ct));
        ApplyVolumeCommand = new MobileAsyncCommand(ct => SendMediaAsync(MediaControlAction.SetVolume, TargetVolumePercent, ct));

        foreach (var hk in ControlModuleSettings.DefaultHotkeys)
        {
            Hotkeys.Add(hk);
        }

        _realtimeClient.MediaStateReceived += OnRealtimeMediaStateReceived;
        _realtimeClient.ModuleStatusChangedReceived += OnRealtimeModuleStatusChanged;
    }

    public ObservableCollection<IntegrationModuleStatusSnapshot> ModuleStatuses { get; } = [];
    public ObservableCollection<RemoteHotkeyDefinition> Hotkeys { get; } = [];
    public ObservableCollection<DiskTelemetryItem> HostDisks { get; } = [];
    public IReadOnlyList<string> AvailableKeys { get; } = ["Enter", "Escape", "Tab", "Space", "Backspace", "Delete", "Up", "Down", "Left", "Right"];

    public MobileAsyncCommand RefreshAllCommand { get; }
    public MobileAsyncCommand ToggleControlCommand { get; }
    public MobileAsyncCommand ToggleNotifyCommand { get; }
    public MobileAsyncCommand ToggleMonitorCommand { get; }
    public MobileAsyncCommand ToggleMediaCommand { get; }
    public MobileAsyncCommand MouseMoveDeltaCommand { get; }
    public MobileAsyncCommand MouseLeftClickCommand { get; }
    public MobileAsyncCommand MouseRightClickCommand { get; }
    public MobileAsyncCommand MouseDoubleClickCommand { get; }
    public MobileAsyncCommand MouseScrollUpCommand { get; }
    public MobileAsyncCommand MouseScrollDownCommand { get; }
    public MobileAsyncCommand SendTextCommand { get; }
    public MobileAsyncCommand SendKeyCommand { get; }
    public MobileAsyncCommand<RemoteHotkeyDefinition> SendHotkeyCommand { get; }
    public MobileAsyncCommand RequestLockCommand { get; }
    public MobileAsyncCommand RequestSleepCommand { get; }
    public MobileAsyncCommand RequestShutdownCommand { get; }
    public MobileAsyncCommand ConfirmPowerActionCommand { get; }
    public MobileAsyncCommand CancelPowerConfirmationCommand { get; }
    public MobileAsyncCommand PushNotificationCommand { get; }
    public MobileAsyncCommand RefreshMonitorCommand { get; }
    public MobileAsyncCommand MediaPlayPauseCommand { get; }
    public MobileAsyncCommand MediaNextCommand { get; }
    public MobileAsyncCommand MediaPreviousCommand { get; }
    public MobileAsyncCommand MediaStopCommand { get; }
    public MobileAsyncCommand VolumeUpCommand { get; }
    public MobileAsyncCommand VolumeDownCommand { get; }
    public MobileAsyncCommand ToggleMuteCommand { get; }
    public MobileAsyncCommand ApplyVolumeCommand { get; }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ControlResultMessage { get => _controlResultMessage; private set => SetProperty(ref _controlResultMessage, value); }
    public bool IsControlEnabled { get => _isControlEnabled; set => SetProperty(ref _isControlEnabled, value); }
    public bool IsNotifyEnabled { get => _isNotifyEnabled; set => SetProperty(ref _isNotifyEnabled, value); }
    public bool IsMonitorEnabled { get => _isMonitorEnabled; set => SetProperty(ref _isMonitorEnabled, value); }
    public bool IsMediaEnabled { get => _isMediaEnabled; set => SetProperty(ref _isMediaEnabled, value); }

    public int MouseDeltaX { get => _mouseDeltaX; set => SetProperty(ref _mouseDeltaX, value); }
    public int MouseDeltaY { get => _mouseDeltaY; set => SetProperty(ref _mouseDeltaY, value); }
    public int ScrollDelta { get => _scrollDelta; set => SetProperty(ref _scrollDelta, value); }
    public string RemoteTextInput { get => _remoteTextInput; set => SetProperty(ref _remoteTextInput, value); }
    public string SelectedKeyName { get => _selectedKeyName; set => SetProperty(ref _selectedKeyName, value); }
    public bool HasPendingPowerConfirmation => _pendingPowerAction.HasValue;
    public string PowerConfirmationPrompt { get => _powerConfirmationPrompt; private set => SetProperty(ref _powerConfirmationPrompt, value); }

    public string AllowedAppsCsv { get => _allowedAppsCsv; set => SetProperty(ref _allowedAppsCsv, value); }
    public string AndroidListenerNotice { get => _androidListenerNotice; private set => SetProperty(ref _androidListenerNotice, value); }
    public string TestNotifyAppName { get => _testNotifyAppName; set => SetProperty(ref _testNotifyAppName, value); }
    public string TestNotifyTitle { get => _testNotifyTitle; set => SetProperty(ref _testNotifyTitle, value); }
    public string TestNotifyBody { get => _testNotifyBody; set => SetProperty(ref _testNotifyBody, value); }

    public string HostCpuSummary { get => _hostCpuSummary; private set => SetProperty(ref _hostCpuSummary, value); }
    public string HostMemorySummary { get => _hostMemorySummary; private set => SetProperty(ref _hostMemorySummary, value); }
    public string HostGpuSummary { get => _hostGpuSummary; private set => SetProperty(ref _hostGpuSummary, value); }
    public string HostTemperatureSummary { get => _hostTemperatureSummary; private set => SetProperty(ref _hostTemperatureSummary, value); }

    public string RemoteMediaStatusText { get => _remoteMediaStatusText; private set => SetProperty(ref _remoteMediaStatusText, value); }
    public string RemoteMediaVolumeText { get => _remoteMediaVolumeText; private set => SetProperty(ref _remoteMediaVolumeText, value); }
    public string RemoteMediaMetadataNote { get => _remoteMediaMetadataNote; private set => SetProperty(ref _remoteMediaMetadataNote, value); }
    public int TargetVolumePercent { get => _targetVolumePercent; set => SetProperty(ref _targetVolumePercent, Math.Clamp(value, 0, 100)); }

    public async Task InitializeAsync(CancellationToken ct = default)
        => await RefreshAllAsync(ct).ConfigureAwait(false);

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var remoteStatuses = await _modulesClient.GetRemoteModuleStatusesAsync(ct).ConfigureAwait(false);
        if (remoteStatuses.IsSuccess)
        {
            ApplyModuleStatuses(remoteStatuses.Value);
            var ctlSettings = await _modulesClient.GetRemoteControlSettingsAsync(ct).ConfigureAwait(false);
            if (ctlSettings.IsSuccess)
            {
                Hotkeys.Clear();
                foreach (var hk in ctlSettings.Value.Hotkeys) Hotkeys.Add(hk);
            }

            await RefreshMonitorAsync(ct).ConfigureAwait(false);
            await RefreshMediaAsync(ct).ConfigureAwait(false);
            StatusMessage = $"Conectado ao host Windows ({ModuleStatuses.Count} módulos).";
        }
        else
        {
            var local = await _localSettings.GetModuleStatusesAsync(ct).ConfigureAwait(false);
            if (local.IsSuccess) ApplyModuleStatuses(local.Value);
            StatusMessage = $"Modo local/offline: {remoteStatuses.Error!.Message}";
        }
    }

    public async Task ToggleRemoteModuleAsync(IntegrationModuleKind kind, bool enabled, CancellationToken ct = default)
    {
        var req = new UpdateModuleToggleRequest(kind, enabled);
        await _localSettings.SetModuleEnabledAsync(req, ct).ConfigureAwait(false);

        var remote = await _modulesClient.SetRemoteModuleEnabledAsync(req, ct).ConfigureAwait(false);
        StatusMessage = remote.IsSuccess
            ? $"{remote.Value.DisplayName}: {(remote.Value.IsEnabled ? "ativado" : "desativado")} no host."
            : $"Local atualizado; servidor: {remote.Error!.Message}";
    }

    public async Task SendMouseMoveDeltaAsync(int dx, int dy, CancellationToken ct = default)
    {
        MouseDeltaX = dx;
        MouseDeltaY = dy;
        await SendControlAsync(RemoteControlActionType.MouseRelativeMove, dx: dx, dy: dy, ct: ct).ConfigureAwait(false);
    }

    private async Task SendControlAsync(
        RemoteControlActionType action,
        int? dx = null,
        int? dy = null,
        int? scrollDelta = null,
        string? keyName = null,
        string? text = null,
        RemoteHotkeyDefinition? hotkey = null,
        bool confirmedByUser = false,
        CancellationToken ct = default)
    {
        var req = new RemoteControlCommandRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            Action = action,
            DeltaX = dx ?? MouseDeltaX,
            DeltaY = dy ?? MouseDeltaY,
            ScrollDelta = scrollDelta ?? ScrollDelta,
            KeyName = keyName,
            Text = text,
            HotkeyId = hotkey?.HotkeyId,
            Keys = hotkey?.Keys,
            ConfirmedByUser = confirmedByUser
        };

        var res = await _modulesClient.SendControlCommandAsync(req, ct).ConfigureAwait(false);
        ControlResultMessage = res.IsSuccess
            ? res.Value.Message
            : res.Error!.Message;
    }

    private async Task SendTextAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(RemoteTextInput))
        {
            ControlResultMessage = "Digite um texto para enviar ao teclado virtual.";
            return;
        }

        await SendControlAsync(RemoteControlActionType.TextInput, text: RemoteTextInput, ct: ct).ConfigureAwait(false);
        RemoteTextInput = string.Empty;
    }

    private Task SendKeyAsync(CancellationToken ct)
        => SendControlAsync(RemoteControlActionType.KeyPress, keyName: SelectedKeyName, ct: ct);

    private Task SendHotkeyAsync(RemoteHotkeyDefinition? hk, CancellationToken ct)
        => hk is null ? Task.CompletedTask : SendControlAsync(RemoteControlActionType.Hotkey, hotkey: hk, ct: ct);

    private Task StagePowerActionAsync(RemoteControlActionType powerAction)
    {
        _pendingPowerAction = powerAction;
        PowerConfirmationPrompt = $"Confirmar ação crítica '{powerAction}' no Windows?";
        OnPropertyChanged(nameof(HasPendingPowerConfirmation));
        return Task.CompletedTask;
    }

    private async Task ConfirmAndExecutePowerAsync(CancellationToken ct)
    {
        if (!_pendingPowerAction.HasValue) return;
        var action = _pendingPowerAction.Value;
        await ClearPowerConfirmationAsync().ConfigureAwait(false);
        await SendControlAsync(action, confirmedByUser: true, ct: ct).ConfigureAwait(false);
    }

    private Task ClearPowerConfirmationAsync()
    {
        _pendingPowerAction = null;
        PowerConfirmationPrompt = string.Empty;
        OnPropertyChanged(nameof(HasPendingPowerConfirmation));
        return Task.CompletedTask;
    }

    public async Task PushNotificationAsync(CancellationToken ct = default)
    {
        if (!IsNotifyEnabled)
        {
            StatusMessage = "Encaminhamento desativado neste dispositivo.";
            return;
        }

        var req = new PushMobileNotificationRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            AppName = TestNotifyAppName,
            PackageName = "com.nexus.mobile.manual",
            Title = TestNotifyTitle,
            Body = TestNotifyBody,
            Urgency = NotificationUrgency.Normal
        };

        var res = await _modulesClient.PushMobileNotificationAsync(req, ct).ConfigureAwait(false);
        StatusMessage = res.IsSuccess
            ? $"{res.Value.FilterOutcome}: {res.Value.Reason}"
            : res.Error!.Message;
    }

    public async Task RefreshMonitorAsync(CancellationToken ct = default)
    {
        var res = await _modulesClient.GetRemoteMonitorSnapshotAsync(ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            HostCpuSummary = res.Error!.Message;
            return;
        }

        var s = res.Value;
        HostCpuSummary = s.Cpu.UsagePercent.IsAvailable
            ? $"CPU: {s.Cpu.UsagePercent.Value:F1}% ({s.Cpu.LogicalCores} threads)"
            : $"CPU: {s.Cpu.UsagePercent.StatusMessage}";

        HostMemorySummary = s.Memory.UsagePercent.IsAvailable
            ? $"RAM: {s.Memory.UsagePercent.Value:F1}% ({s.Memory.UsedMb.Value:F0}/{s.Memory.TotalMb.Value:F0} MB)"
            : $"RAM: {s.Memory.UsagePercent.StatusMessage}";

        HostGpuSummary = s.Gpu.IsAvailable
            ? $"GPU: {s.Gpu.AdapterName}"
            : $"GPU: {s.Gpu.StatusMessage}";

        HostTemperatureSummary = s.Temperatures.IsAvailable
            ? $"Temp: {s.Temperatures.PrimaryCelsius.Value:F1} °C"
            : $"Temp: {s.Temperatures.StatusMessage}";

        HostDisks.Clear();
        foreach (var d in s.Disks) HostDisks.Add(d);
    }

    public async Task RefreshMediaAsync(CancellationToken ct = default)
    {
        var res = await _modulesClient.GetRemoteMediaStateAsync(ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            RemoteMediaStatusText = res.Error!.Message;
            return;
        }

        ApplyMediaState(res.Value);
    }

    private async Task SendMediaAsync(MediaControlAction action, int? volume, CancellationToken ct)
    {
        var res = await _modulesClient.SendMediaCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            Action = action,
            VolumePercent = volume
        }, ct).ConfigureAwait(false);

        if (res.IsSuccess)
        {
            ApplyMediaState(res.Value);
            StatusMessage = $"Mídia remota: {action}";
        }
        else
        {
            StatusMessage = res.Error!.Message;
        }
    }

    private void ApplyMediaState(MediaPlaybackStateContract state)
    {
        RemoteMediaStatusText = $"Estado: {state.PlaybackStatus}";
        RemoteMediaVolumeText = state.VolumePercent.IsAvailable
            ? $"Volume: {state.VolumePercent.Value}% · {(state.IsMuted.IsAvailable && state.IsMuted.Value ? "Mudo" : "Ativo")}"
            : state.VolumePercent.StatusMessage;
        RemoteMediaMetadataNote = state.MetadataAvailabilityNote;
        if (state.VolumePercent.IsAvailable)
        {
            _targetVolumePercent = state.VolumePercent.Value;
            OnPropertyChanged(nameof(TargetVolumePercent));
        }
    }

    private void ApplyModuleStatuses(IReadOnlyList<IntegrationModuleStatusSnapshot> list)
    {
        ModuleStatuses.Clear();
        foreach (var item in list)
        {
            ModuleStatuses.Add(item);
            switch (item.ModuleKind)
            {
                case IntegrationModuleKind.Control: IsControlEnabled = item.IsEnabled; break;
                case IntegrationModuleKind.Notify: IsNotifyEnabled = item.IsEnabled; break;
                case IntegrationModuleKind.Monitor: IsMonitorEnabled = item.IsEnabled; break;
                case IntegrationModuleKind.Media: IsMediaEnabled = item.IsEnabled; break;
            }
        }
    }

    private void OnRealtimeMediaStateReceived(object? sender, MediaPlaybackStateContract state)
        => ApplyMediaState(state);

    private void OnRealtimeModuleStatusChanged(object? sender, IntegrationModuleStatusSnapshot status)
    {
        var existing = ModuleStatuses.FirstOrDefault(m => m.ModuleKind == status.ModuleKind);
        if (existing is not null)
        {
            var idx = ModuleStatuses.IndexOf(existing);
            ModuleStatuses[idx] = status;
        }
    }

    public void Dispose()
    {
        _realtimeClient.MediaStateReceived -= OnRealtimeMediaStateReceived;
        _realtimeClient.ModuleStatusChangedReceived -= OnRealtimeModuleStatusChanged;
    }
}

