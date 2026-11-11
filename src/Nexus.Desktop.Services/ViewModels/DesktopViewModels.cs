using System.Collections.ObjectModel;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
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
    Sync = 7
}

public sealed record NavigationTabItem(DesktopSection Section, string Title, string Subtitle, string IconKey);

public sealed class DashboardViewModel : ObservableObject
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

    public DashboardViewModel(
        ILocalDeviceContext localDevice,
        IDeviceRegistry deviceRegistry,
        IConnectionManager connectionManager,
        IDesktopSettingsService settingsService,
        IServerHealthProbeService probeService,
        DiagnosticHealthService healthService,
        ICryptographicIdentityProvider identityProvider,
        INexusRouteOrchestrator routeOrchestrator,
        IEventBus eventBus)
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

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ProbeServerCommand = new AsyncRelayCommand(ProbeServerAsync);
        EmitDiagnosticEventCommand = new AsyncRelayCommand(EmitDiagnosticEventAsync);

        _routeOrchestrator.StatusChanged += (_, r) => ApplyRouteSummary(r);
    }

    public ObservableCollection<string> RecentActivities { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ProbeServerCommand { get; }
    public AsyncRelayCommand EmitDiagnosticEventCommand { get; }

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

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var identity = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken);
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

        var devices = await _deviceRegistry.GetAllDevicesAsync(cancellationToken);
        if (devices.IsSuccess)
        {
            RegisteredDevicesCount = devices.Value.Count;
        }

        var health = await _healthService.CaptureAsync(cancellationToken);
        if (health.IsSuccess)
        {
            StorageHealthBadge = health.Value.IsHealthy ? "SQLite Saudável" : "Armazenamento Indisponível";
        }

        var snapshot = _connectionManager.CurrentSnapshot;
        ConnectionBadge = TranslateConnectionState(snapshot.State);
        ApplyRouteSummary(_routeOrchestrator.CurrentStatus);
        AddActivity($"Painel atualizado ({RegisteredDevicesCount} dispositivos registrados).");
    }

    private void ApplyRouteSummary(ConnectivityStatusReport report)
    {
        var routeLabel = ConnectivityViewModel.FormatRouteKind(report.RouteKind);
        var latencyPart = report.LatencyMs.HasValue ? $" · {report.LatencyMs} ms" : string.Empty;
        RouteSummary = $"Rota: {routeLabel}{latencyPart}";
        VpnSummary = report.VpnDetail ?? report.VpnStatus.ToString();
    }

    public async Task ProbeServerAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync(cancellationToken);
        var endpoint = settings.IsSuccess ? settings.Value.ServerEndpoint : "http://127.0.0.1:5280";

        var probe = await _probeService.ProbeAsync(endpoint, cancellationToken);
        if (probe.IsSuccess)
        {
            IsServerOnline = probe.Value.IsReachable;
            ConnectionBadge = probe.Value.StatusLabel;
            ServerProbeDetail = $"{endpoint} · {probe.Value.Detail ?? "OK"}";
            StatusMessage = probe.Value.IsReachable
                ? $"Conexão verificada com {endpoint}"
                : $"Servidor local indisponível em {endpoint}";
            AddActivity($"Probe de servidor ({endpoint}): {probe.Value.StatusLabel}");
        }
        else
        {
            StatusMessage = probe.Error!.Message;
        }
    }

    private async Task EmitDiagnosticEventAsync(CancellationToken cancellationToken)
    {
        await _eventBus.PublishAsync(
            new DiagnosticDomainEvent("Dashboard", "Evento de diagnóstico manual disparado pelo usuário."),
            cancellationToken);
        AddActivity("Evento de diagnóstico publicado no barramento interno.");
        StatusMessage = "Evento publicado no EventBus.";
    }

    public void AddActivity(string text)
    {
        RecentActivities.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {text}");
        while (RecentActivities.Count > 15)
        {
            RecentActivities.RemoveAt(RecentActivities.Count - 1);
        }
    }

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
}

public sealed record TrustedDevicePresenceItem(
    TrustedDeviceRecord Device,
    bool IsRealtimeConnected,
    ConnectionState ConnectionState,
    string ConnectionBadgeText);

public sealed class DevicesViewModel : ObservableObject
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
        IRealtimeClientChannel? realtimeClient = null)
    {
        _deviceRegistry = deviceRegistry;
        _localDevice = localDevice;
        _pairingClient = pairingClient;
        _realtimeClient = realtimeClient;

        LoadDevicesCommand = new AsyncRelayCommand(LoadAllAsync);
        RegisterSampleDeviceCommand = new AsyncRelayCommand(RegisterDeviceAsync);
        RemoveSelectedDeviceCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => SelectedDevice is not null && SelectedDevice.Id != _localDevice.DeviceId);

        LoadTrustedDevicesCommand = new AsyncRelayCommand(LoadTrustedDevicesAsync);
        RenameTrustedDeviceCommand = new AsyncRelayCommand(RenameTrustedAsync, () => SelectedTrustedDevice is not null);
        UpdateTrustedPermissionsCommand = new AsyncRelayCommand(UpdateTrustedPermissionsAsync, () => SelectedTrustedDevice is not null);
        RevokeTrustedDeviceCommand = new AsyncRelayCommand(RevokeTrustedAsync, () => SelectedTrustedDevice is not null && SelectedTrustedDevice.TrustStatus != DeviceTrustStatus.Revoked);
        RemoveTrustedDeviceCommand = new AsyncRelayCommand(RemoveTrustedAsync, () => SelectedTrustedDevice is not null);

        if (_realtimeClient is not null)
        {
            _realtimeClient.PresenceUpdated += (_, _) => RebuildTrustedPresenceItems();
            _realtimeClient.StateChanged += (_, _) => RebuildTrustedPresenceItems();
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
        await LoadTrustedDevicesAsync(cancellationToken);
        await LoadDevicesAsync(cancellationToken);
    }

    public async Task LoadTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        var previousSelectedId = SelectedTrustedDevice?.DeviceId;
        var result = await _pairingClient.ListTrustedDevicesAsync(cancellationToken);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error!.Message;
            return;
        }

        TrustedDevices.Clear();
        foreach (var item in result.Value)
        {
            TrustedDevices.Add(item);
        }

        AuthorizedTrustedCount = TrustedDevices.Count(d => d.TrustStatus == DeviceTrustStatus.Authorized);
        RebuildTrustedPresenceItems();

        SelectedTrustedDevice = previousSelectedId.HasValue
            ? TrustedDevices.FirstOrDefault(d => d.DeviceId == previousSelectedId.Value) ?? TrustedDevices.FirstOrDefault()
            : TrustedDevices.FirstOrDefault();

        FeedbackMessage = $"{AuthorizedTrustedCount} autorizado(s) · {ConnectedTrustedCount} conectado(s) agora · {TrustedDevices.Count} no catálogo.";
    }

    public async Task LoadDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _deviceRegistry.GetAllDevicesAsync(cancellationToken);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error!.Message;
            return;
        }

        Devices.Clear();
        foreach (var device in result.Value)
        {
            Devices.Add(device);
        }
    }

    private async Task RenameTrustedAsync(CancellationToken cancellationToken)
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        var result = await _pairingClient.RenameTrustedDeviceAsync(SelectedTrustedDevice.DeviceId, EditTrustedName, cancellationToken);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error!.Message;
            return;
        }

        await LoadAllAsync(cancellationToken);
        FeedbackMessage = $"Dispositivo renomeado para '{result.Value.CustomName}'.";
    }

    private async Task UpdateTrustedPermissionsAsync(CancellationToken cancellationToken)
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        var permissions = NexusPermission.None;
        if (PermViewStatus) permissions |= NexusPermission.ViewStatus;
        if (PermNotifications) permissions |= NexusPermission.Notifications;
        if (PermClipboard) permissions |= NexusPermission.Clipboard;
        if (PermFileTransfer) permissions |= NexusPermission.FileTransfer;
        if (PermRemoteCommands) permissions |= NexusPermission.RemoteCommands;
        if (PermManageDevices) permissions |= NexusPermission.ManageDevices;

        var result = await _pairingClient.UpdatePermissionsAsync(SelectedTrustedDevice.DeviceId, permissions, cancellationToken);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error!.Message;
            return;
        }

        await LoadTrustedDevicesAsync(cancellationToken);
        FeedbackMessage = $"Permissões atualizadas para '{result.Value.CustomName}'.";
    }

    private async Task RevokeTrustedAsync(CancellationToken cancellationToken)
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        var name = SelectedTrustedDevice.CustomName;
        var result = await _pairingClient.RevokeTrustedDeviceAsync(SelectedTrustedDevice.DeviceId, RevokeReason, cancellationToken);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error!.Message;
            return;
        }

        await LoadAllAsync(cancellationToken);
        FeedbackMessage = $"Acesso de '{name}' revogado. Tokens e sessão invalidados.";
    }

    private async Task RemoveTrustedAsync(CancellationToken cancellationToken)
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        var name = SelectedTrustedDevice.CustomName;
        var id = SelectedTrustedDevice.DeviceId;
        var result = await _pairingClient.RemoveTrustedDeviceAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error!.Message;
            return;
        }

        await LoadAllAsync(cancellationToken);
        FeedbackMessage = $"Confiança de '{name}' removida permanentemente.";
    }

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

        TrustedDeviceItems.Clear();
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

            TrustedDeviceItems.Add(new TrustedDevicePresenceItem(
                device,
                isLive,
                isLive ? ConnectionState.Connected : ConnectionState.Disconnected,
                badge));
        }

        ConnectedTrustedCount = TrustedDeviceItems.Count(i => i.IsRealtimeConnected);
        PopulateTrustedInspector(SelectedTrustedDevice);
    }

    private async Task RegisterDeviceAsync(CancellationToken cancellationToken)
    {
        var registration = new DeviceRegistrationContract
        {
            DeviceId = Guid.CreateVersion7(),
            Name = NewDeviceName,
            Platform = SelectedPlatform,
            Type = SelectedType,
            OperatingSystemVersion = SelectedPlatform == DevicePlatform.Android ? "Android 15" : Environment.OSVersion.VersionString,
            AppVersion = "0.3.0",
            Capabilities = DeviceCapability.Notifications | DeviceCapability.Clipboard | DeviceCapability.FileTransfer | DeviceCapability.Telemetry
        };

        var result = await _deviceRegistry.RegisterOrUpdateAsync(registration, cancellationToken);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error!.Message;
            return;
        }

        await LoadDevicesAsync(cancellationToken);
        FeedbackMessage = $"Dispositivo '{result.Value.Name}' registrado e validado.";
    }

    private async Task RemoveSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var id = SelectedDevice.Id;
        var removed = await _deviceRegistry.RemoveDeviceAsync(id, cancellationToken);
        if (removed.IsSuccess && removed.Value)
        {
            await LoadDevicesAsync(cancellationToken);
            FeedbackMessage = "Dispositivo removido.";
        }
        else if (removed.IsFailure)
        {
            FeedbackMessage = removed.Error!.Message;
        }
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IDesktopSettingsService _settingsService;
    private readonly ICryptographicIdentityProvider _identityProvider;

    private string _deviceName = Environment.MachineName;
    private string _serverEndpoint = "http://127.0.0.1:5280";
    private bool _autoProbeServer = true;
    private bool _enableSubtleAnimations = true;
    private string _selectedLogLevel = "Information";
    private string _feedbackMessage = string.Empty;

    public SettingsViewModel(
        IDesktopSettingsService settingsService,
        ICryptographicIdentityProvider identityProvider)
    {
        _settingsService = settingsService;
        _identityProvider = identityProvider;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
    }

    public IReadOnlyList<string> LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }

    public string DeviceName { get => _deviceName; set => SetProperty(ref _deviceName, value); }
    public string ServerEndpoint { get => _serverEndpoint; set => SetProperty(ref _serverEndpoint, value); }
    public bool AutoProbeServer { get => _autoProbeServer; set => SetProperty(ref _autoProbeServer, value); }
    public bool EnableSubtleAnimations { get => _enableSubtleAnimations; set => SetProperty(ref _enableSubtleAnimations, value); }
    public string SelectedLogLevel { get => _selectedLogLevel; set => SetProperty(ref _selectedLogLevel, value); }
    public string FeedbackMessage { get => _feedbackMessage; private set => SetProperty(ref _feedbackMessage, value); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _settingsService.LoadAsync(cancellationToken);
        if (loaded.IsSuccess)
        {
            ApplyModel(loaded.Value);
            FeedbackMessage = "Configurações carregadas do SQLite.";
        }
        else
        {
            FeedbackMessage = loaded.Error!.Message;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var model = new DesktopSettingsModel
        {
            DeviceName = DeviceName,
            ServerEndpoint = ServerEndpoint,
            AutoProbeServer = AutoProbeServer,
            EnableSubtleAnimations = EnableSubtleAnimations,
            LogLevel = SelectedLogLevel
        };

        var saved = await _settingsService.SaveAsync(model, cancellationToken);
        if (saved.IsSuccess)
        {
            await _identityProvider.RenameLocalDeviceAsync(model.DeviceName, cancellationToken);
            FeedbackMessage = $"Preferências e identidade salvas às {DateTime.Now:HH:mm:ss}.";
        }
        else
        {
            FeedbackMessage = saved.Error!.Message;
        }
    }

    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        var reset = await _settingsService.ResetToDefaultsAsync(cancellationToken);
        if (reset.IsSuccess)
        {
            ApplyModel(reset.Value);
            FeedbackMessage = "Configurações restauradas para os padrões.";
        }
        else
        {
            FeedbackMessage = reset.Error!.Message;
        }
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
        InMemoryLogSink logSink)
    {
        _healthService = healthService;
        _moduleRegistry = moduleRegistry;
        _logSink = logSink;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
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
        var report = await _healthService.CaptureAsync(cancellationToken);
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

        Modules.Clear();
        foreach (var module in _moduleRegistry.GetDescriptors())
        {
            Modules.Add(module);
        }

        RecentLogs.Clear();
        foreach (var log in _logSink.GetRecent(60))
        {
            RecentLogs.Add(log);
        }
    }
}

public sealed class MainShellViewModel : ObservableObject
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
        PairingViewModel pairing,
        DevicesViewModel devices,
        SettingsViewModel settings,
        SystemStatusViewModel systemStatus)
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
        Pairing = pairing;
        Devices = devices;
        Settings = settings;
        SystemStatus = systemStatus;

        NavigationTabs =
        [
            new NavigationTabItem(DesktopSection.Dashboard, "Dashboard", "Visão geral", "Home"),
            new NavigationTabItem(DesktopSection.Connectivity, "Conectividade", "Local, mDNS & Tailscale", "Network"),
            new NavigationTabItem(DesktopSection.Realtime, "Tempo Real", "SignalR, Comandos & ACK", "Pulse"),
            new NavigationTabItem(DesktopSection.Sync, "Sincronização", "Clipboard, Notas & Conflitos", "Sync"),
            new NavigationTabItem(DesktopSection.Pairing, "Pareamento", "QR Code & Confiança", "QrCode"),
            new NavigationTabItem(DesktopSection.Devices, "Dispositivos", "Confiança & Acesso", "Devices"),
            new NavigationTabItem(DesktopSection.SystemStatus, "Status do Sistema", "Saúde & Logs", "Pulse"),
            new NavigationTabItem(DesktopSection.Settings, "Configurações", "Preferências", "Settings")
        ];

        _stateManager.StateChanged += (_, e) => UpdateFooter(e.NewState);
    }

    public IReadOnlyList<NavigationTabItem> NavigationTabs { get; }
    public DashboardViewModel Dashboard { get; }
    public ConnectivityViewModel Connectivity { get; }
    public RealtimeViewModel Realtime { get; }
    public SyncViewModel Sync { get; }
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var init = await _storageInitializer.InitializeAsync(cancellationToken);
        IsStorageReady = init.IsSuccess;

        await _moduleRegistry.InitializeAllAsync(_serviceProvider, cancellationToken);

        await _deviceRegistry.RegisterOrUpdateAsync(new DeviceRegistrationContract
        {
            DeviceId = _localDevice.DeviceId,
            Name = _localDevice.DeviceName,
            Platform = _localDevice.Platform,
            Type = _localDevice.DeviceType,
            OperatingSystemVersion = _localDevice.OperatingSystemVersion,
            AppVersion = _localDevice.AppVersion,
            Capabilities = _localDevice.Capabilities
        }, cancellationToken);

        await Dashboard.RefreshAsync(cancellationToken);
        await Connectivity.InitializeAsync(cancellationToken);
        await Realtime.InitializeAsync(cancellationToken);
        await Sync.InitializeAsync(cancellationToken);
        await Pairing.LoadIdentityAsync(cancellationToken);
        await Devices.LoadAllAsync(cancellationToken);
        await Settings.LoadAsync(cancellationToken);
        await SystemStatus.RefreshAsync(cancellationToken);

        UpdateFooter(_stateManager.CurrentState);
    }

    private void UpdateFooter(NexusRuntimeState state)
    {
        FooterDeviceText = $"{_localDevice.DeviceName} ({_localDevice.Platform})";
        FooterStatusText = state.IsStorageInitialized
            ? $"SQLite ativo · {state.KnownDevicesCount} dispositivo(s) · Conexão: {state.ConnectionState}"
            : "Armazenamento pendente";
    }
}
