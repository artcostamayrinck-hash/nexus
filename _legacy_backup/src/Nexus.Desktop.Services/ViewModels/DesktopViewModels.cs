using System.Collections.ObjectModel;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Events;
using Nexus.Core.Modules;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Updates;
using Nexus.Desktop.Services.Connectivity;
using Nexus.Desktop.Services.Mvvm;
using Nexus.Desktop.Services.Security;
using Nexus.Desktop.Services.Settings;
using Nexus.Infrastructure.Logging;
using Nexus.Infrastructure.Security;
using Nexus.Modules.Abstractions;
using Nexus.Modules.Registry;
using Nexus.Security.Identity;

namespace Nexus.Desktop.Services.ViewModels;

public enum DesktopSection
{
    Dashboard = 0,
    Devices = 1,
    Settings = 2,
    SystemStatus = 3,
    Pairing = 4,
    Connectivity = 5,
    Realtime = 6,
    Sync = 7,
    Transfers = 8,
    Modules = 9,
    Automation = 10
}

public sealed record NavigationTabItem(DesktopSection Section, string Title, string Subtitle, string IconKey);

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly ILocalDeviceContext _localDevice;
    private readonly IDeviceRegistry _deviceRegistry;
    private readonly IConnectionManager _connectionManager;
    private readonly IDesktopSettingsService _settingsService;
    private readonly IServerHealthProbeService _probeService;
    private readonly DiagnosticHealthService _healthService;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly INexusRouteOrchestrator _routeOrchestrator;
    private readonly IEventBus _eventBus;
    private readonly IModuleSettingsService? _moduleSettings;
    private readonly IHostMonitorService? _hostMonitor;
    private readonly IWindowsMediaService? _mediaService;
    private readonly IAutomationEngine? _automationEngine;

    private string _localDeviceName = string.Empty;
    private string _localDeviceIdText = string.Empty;
    private string _localFingerprintDisplay = "-";
    private string _platformBadge = string.Empty;
    private string _connectionBadge = "Desconectado";
    private string _serverProbeDetail = "Aguardando verificação";
    private string _routeSummary = "Rota: Nenhuma";
    private string _vpnSummary = "VPN: Verificando";
    private string _storageHealthBadge = "Inicializando";
    private int _registeredDevicesCount;
    private bool _isServerOnline;
    private string _statusMessage = "Base NEXUS pronta.";
    private string _quickModulesBadge = "Control · Notify · Monitor · Media";
    private string _quickHostTelemetryText = "Aguardando coleta do host...";
    private string _quickMediaSummaryText = "Volume e mídia nativos prontos";

    public DashboardViewModel(
        ILocalDeviceContext localDevice,
        IDeviceRegistry deviceRegistry,
        IConnectionManager connectionManager,
        IDesktopSettingsService settingsService,
        IServerHealthProbeService probeService,
        DiagnosticHealthService healthService,
        ICryptographicIdentityProvider identityProvider,
        INexusRouteOrchestrator routeOrchestrator,
        IEventBus eventBus,
        IModuleSettingsService? moduleSettings = null,
        IHostMonitorService? hostMonitor = null,
        IWindowsMediaService? mediaService = null,
        IAutomationEngine? automationEngine = null,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _localDevice = localDevice;
        _deviceRegistry = deviceRegistry;
        _connectionManager = connectionManager;
        _settingsService = settingsService;
        _probeService = probeService;
        _healthService = healthService;
        _identityProvider = identityProvider;
        _routeOrchestrator = routeOrchestrator;
        _eventBus = eventBus;
        _moduleSettings = moduleSettings;
        _hostMonitor = hostMonitor;
        _mediaService = mediaService;
        _automationEngine = automationEngine;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, uiDispatcher: uiDispatcher);
        ProbeServerCommand = new AsyncRelayCommand(ProbeServerAsync, uiDispatcher: uiDispatcher);
        EmitDiagnosticEventCommand = new AsyncRelayCommand(EmitDiagnosticEventAsync, uiDispatcher: uiDispatcher);
        OpenModulesCommand = new RelayCommand(() => NavigateToSectionAction?.Invoke(DesktopSection.Modules), uiDispatcher: uiDispatcher);
        OpenAutomationCommand = new RelayCommand(() => NavigateToSectionAction?.Invoke(DesktopSection.Automation), uiDispatcher: uiDispatcher);
        QuickMediaPlayPauseCommand = new AsyncRelayCommand(QuickMediaPlayPauseAsync, uiDispatcher: uiDispatcher);
        QuickVolumeUpCommand = new AsyncRelayCommand(QuickVolumeUpAsync, uiDispatcher: uiDispatcher);
        QuickVolumeDownCommand = new AsyncRelayCommand(QuickVolumeDownAsync, uiDispatcher: uiDispatcher);

        _routeOrchestrator.StatusChanged += OnRouteStatusChanged;
    }

    public Action<DesktopSection>? NavigateToSectionAction { get; set; }

    public ObservableCollection<string> RecentActivities { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ProbeServerCommand { get; }
    public AsyncRelayCommand EmitDiagnosticEventCommand { get; }
    public RelayCommand OpenModulesCommand { get; }
    public RelayCommand OpenAutomationCommand { get; }
    public AsyncRelayCommand QuickMediaPlayPauseCommand { get; }
    public AsyncRelayCommand QuickVolumeUpCommand { get; }
    public AsyncRelayCommand QuickVolumeDownCommand { get; }

    public string LocalDeviceName { get => _localDeviceName; private set => SetProperty(ref _localDeviceName, value); }
    public string LocalDeviceIdText { get => _localDeviceIdText; private set => SetProperty(ref _localDeviceIdText, value); }
    public string LocalFingerprintDisplay { get => _localFingerprintDisplay; private set => SetProperty(ref _localFingerprintDisplay, value); }
    public string PlatformBadge { get => _platformBadge; private set => SetProperty(ref _platformBadge, value); }
    public string ConnectionBadge { get => _connectionBadge; private set => SetProperty(ref _connectionBadge, value); }
    public string ServerProbeDetail { get => _serverProbeDetail; private set => SetProperty(ref _serverProbeDetail, value); }
    public string RouteSummary { get => _routeSummary; private set => SetProperty(ref _routeSummary, value); }
    public string VpnSummary { get => _vpnSummary; private set => SetProperty(ref _vpnSummary, value); }
    public string StorageHealthBadge { get => _storageHealthBadge; private set => SetProperty(ref _storageHealthBadge, value); }
    public int RegisteredDevicesCount { get => _registeredDevicesCount; private set => SetProperty(ref _registeredDevicesCount, value); }
    public bool IsServerOnline { get => _isServerOnline; private set => SetProperty(ref _isServerOnline, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string QuickModulesBadge { get => _quickModulesBadge; private set => SetProperty(ref _quickModulesBadge, value); }
    public string QuickHostTelemetryText { get => _quickHostTelemetryText; private set => SetProperty(ref _quickHostTelemetryText, value); }
    public string QuickMediaSummaryText { get => _quickMediaSummaryText; private set => SetProperty(ref _quickMediaSummaryText, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var identity = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        var devices = await _deviceRegistry.GetAllDevicesAsync(cancellationToken).ConfigureAwait(false);
        var health = await _healthService.CaptureAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = _connectionManager.CurrentSnapshot;
        var routeReport = _routeOrchestrator.CurrentStatus;
        var quickModules = await CaptureQuickModulesAsync(cancellationToken).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            if (identity.IsSuccess)
            {
                LocalDeviceName = identity.Value.CustomName;
                LocalDeviceIdText = identity.Value.DeviceId.ToString("D");
                LocalFingerprintDisplay = NexusPairingProtocolClient.FormatFingerprint(identity.Value.PublicKeyFingerprint);
            }
            else
            {
                LocalDeviceName = _localDevice.DeviceName;
                LocalDeviceIdText = _localDevice.DeviceId.ToString("D");
            }

            PlatformBadge = $"{_localDevice.Platform} · {_localDevice.DeviceType}";

            if (devices.IsSuccess)
            {
                RegisteredDevicesCount = devices.Value.Count;
            }

            if (health.IsSuccess)
            {
                StorageHealthBadge = health.Value.IsHealthy ? "SQLite Saudável" : "Armazenamento Indisponível";
            }

            ConnectionBadge = TranslateConnectionState(snapshot.State);
            ApplyRouteSummary(routeReport);
            ApplyQuickModules(quickModules);
            AddActivityCore($"Painel atualizado ({RegisteredDevicesCount} dispositivos registrados).");
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed record QuickModulesSnapshot(string? ModulesBadge, string? TelemetryText, string? MediaSummaryText);

    private async Task<QuickModulesSnapshot> CaptureQuickModulesAsync(CancellationToken ct)
    {
        string? modulesBadge = null;
        string? telemetryText = null;
        string? mediaText = null;

        if (_moduleSettings is not null)
        {
            var modRes = await _moduleSettings.GetModuleStatusesAsync(ct).ConfigureAwait(false);
            if (modRes.IsSuccess)
            {
                var enabled = modRes.Value.Count(m => m.IsEnabled);
                modulesBadge = $"{enabled}/{modRes.Value.Count} módulos ativos";
            }
        }

        if (_hostMonitor is not null)
        {
            var mon = await _hostMonitor.CollectSnapshotAsync(ct).ConfigureAwait(false);
            if (mon.IsSuccess)
            {
                var cpu = mon.Value.Cpu.UsagePercent.IsAvailable ? $"{mon.Value.Cpu.UsagePercent.Value:F0}%" : "N/D";
                var ram = mon.Value.Memory.UsagePercent.IsAvailable ? $"{mon.Value.Memory.UsagePercent.Value:F0}%" : "N/D";
                telemetryText = $"CPU: {cpu} · RAM: {ram} · {mon.Value.Disks.Count} disco(s)";
            }
            else
            {
                telemetryText = mon.Error!.Message;
            }
        }

        if (_mediaService is not null)
        {
            var med = await _mediaService.GetCurrentStateAsync(ct).ConfigureAwait(false);
            if (med.IsSuccess)
            {
                var vol = med.Value.VolumePercent.IsAvailable ? $"{med.Value.VolumePercent.Value}%" : "N/D";
                mediaText = $"Volume: {vol} · {med.Value.PlaybackStatus}";
            }
            else
            {
                mediaText = med.Error!.Message;
            }
        }

        return new QuickModulesSnapshot(modulesBadge, telemetryText, mediaText);
    }

    private void ApplyQuickModules(QuickModulesSnapshot snapshot)
    {
        if (snapshot.ModulesBadge is not null) QuickModulesBadge = snapshot.ModulesBadge;
        if (snapshot.TelemetryText is not null) QuickHostTelemetryText = snapshot.TelemetryText;
        if (snapshot.MediaSummaryText is not null) QuickMediaSummaryText = snapshot.MediaSummaryText;
    }

    private async Task QuickMediaPlayPauseAsync(CancellationToken ct)
    {
        if (_mediaService is null) return;
        var res = await _mediaService.ExecuteCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            Action = MediaControlAction.PlayPause
        }, ct).ConfigureAwait(false);

        var quick = await CaptureQuickModulesAsync(ct).ConfigureAwait(false);
        var msg = res.IsSuccess ? "Ação Play/Pause executada." : res.Error!.Message;
        await RunOnUiAsync(() =>
        {
            ApplyQuickModules(quick);
            StatusMessage = msg;
        }, ct).ConfigureAwait(false);
    }

    private async Task QuickVolumeUpAsync(CancellationToken ct)
    {
        if (_mediaService is null) return;
        var res = await _mediaService.ExecuteCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            Action = MediaControlAction.VolumeUp
        }, ct).ConfigureAwait(false);

        var quick = await CaptureQuickModulesAsync(ct).ConfigureAwait(false);
        var msg = res.IsSuccess ? "Volume aumentado." : res.Error!.Message;
        await RunOnUiAsync(() =>
        {
            ApplyQuickModules(quick);
            StatusMessage = msg;
        }, ct).ConfigureAwait(false);
    }

    private async Task QuickVolumeDownAsync(CancellationToken ct)
    {
        if (_mediaService is null) return;
        var res = await _mediaService.ExecuteCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = _localDevice.DeviceId,
            Action = MediaControlAction.VolumeDown
        }, ct).ConfigureAwait(false);

        var quick = await CaptureQuickModulesAsync(ct).ConfigureAwait(false);
        var msg = res.IsSuccess ? "Volume reduzido." : res.Error!.Message;
        await RunOnUiAsync(() =>
        {
            ApplyQuickModules(quick);
            StatusMessage = msg;
        }, ct).ConfigureAwait(false);
    }

    private void OnRouteStatusChanged(object? sender, ConnectivityStatusReport report)
        => RunOnUi(() => ApplyRouteSummary(report));

    private void ApplyRouteSummary(ConnectivityStatusReport report)
    {
        var routeLabel = ConnectivityViewModel.FormatRouteKind(report.RouteKind);
        var latencyPart = report.LatencyMs.HasValue ? $" · {report.LatencyMs} ms" : string.Empty;
        RouteSummary = $"Rota: {routeLabel}{latencyPart}";
        VpnSummary = report.VpnDetail ?? report.VpnStatus.ToString();
    }

    public async Task ProbeServerAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = settings.IsSuccess ? settings.Value.ServerEndpoint : "http://127.0.0.1:5280";

        var probe = await _probeService.ProbeAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            if (probe.IsSuccess)
            {
                IsServerOnline = probe.Value.IsReachable;
                ConnectionBadge = probe.Value.StatusLabel;
                ServerProbeDetail = $"{endpoint} · {probe.Value.Detail ?? "OK"}";
                StatusMessage = probe.Value.IsReachable
                    ? $"Conexão verificada com {endpoint}"
                    : $"Servidor local indisponível em {endpoint}";
                AddActivityCore($"Probe de servidor ({endpoint}): {probe.Value.StatusLabel}");
            }
            else
            {
                StatusMessage = probe.Error!.Message;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitDiagnosticEventAsync(CancellationToken cancellationToken)
    {
        await _eventBus.PublishAsync(
            new DiagnosticDomainEvent("Dashboard", "Evento de diagnóstico manual disparado pelo usuário."),
            cancellationToken).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            AddActivityCore("Evento de diagnóstico publicado no barramento interno.");
            StatusMessage = "Evento publicado no EventBus.";
        }, cancellationToken).ConfigureAwait(false);
    }

    public void AddActivity(string text) => RunOnUi(() => AddActivityCore(text));

    private void AddActivityCore(string text)
        => PrependCapped(RecentActivities, $"[{DateTime.Now:HH:mm:ss}] {text}", 15);

    private static string TranslateConnectionState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Conectado",
        ConnectionState.Connecting => "Conectando",
        ConnectionState.Discovering => "Descobrindo",
        ConnectionState.Reconnecting => "Reconectando",
        ConnectionState.Degraded => "Degradado",
        ConnectionState.Faulted => "Falha",
        _ => "Desconectado"
    };

    public void Dispose()
    {
        _routeOrchestrator.StatusChanged -= OnRouteStatusChanged;
    }
}

public sealed record TrustedDevicePresenceItem(
    TrustedDeviceRecord Device,
    bool IsRealtimeConnected,
    ConnectionState ConnectionState,
    string ConnectionBadgeText);

public sealed class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceRegistry _deviceRegistry;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IDesktopPairingClient _pairingClient;
    private readonly IRealtimeClientChannel? _realtimeClient;

    private string _newDeviceName = "Pixel 9 Pro (Android)";
    private DevicePlatform _selectedPlatform = DevicePlatform.Android;
    private DeviceType _selectedType = DeviceType.Phone;
    private string _feedbackMessage = string.Empty;
    private DeviceInfo? _selectedDevice;

    private TrustedDeviceRecord? _selectedTrustedDevice;
    private TrustedDevicePresenceItem? _selectedTrustedItem;
    private string _editTrustedName = string.Empty;
    private string _revokeReason = "Revogado pelo usuário no Windows";
    private string _selectedFingerprintDisplay = "-";
    private string _selectedPairedAtText = "-";
    private string _selectedLastAccessText = "-";
    private string _selectedTrustBadge = "-";
    private string _selectedConnectionBadge = "Desconectado (Offline)";
    private int _authorizedTrustedCount;
    private int _connectedTrustedCount;

    private bool _permViewStatus;
    private bool _permNotifications;
    private bool _permClipboard;
    private bool _permFileTransfer;
    private bool _permRemoteCommands;
    private bool _permManageDevices;

    public DevicesViewModel(
        IDeviceRegistry deviceRegistry,
        ILocalDeviceContext localDevice,
        IDesktopPairingClient pairingClient,
        IRealtimeClientChannel? realtimeClient = null,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _deviceRegistry = deviceRegistry;
        _localDevice = localDevice;
        _pairingClient = pairingClient;
        _realtimeClient = realtimeClient;

        LoadDevicesCommand = new AsyncRelayCommand(LoadAllAsync, uiDispatcher: uiDispatcher);
        RegisterSampleDeviceCommand = new AsyncRelayCommand(RegisterDeviceAsync, uiDispatcher: uiDispatcher);
        RemoveSelectedDeviceCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedDevice is not null && SelectedDevice.Id != _localDevice.DeviceId, uiDispatcher: uiDispatcher);

        LoadTrustedDevicesCommand = new AsyncRelayCommand(LoadTrustedDevicesAsync, uiDispatcher: uiDispatcher);
        RenameTrustedDeviceCommand = new AsyncRelayCommand(RenameTrustedAsync, () => SelectedTrustedDevice is not null, uiDispatcher: uiDispatcher);
        UpdateTrustedPermissionsCommand = new AsyncRelayCommand(UpdateTrustedPermissionsAsync, () => SelectedTrustedDevice is not null, uiDispatcher: uiDispatcher);
        RevokeTrustedDeviceCommand = new AsyncRelayCommand(RevokeTrustedAsync, () => SelectedTrustedDevice is not null && SelectedTrustedDevice.TrustStatus != DeviceTrustStatus.Revoked, uiDispatcher: uiDispatcher);
        RemoveTrustedDeviceCommand = new AsyncRelayCommand(RemoveTrustedAsync, () => SelectedTrustedDevice is not null, uiDispatcher: uiDispatcher);

        if (_realtimeClient is not null)
        {
            _realtimeClient.PresenceUpdated += OnRealtimePresenceUpdated;
            _realtimeClient.StateChanged += OnRealtimeStateChanged;
        }
    }

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<TrustedDeviceRecord> TrustedDevices { get; } = [];
    public ObservableCollection<TrustedDevicePresenceItem> TrustedDeviceItems { get; } = [];
    public IReadOnlyList<DevicePlatform> AvailablePlatforms { get; } = [DevicePlatform.Android, DevicePlatform.Windows, DevicePlatform.Linux];
    public IReadOnlyList<DeviceType> AvailableTypes { get; } = [DeviceType.Phone, DeviceType.Tablet, DeviceType.Desktop, DeviceType.Laptop];

    public AsyncRelayCommand LoadDevicesCommand { get; }
    public AsyncRelayCommand RegisterSampleDeviceCommand { get; }
    public AsyncRelayCommand RemoveSelectedDeviceCommand { get; }

    public AsyncRelayCommand LoadTrustedDevicesCommand { get; }
    public AsyncRelayCommand RenameTrustedDeviceCommand { get; }
    public AsyncRelayCommand UpdateTrustedPermissionsCommand { get; }
    public AsyncRelayCommand RevokeTrustedDeviceCommand { get; }
    public AsyncRelayCommand RemoveTrustedDeviceCommand { get; }

    public string NewDeviceName { get => _newDeviceName; set => SetProperty(ref _newDeviceName, value); }
    public DevicePlatform SelectedPlatform { get => _selectedPlatform; set => SetProperty(ref _selectedPlatform, value); }
    public DeviceType SelectedType { get => _selectedType; set => SetProperty(ref _selectedType, value); }
    public string FeedbackMessage { get => _feedbackMessage; private set => SetProperty(ref _feedbackMessage, value); }

    public string EditTrustedName { get => _editTrustedName; set => SetProperty(ref _editTrustedName, value); }
    public string RevokeReason { get => _revokeReason; set => SetProperty(ref _revokeReason, value); }
    public string SelectedFingerprintDisplay { get => _selectedFingerprintDisplay; private set => SetProperty(ref _selectedFingerprintDisplay, value); }
    public string SelectedPairedAtText { get => _selectedPairedAtText; private set => SetProperty(ref _selectedPairedAtText, value); }
    public string SelectedLastAccessText { get => _selectedLastAccessText; private set => SetProperty(ref _selectedLastAccessText, value); }
    public string SelectedTrustBadge { get => _selectedTrustBadge; private set => SetProperty(ref _selectedTrustBadge, value); }
    public string SelectedConnectionBadge { get => _selectedConnectionBadge; private set => SetProperty(ref _selectedConnectionBadge, value); }
    public int AuthorizedTrustedCount { get => _authorizedTrustedCount; private set => SetProperty(ref _authorizedTrustedCount, value); }
    public int ConnectedTrustedCount { get => _connectedTrustedCount; private set => SetProperty(ref _connectedTrustedCount, value); }
    public bool HasSelectedTrusted => SelectedTrustedDevice is not null;

    public TrustedDevicePresenceItem? SelectedTrustedItem
    {
        get => _selectedTrustedItem;
        set
        {
            if (SetProperty(ref _selectedTrustedItem, value) && value?.Device.DeviceId != _selectedTrustedDevice?.DeviceId)
            {
                SelectedTrustedDevice = value?.Device;
            }
        }
    }

    public bool PermViewStatus { get => _permViewStatus; set => SetProperty(ref _permViewStatus, value); }
    public bool PermNotifications { get => _permNotifications; set => SetProperty(ref _permNotifications, value); }
    public bool PermClipboard { get => _permClipboard; set => SetProperty(ref _permClipboard, value); }
    public bool PermFileTransfer { get => _permFileTransfer; set => SetProperty(ref _permFileTransfer, value); }
    public bool PermRemoteCommands { get => _permRemoteCommands; set => SetProperty(ref _permRemoteCommands, value); }
    public bool PermManageDevices { get => _permManageDevices; set => SetProperty(ref _permManageDevices, value); }

    public DeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RemoveSelectedDeviceCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public TrustedDeviceRecord? SelectedTrustedDevice
    {
        get => _selectedTrustedDevice;
        set
        {
            if (SetProperty(ref _selectedTrustedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelectedTrusted));
                PopulateTrustedInspector(value);
                RenameTrustedDeviceCommand.NotifyCanExecuteChanged();
                UpdateTrustedPermissionsCommand.NotifyCanExecuteChanged();
                RevokeTrustedDeviceCommand.NotifyCanExecuteChanged();
                RemoveTrustedDeviceCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await LoadTrustedDevicesAsync(cancellationToken).ConfigureAwait(false);
        await LoadDevicesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        Guid? previousSelectedId = null;
        await RunOnUiAsync(() => previousSelectedId = SelectedTrustedDevice?.DeviceId, cancellationToken).ConfigureAwait(false);

        var result = await _pairingClient.ListTrustedDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var items = result.Value;
        await RunOnUiAsync(() =>
        {
            ReplaceCollection(TrustedDevices, items);
            AuthorizedTrustedCount = TrustedDevices.Count(d => d.TrustStatus == DeviceTrustStatus.Authorized);
            RebuildTrustedPresenceItems();

            SelectedTrustedDevice = previousSelectedId.HasValue
                ? TrustedDevices.FirstOrDefault(d => d.DeviceId == previousSelectedId.Value) ?? TrustedDevices.FirstOrDefault()
                : TrustedDevices.FirstOrDefault();

            FeedbackMessage = $"{AuthorizedTrustedCount} autorizado(s) · {ConnectedTrustedCount} conectado(s) agora · {TrustedDevices.Count} no catálogo.";
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _deviceRegistry.GetAllDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var items = result.Value;
        await RunOnUiAsync(() => ReplaceCollection(Devices, items), cancellationToken).ConfigureAwait(false);
    }

    private async Task RenameTrustedAsync(CancellationToken cancellationToken)
    {
        Guid? targetId = null;
        string newName = string.Empty;
        await RunOnUiAsync(() =>
        {
            targetId = SelectedTrustedDevice?.DeviceId;
            newName = EditTrustedName;
        }, cancellationToken).ConfigureAwait(false);

        if (!targetId.HasValue)
        {
            return;
        }

        var result = await _pairingClient.RenameTrustedDeviceAsync(targetId.Value, newName, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var updatedName = result.Value.CustomName;
        await RunOnUiAsync(() => FeedbackMessage = $"Dispositivo renomeado para '{updatedName}'.", cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateTrustedPermissionsAsync(CancellationToken cancellationToken)
    {
        Guid? targetId = null;
        var permissions = NexusPermission.None;

        await RunOnUiAsync(() =>
        {
            targetId = SelectedTrustedDevice?.DeviceId;
            if (PermViewStatus) permissions |= NexusPermission.ViewStatus;
            if (PermNotifications) permissions |= NexusPermission.Notifications;
            if (PermClipboard) permissions |= NexusPermission.Clipboard;
            if (PermFileTransfer) permissions |= NexusPermission.FileTransfer;
            if (PermRemoteCommands) permissions |= NexusPermission.RemoteCommands;
            if (PermManageDevices) permissions |= NexusPermission.ManageDevices;
        }, cancellationToken).ConfigureAwait(false);

        if (!targetId.HasValue)
        {
            return;
        }

        var result = await _pairingClient.UpdatePermissionsAsync(targetId.Value, permissions, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await LoadTrustedDevicesAsync(cancellationToken).ConfigureAwait(false);
        var updatedName = result.Value.CustomName;
        await RunOnUiAsync(() => FeedbackMessage = $"Permissões atualizadas para '{updatedName}'.", cancellationToken).ConfigureAwait(false);
    }

    private async Task RevokeTrustedAsync(CancellationToken cancellationToken)
    {
        Guid? targetId = null;
        string targetName = string.Empty;
        string reason = string.Empty;

        await RunOnUiAsync(() =>
        {
            targetId = SelectedTrustedDevice?.DeviceId;
            targetName = SelectedTrustedDevice?.CustomName ?? string.Empty;
            reason = RevokeReason;
        }, cancellationToken).ConfigureAwait(false);

        if (!targetId.HasValue)
        {
            return;
        }

        var result = await _pairingClient.RevokeTrustedDeviceAsync(targetId.Value, reason, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(
            () => FeedbackMessage = $"Acesso de '{targetName}' revogado. Tokens e sessão invalidados.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveTrustedAsync(CancellationToken cancellationToken)
    {
        Guid? targetId = null;
        string targetName = string.Empty;

        await RunOnUiAsync(() =>
        {
            targetId = SelectedTrustedDevice?.DeviceId;
            targetName = SelectedTrustedDevice?.CustomName ?? string.Empty;
        }, cancellationToken).ConfigureAwait(false);

        if (!targetId.HasValue)
        {
            return;
        }

        var result = await _pairingClient.RemoveTrustedDeviceAsync(targetId.Value, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(
            () => FeedbackMessage = $"Confiança de '{targetName}' removida permanentemente.",
            cancellationToken).ConfigureAwait(false);
    }

    private void OnRealtimePresenceUpdated(object? sender, IReadOnlyList<DevicePresenceSnapshot> _)
        => RunOnUi(RebuildTrustedPresenceItems);

    private void OnRealtimeStateChanged(object? sender, ConnectionStateSnapshot _)
        => RunOnUi(RebuildTrustedPresenceItems);

    private void PopulateTrustedInspector(TrustedDeviceRecord? record)
    {
        if (record is null)
        {
            EditTrustedName = string.Empty;
            SelectedFingerprintDisplay = "-";
            SelectedPairedAtText = "-";
            SelectedLastAccessText = "-";
            SelectedTrustBadge = "-";
            SelectedConnectionBadge = "Desconectado (Offline)";
            PermViewStatus = PermNotifications = PermClipboard = PermFileTransfer = PermRemoteCommands = PermManageDevices = false;
            return;
        }

        var presence = _realtimeClient?.LivePresence.FirstOrDefault(p => p.DeviceId == record.DeviceId);
        var isLiveConnected = record.TrustStatus == DeviceTrustStatus.Authorized &&
                              _realtimeClient?.IsConnected == true &&
                              presence?.IsRealtimeConnected == true;

        SelectedConnectionBadge = record.TrustStatus == DeviceTrustStatus.Revoked
            ? "Revogado · Desconectado"
            : isLiveConnected
                ? "Conectado em tempo real"
                : "Desconectado (Offline)";

        if (_selectedTrustedItem?.Device.DeviceId != record.DeviceId)
        {
            SelectedTrustedItem = TrustedDeviceItems.FirstOrDefault(i => i.Device.DeviceId == record.DeviceId);
        }

        EditTrustedName = record.CustomName;
        SelectedFingerprintDisplay = NexusPairingProtocolClient.FormatFingerprint(record.PublicKeyFingerprint);
        SelectedPairedAtText = $"{record.PairedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
        SelectedLastAccessText = $"{record.LastAccessAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
        SelectedTrustBadge = record.TrustStatus switch
        {
            DeviceTrustStatus.Authorized => "Autorizado",
            DeviceTrustStatus.Revoked => $"Revogado ({record.RevokedAtUtc?.ToLocalTime():dd/MM HH:mm})",
            _ => "Pendente"
        };

        PermViewStatus = record.Permissions.HasFlag(NexusPermission.ViewStatus);
        PermNotifications = record.Permissions.HasFlag(NexusPermission.Notifications);
        PermClipboard = record.Permissions.HasFlag(NexusPermission.Clipboard);
        PermFileTransfer = record.Permissions.HasFlag(NexusPermission.FileTransfer);
        PermRemoteCommands = record.Permissions.HasFlag(NexusPermission.RemoteCommands);
        PermManageDevices = record.Permissions.HasFlag(NexusPermission.ManageDevices);
    }

    private void RebuildTrustedPresenceItems()
    {
        var liveMap = (_realtimeClient?.IsConnected == true
                ? _realtimeClient.LivePresence
                : Array.Empty<DevicePresenceSnapshot>())
            .ToDictionary(p => p.DeviceId);

        var nextItems = new List<TrustedDevicePresenceItem>(TrustedDevices.Count);
        foreach (var device in TrustedDevices)
        {
            var isLive = device.TrustStatus == DeviceTrustStatus.Authorized &&
                         liveMap.TryGetValue(device.DeviceId, out var presence) &&
                         presence.IsRealtimeConnected;

            var badge = device.TrustStatus == DeviceTrustStatus.Revoked
                ? "Revogado"
                : isLive
                    ? "Conectado"
                    : "Offline";

            nextItems.Add(new TrustedDevicePresenceItem(
                device,
                isLive,
                isLive ? ConnectionState.Connected : ConnectionState.Disconnected,
                badge));
        }

        ReplaceCollection(TrustedDeviceItems, nextItems);
        ConnectedTrustedCount = TrustedDeviceItems.Count(i => i.IsRealtimeConnected);
        PopulateTrustedInspector(SelectedTrustedDevice);
    }

    private async Task RegisterDeviceAsync(CancellationToken cancellationToken)
    {
        string name = string.Empty;
        DevicePlatform platform = DevicePlatform.Android;
        DeviceType type = DeviceType.Phone;

        await RunOnUiAsync(() =>
        {
            name = NewDeviceName;
            platform = SelectedPlatform;
            type = SelectedType;
        }, cancellationToken).ConfigureAwait(false);

        var registration = new DeviceRegistrationContract
        {
            DeviceId = Guid.CreateVersion7(),
            Name = name,
            Platform = platform,
            Type = type,
            OperatingSystemVersion = platform == DevicePlatform.Android ? "Android 15" : Environment.OSVersion.VersionString,
            AppVersion = "0.3.0",
            Capabilities = DeviceCapability.Notifications | DeviceCapability.Clipboard | DeviceCapability.FileTransfer | DeviceCapability.Telemetry
        };

        var result = await _deviceRegistry.RegisterOrUpdateAsync(registration, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        await LoadDevicesAsync(cancellationToken).ConfigureAwait(false);
        var savedName = result.Value.Name;
        await RunOnUiAsync(() => FeedbackMessage = $"Dispositivo '{savedName}' registrado e validado.", cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveSelectedAsync(CancellationToken cancellationToken)
    {
        Guid? selectedId = null;
        await RunOnUiAsync(() => selectedId = SelectedDevice?.Id, cancellationToken).ConfigureAwait(false);

        if (!selectedId.HasValue)
        {
            return;
        }

        var removed = await _deviceRegistry.RemoveDeviceAsync(selectedId.Value, cancellationToken).ConfigureAwait(false);
        if (removed.IsSuccess && removed.Value)
        {
            await LoadDevicesAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUiAsync(() => FeedbackMessage = "Dispositivo removido.", cancellationToken).ConfigureAwait(false);
        }
        else if (removed.IsFailure)
        {
            var errMsg = removed.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_realtimeClient is not null)
        {
            _realtimeClient.PresenceUpdated -= OnRealtimePresenceUpdated;
            _realtimeClient.StateChanged -= OnRealtimeStateChanged;
        }
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IDesktopSettingsService _settingsService;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly Nexus.Core.Updates.INexusUpdateService? _updateService;

    private string _deviceName = Environment.MachineName;
    private string _serverEndpoint = "http://127.0.0.1:5280";
    private bool _autoProbeServer = true;
    private bool _enableSubtleAnimations = true;
    private string _selectedLogLevel = "Information";
    private string _feedbackMessage = string.Empty;
    private string _appVersionDisplay = "1.0.0";
    private string _updateRepositoryOwner = "nexus-ecosystem";
    private string _updateRepositoryName = "nexus";
    private string _updateCheckStatusText = "Sem verificação recente de GitHub Releases.";

    public SettingsViewModel(
        IDesktopSettingsService settingsService,
        ICryptographicIdentityProvider identityProvider,
        Nexus.Core.Updates.INexusUpdateService? updateService = null,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _settingsService = settingsService;
        _identityProvider = identityProvider;
        _updateService = updateService;
        LoadCommand = new AsyncRelayCommand(LoadAsync, uiDispatcher: uiDispatcher);
        SaveCommand = new AsyncRelayCommand(SaveAsync, uiDispatcher: uiDispatcher);
        ResetCommand = new AsyncRelayCommand(ResetAsync, uiDispatcher: uiDispatcher);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, uiDispatcher: uiDispatcher);
    }

    public IReadOnlyList<string> LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }

    public string DeviceName { get => _deviceName; set => SetProperty(ref _deviceName, value); }
    public string ServerEndpoint { get => _serverEndpoint; set => SetProperty(ref _serverEndpoint, value); }
    public bool AutoProbeServer { get => _autoProbeServer; set => SetProperty(ref _autoProbeServer, value); }
    public bool EnableSubtleAnimations { get => _enableSubtleAnimations; set => SetProperty(ref _enableSubtleAnimations, value); }
    public string SelectedLogLevel { get => _selectedLogLevel; set => SetProperty(ref _selectedLogLevel, value); }
    public string FeedbackMessage { get => _feedbackMessage; private set => SetProperty(ref _feedbackMessage, value); }
    public string AppVersionDisplay { get => _appVersionDisplay; private set => SetProperty(ref _appVersionDisplay, value); }
    public string UpdateRepositoryOwner { get => _updateRepositoryOwner; set => SetProperty(ref _updateRepositoryOwner, value); }
    public string UpdateRepositoryName { get => _updateRepositoryName; set => SetProperty(ref _updateRepositoryName, value); }
    public string UpdateCheckStatusText { get => _updateCheckStatusText; private set => SetProperty(ref _updateCheckStatusText, value); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        Nexus.Contracts.Updates.InstalledAppVersionInfo? installed = null;
        Nexus.Contracts.Updates.UpdateConfiguration? cfg = null;
        if (_updateService is not null)
        {
            installed = await _updateService.GetInstalledVersionAsync(cancellationToken).ConfigureAwait(false);
            cfg = await _updateService.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }

        await RunOnUiAsync(() =>
        {
            if (loaded.IsSuccess)
            {
                ApplyModel(loaded.Value);
                FeedbackMessage = "Configurações carregadas do SQLite.";
            }
            else
            {
                FeedbackMessage = loaded.Error!.Message;
            }

            if (installed is not null && cfg is not null)
            {
                AppVersionDisplay = $"v{installed.Version} ({installed.PackageId})";
                UpdateRepositoryOwner = cfg.RepositoryOwner;
                UpdateRepositoryName = cfg.RepositoryName;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_updateService is null)
        {
            await RunOnUiAsync(
                () => UpdateCheckStatusText = "Serviço Nexus Update não registrado neste host.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string owner = string.Empty;
        string repo = string.Empty;
        await RunOnUiAsync(() =>
        {
            owner = UpdateRepositoryOwner;
            repo = UpdateRepositoryName;
        }, cancellationToken).ConfigureAwait(false);

        var cfg = await _updateService.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var savedCfg = await _updateService.SaveConfigurationAsync(cfg with
        {
            RepositoryOwner = owner,
            RepositoryName = repo
        }, cancellationToken).ConfigureAwait(false);

        if (savedCfg.IsFailure)
        {
            var errMsg = savedCfg.Error!.Message;
            await RunOnUiAsync(() => UpdateCheckStatusText = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var res = await _updateService.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
        var status = res.IsSuccess ? res.Value.Message : res.Error!.Message;
        await RunOnUiAsync(() => UpdateCheckStatusText = status, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        DesktopSettingsModel model = new();
        await RunOnUiAsync(() =>
        {
            model = new DesktopSettingsModel
            {
                DeviceName = DeviceName,
                ServerEndpoint = ServerEndpoint,
                AutoProbeServer = AutoProbeServer,
                EnableSubtleAnimations = EnableSubtleAnimations,
                LogLevel = SelectedLogLevel
            };
        }, cancellationToken).ConfigureAwait(false);

        var saved = await _settingsService.SaveAsync(model, cancellationToken).ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            await _identityProvider.RenameLocalDeviceAsync(model.DeviceName, cancellationToken).ConfigureAwait(false);
            await RunOnUiAsync(
                () => FeedbackMessage = $"Preferências e identidade salvas às {DateTime.Now:HH:mm:ss}.",
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var errMsg = saved.Error!.Message;
            await RunOnUiAsync(() => FeedbackMessage = errMsg, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        var reset = await _settingsService.ResetToDefaultsAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            if (reset.IsSuccess)
            {
                ApplyModel(reset.Value);
                FeedbackMessage = "Configurações restauradas para os padrões.";
            }
            else
            {
                FeedbackMessage = reset.Error!.Message;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyModel(DesktopSettingsModel model)
    {
        DeviceName = model.DeviceName;
        ServerEndpoint = model.ServerEndpoint;
        AutoProbeServer = model.AutoProbeServer;
        EnableSubtleAnimations = model.EnableSubtleAnimations;
        SelectedLogLevel = model.LogLevel;
    }
}

public sealed class SystemStatusViewModel : ObservableObject
{
    private readonly DiagnosticHealthService _healthService;
    private readonly IModuleRegistry _moduleRegistry;
    private readonly InMemoryLogSink _logSink;

    private string _storageRootPath = string.Empty;
    private string _databaseSizeText = "0 KB";
    private int _managedFilesCount;
    private string _overallHealthLabel = "Saudável";
    private string _lastCheckedText = string.Empty;

    public SystemStatusViewModel(
        DiagnosticHealthService healthService,
        IModuleRegistry moduleRegistry,
        InMemoryLogSink logSink,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _healthService = healthService;
        _moduleRegistry = moduleRegistry;
        _logSink = logSink;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, uiDispatcher: uiDispatcher);
    }

    public ObservableCollection<ModuleDescriptor> Modules { get; } = [];
    public ObservableCollection<LogEntrySnapshot> RecentLogs { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }

    public string StorageRootPath { get => _storageRootPath; private set => SetProperty(ref _storageRootPath, value); }
    public string DatabaseSizeText { get => _databaseSizeText; private set => SetProperty(ref _databaseSizeText, value); }
    public int ManagedFilesCount { get => _managedFilesCount; private set => SetProperty(ref _managedFilesCount, value); }
    public string OverallHealthLabel { get => _overallHealthLabel; private set => SetProperty(ref _overallHealthLabel, value); }
    public string LastCheckedText { get => _lastCheckedText; private set => SetProperty(ref _lastCheckedText, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var report = await _healthService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var descriptors = _moduleRegistry.GetDescriptors().ToArray();
        var logs = _logSink.GetRecent(60).ToArray();

        await RunOnUiAsync(() =>
        {
            if (report.IsSuccess)
            {
                OverallHealthLabel = report.Value.IsHealthy ? "Operacional" : "Atenção";
                StorageRootPath = report.Value.Storage?.RootDirectory ?? "-";
                DatabaseSizeText = report.Value.Storage is not null
                    ? $"{Math.Max(1, report.Value.Storage.DatabaseSizeBytes / 1024.0):F1} KB"
                    : "N/A";
                ManagedFilesCount = report.Value.Storage?.ManagedFileCount ?? 0;
                LastCheckedText = $"Atualizado às {report.Value.CheckedAtUtc.ToLocalTime():HH:mm:ss}";
            }

            ReplaceCollection(Modules, descriptors);
            ReplaceCollection(RecentLogs, logs);
        }, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MainShellViewModel : ObservableObject, IDisposable
{
    private readonly IStorageInitializer _storageInitializer;
    private readonly IModuleRegistry _moduleRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IDeviceRegistry _deviceRegistry;
    private readonly IStateManager<NexusRuntimeState> _stateManager;

    private DesktopSection _activeSection = DesktopSection.Dashboard;
    private string _footerStatusText = "Inicializando fundação NEXUS...";
    private string _footerDeviceText = string.Empty;
    private bool _isStorageReady;
    private bool _isInitializing;

    public MainShellViewModel(
        IStorageInitializer storageInitializer,
        IModuleRegistry moduleRegistry,
        IServiceProvider serviceProvider,
        ILocalDeviceContext localDevice,
        IDeviceRegistry deviceRegistry,
        IStateManager<NexusRuntimeState> stateManager,
        DashboardViewModel dashboard,
        ConnectivityViewModel connectivity,
        RealtimeViewModel realtime,
        SyncViewModel sync,
        TransferViewModel transfers,
        ModulesViewModel modules,
        AutomationViewModel automation,
        PairingViewModel pairing,
        DevicesViewModel devices,
        SettingsViewModel settings,
        SystemStatusViewModel systemStatus,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _storageInitializer = storageInitializer;
        _moduleRegistry = moduleRegistry;
        _serviceProvider = serviceProvider;
        _localDevice = localDevice;
        _deviceRegistry = deviceRegistry;
        _stateManager = stateManager;

        Dashboard = dashboard;
        Connectivity = connectivity;
        Realtime = realtime;
        Sync = sync;
        Transfers = transfers;
        Modules = modules;
        Automation = automation;
        Pairing = pairing;
        Devices = devices;
        Settings = settings;
        SystemStatus = systemStatus;

        Dashboard.NavigateToSectionAction = section => RunOnUi(() => ActiveSection = section);

        NavigationTabs =
        [
            new NavigationTabItem(DesktopSection.Dashboard, "Dashboard", "Visão geral", "Home"),
            new NavigationTabItem(DesktopSection.Connectivity, "Conectividade", "Local, mDNS & Tailscale", "Network"),
            new NavigationTabItem(DesktopSection.Realtime, "Tempo Real", "SignalR, Comandos & ACK", "Pulse"),
            new NavigationTabItem(DesktopSection.Sync, "Sincronização", "Clipboard, Notas & Conflitos", "Sync"),
            new NavigationTabItem(DesktopSection.Transfers, "Transferências", "Arquivos HTTP & Fila", "Transfer"),
            new NavigationTabItem(DesktopSection.Modules, "Módulos", "Control, Notify, Monitor & Media", "Modules"),
            new NavigationTabItem(DesktopSection.Automation, "Painel & Automações", "Widgets, Perfis & Regras", "Automation"),
            new NavigationTabItem(DesktopSection.Pairing, "Pareamento", "QR Code & Confiança", "QrCode"),
            new NavigationTabItem(DesktopSection.Devices, "Dispositivos", "Confiança & Acesso", "Devices"),
            new NavigationTabItem(DesktopSection.SystemStatus, "Status do Sistema", "Saúde & Logs", "Pulse"),
            new NavigationTabItem(DesktopSection.Settings, "Configurações", "Preferências", "Settings")
        ];

        _stateManager.StateChanged += OnRuntimeStateChanged;
    }

    public IReadOnlyList<NavigationTabItem> NavigationTabs { get; }
    public DashboardViewModel Dashboard { get; }
    public ConnectivityViewModel Connectivity { get; }
    public RealtimeViewModel Realtime { get; }
    public SyncViewModel Sync { get; }
    public TransferViewModel Transfers { get; }
    public ModulesViewModel Modules { get; }
    public AutomationViewModel Automation { get; }
    public PairingViewModel Pairing { get; }
    public DevicesViewModel Devices { get; }
    public SettingsViewModel Settings { get; }
    public SystemStatusViewModel SystemStatus { get; }

    public DesktopSection ActiveSection
    {
        get => _activeSection;
        set => SetProperty(ref _activeSection, value);
    }

    public string FooterStatusText { get => _footerStatusText; private set => SetProperty(ref _footerStatusText, value); }
    public string FooterDeviceText { get => _footerDeviceText; private set => SetProperty(ref _footerDeviceText, value); }
    public bool IsStorageReady { get => _isStorageReady; private set => SetProperty(ref _isStorageReady, value); }
    public bool IsInitializing { get => _isInitializing; private set => SetProperty(ref _isInitializing, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RunOnUiAsync(() =>
        {
            IsInitializing = true;
            FooterStatusText = "Inicializando núcleo local NEXUS...";
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var init = await _storageInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var storageOk = init.IsSuccess;
            await RunOnUiAsync(() => IsStorageReady = storageOk, cancellationToken).ConfigureAwait(false);

            await _moduleRegistry.InitializeAllAsync(_serviceProvider, cancellationToken).ConfigureAwait(false);

            await _deviceRegistry.RegisterOrUpdateAsync(new DeviceRegistrationContract
            {
                DeviceId = _localDevice.DeviceId,
                Name = _localDevice.DeviceName,
                Platform = _localDevice.Platform,
                Type = _localDevice.DeviceType,
                OperatingSystemVersion = _localDevice.OperatingSystemVersion,
                AppVersion = _localDevice.AppVersion,
                Capabilities = _localDevice.Capabilities
            }, cancellationToken).ConfigureAwait(false);

            await Task.WhenAll(
                Settings.LoadAsync(cancellationToken),
                Pairing.LoadIdentityAsync(cancellationToken),
                Devices.LoadAllAsync(cancellationToken),
                Connectivity.InitializeAsync(cancellationToken),
                Realtime.InitializeAsync(cancellationToken)).ConfigureAwait(false);

            await Task.WhenAll(
                Dashboard.RefreshAsync(cancellationToken),
                Sync.InitializeAsync(cancellationToken),
                Transfers.InitializeAsync(cancellationToken),
                Modules.InitializeAsync(cancellationToken),
                Automation.InitializeAsync(cancellationToken),
                SystemStatus.RefreshAsync(cancellationToken)).ConfigureAwait(false);

            var currentState = _stateManager.CurrentState;
            await RunOnUiAsync(() => UpdateFooter(currentState), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiAsync(() => IsInitializing = false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnRuntimeStateChanged(object? sender, StateChangedEventArgs<NexusRuntimeState> e)
        => RunOnUi(() => UpdateFooter(e.NewState));

    private void UpdateFooter(NexusRuntimeState state)
    {
        FooterDeviceText = $"{_localDevice.DeviceName} ({_localDevice.Platform})";
        FooterStatusText = state.IsStorageInitialized
            ? $"SQLite ativo · {state.KnownDevicesCount} dispositivo(s) · Conexão: {state.ConnectionState}"
            : "Armazenamento pendente";
    }

    public void Dispose()
    {
        _stateManager.StateChanged -= OnRuntimeStateChanged;
    }
}
