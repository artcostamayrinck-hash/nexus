using System.Collections.ObjectModel;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Services;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Security;
using Nexus.Mobile.Services.Connectivity;
using Nexus.Mobile.Services.Mvvm;
using Nexus.Mobile.Services.Security;
using Nexus.Mobile.Services.Settings;
using Nexus.Modules.Registry;
using Nexus.Security.Identity;

namespace Nexus.Mobile.Services.ViewModels;

public sealed class HomeViewModel : MobileObservableObject
{
    private readonly IStorageInitializer _storageInitializer;
    private readonly IModuleRegistry _moduleRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IDeviceRegistry _deviceRegistry;
    private readonly IConnectionManager _connectionManager;
    private readonly DiagnosticHealthService _healthService;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly IMobilePairingClient _pairingClient;

    private string _deviceTitle = string.Empty;
    private string _deviceSubtitle = string.Empty;
    private string _localFingerprintDisplay = "-";
    private string _connectionBadge = "Desconectado";
    private string _storageBadge = "Inicializando...";
    private string _pairedSummaryText = "Não pareado";
    private int _knownDevicesCount;
    private bool _isBusy;

    public HomeViewModel(
        IStorageInitializer storageInitializer,
        IModuleRegistry moduleRegistry,
        IServiceProvider serviceProvider,
        ILocalDeviceContext localDevice,
        IDeviceRegistry deviceRegistry,
        IConnectionManager connectionManager,
        DiagnosticHealthService healthService,
        ICryptographicIdentityProvider identityProvider,
        IMobilePairingClient pairingClient)
    {
        _storageInitializer = storageInitializer;
        _moduleRegistry = moduleRegistry;
        _serviceProvider = serviceProvider;
        _localDevice = localDevice;
        _deviceRegistry = deviceRegistry;
        _connectionManager = connectionManager;
        _healthService = healthService;
        _identityProvider = identityProvider;
        _pairingClient = pairingClient;

        InitializeCommand = new MobileAsyncCommand(InitializeAsync);
        OpenModulesCommand = new MobileAsyncCommand(_ =>
        {
            NavigateToModulesRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
        OpenPanelCommand = new MobileAsyncCommand(_ =>
        {
            NavigateToPanelRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
        OpenAutomateCommand = new MobileAsyncCommand(_ =>
        {
            NavigateToAutomateRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
        OpenUpdatesCommand = new MobileAsyncCommand(_ =>
        {
            NavigateToUpdatesRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
    }

    public event EventHandler? NavigateToModulesRequested;
    public event EventHandler? NavigateToPanelRequested;
    public event EventHandler? NavigateToAutomateRequested;
    public event EventHandler? NavigateToUpdatesRequested;
    public MobileAsyncCommand InitializeCommand { get; }
    public MobileAsyncCommand OpenModulesCommand { get; }
    public MobileAsyncCommand OpenPanelCommand { get; }
    public MobileAsyncCommand OpenAutomateCommand { get; }
    public MobileAsyncCommand OpenUpdatesCommand { get; }

    public string DeviceTitle { get => _deviceTitle; private set => SetProperty(ref _deviceTitle, value); }
    public string DeviceSubtitle { get => _deviceSubtitle; private set => SetProperty(ref _deviceSubtitle, value); }
    public string LocalFingerprintDisplay { get => _localFingerprintDisplay; private set => SetProperty(ref _localFingerprintDisplay, value); }
    public string ConnectionBadge { get => _connectionBadge; private set => SetProperty(ref _connectionBadge, value); }
    public string StorageBadge { get => _storageBadge; private set => SetProperty(ref _storageBadge, value); }
    public string PairedSummaryText { get => _pairedSummaryText; private set => SetProperty(ref _pairedSummaryText, value); }
    public int KnownDevicesCount { get => _knownDevicesCount; private set => SetProperty(ref _knownDevicesCount, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
        await _storageInitializer.InitializeAsync(cancellationToken);
        await _moduleRegistry.InitializeAllAsync(_serviceProvider, cancellationToken);

        var identity = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken);
        var activeName = identity.IsSuccess ? identity.Value.CustomName : _localDevice.DeviceName;
        var activeId = identity.IsSuccess ? identity.Value.DeviceId : _localDevice.DeviceId;
        LocalFingerprintDisplay = identity.IsSuccess
            ? NexusPairingProtocolClient.FormatFingerprint(identity.Value.PublicKeyFingerprint)
            : "-";

        await _deviceRegistry.RegisterOrUpdateAsync(new DeviceRegistrationContract
        {
            DeviceId = activeId,
            Name = activeName,
            Platform = _localDevice.Platform,
            Type = _localDevice.DeviceType,
            OperatingSystemVersion = _localDevice.OperatingSystemVersion,
            AppVersion = _localDevice.AppVersion,
            Capabilities = _localDevice.Capabilities
        }, cancellationToken);

        DeviceTitle = activeName;
        DeviceSubtitle = $"{_localDevice.Platform} · {_localDevice.AppVersion}";

        var health = await _healthService.CaptureAsync(cancellationToken);
        StorageBadge = health.IsSuccess && health.Value.IsHealthy ? "SQLite Operacional" : "Erro de Armazenamento";

        var devices = await _deviceRegistry.GetAllDevicesAsync(cancellationToken);
        if (devices.IsSuccess)
        {
            KnownDevicesCount = devices.Value.Count;
        }

        var session = await _pairingClient.GetStoredSessionAsync(cancellationToken);
        PairedSummaryText = session.IsSuccess && session.Value is not null
            ? $"Pareado com {session.Value.HostDeviceName}"
            : "Nenhum host pareado";

        ConnectionBadge = _connectionManager.CurrentSnapshot.State.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class ConnectionViewModel : MobileObservableObject, IDisposable
{
    private readonly IMobileConnectionCoordinator _coordinator;
    private readonly IMobileSettingsService _settingsService;
    private readonly IMobilePairingClient _pairingClient;
    private readonly MobileDevicesViewModel _devicesViewModel;

    private PairingQrPayload? _activeQr;
    private CancellationTokenSource? _pollCts;

    private string _serverEndpoint = "http://192.168.1.100:5280";
    private TransportKind _selectedTransport = TransportKind.LocalNetwork;
    private string _statusLabel = "Desconectado";
    private string _detailText = "Configure o endereço do servidor NEXUS e teste o alcance.";
    private string _latencyLabel = "--";

    private string _qrInputText = string.Empty;
    private PairingSessionState _pairingState = PairingSessionState.Idle;
    private string _pairingStateLabel = NexusPairingProtocolClient.GetPairingStateLabel(PairingSessionState.Idle);
    private string _verificationCode = "--- ---";
    private string _pairedHostName = "-";
    private string _pairedHostFingerprint = "-";
    private string _pairingFeedback = "Leia ou cole o payload do QR Code exibido no NEXUS Windows.";

    public ConnectionViewModel(
        IMobileConnectionCoordinator coordinator,
        IMobileSettingsService settingsService,
        IMobilePairingClient pairingClient,
        MobileDevicesViewModel devicesViewModel)
    {
        _coordinator = coordinator;
        _settingsService = settingsService;
        _pairingClient = pairingClient;
        _devicesViewModel = devicesViewModel;

        LoadCommand = new MobileAsyncCommand(LoadAsync);
        TestConnectionCommand = new MobileAsyncCommand(TestConnectionAsync);
        DisconnectCommand = new MobileAsyncCommand(DisconnectAsync);

        SubmitQrPairingCommand = new MobileAsyncCommand(SubmitQrPairingAsync);
        CheckPairingStatusCommand = new MobileAsyncCommand(CheckPairingStatusOnceAsync, () => _activeQr is not null);
        RefreshTokensCommand = new MobileAsyncCommand(RefreshTokensAsync);
        ResetPairingCommand = new MobileAsyncCommand(ResetPairingAsync);
    }

    public IReadOnlyList<TransportKind> AvailableTransports { get; } = [TransportKind.LocalNetwork, TransportKind.SignalR, TransportKind.WebSocket];
    public MobileAsyncCommand LoadCommand { get; }
    public MobileAsyncCommand TestConnectionCommand { get; }
    public MobileAsyncCommand DisconnectCommand { get; }

    public MobileAsyncCommand SubmitQrPairingCommand { get; }
    public MobileAsyncCommand CheckPairingStatusCommand { get; }
    public MobileAsyncCommand RefreshTokensCommand { get; }
    public MobileAsyncCommand ResetPairingCommand { get; }

    public string ServerEndpoint { get => _serverEndpoint; set => SetProperty(ref _serverEndpoint, value); }
    public TransportKind SelectedTransport { get => _selectedTransport; set => SetProperty(ref _selectedTransport, value); }
    public string StatusLabel { get => _statusLabel; private set => SetProperty(ref _statusLabel, value); }
    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }
    public string LatencyLabel { get => _latencyLabel; private set => SetProperty(ref _latencyLabel, value); }

    public string QrInputText { get => _qrInputText; set => SetProperty(ref _qrInputText, value); }
    public PairingSessionState PairingState
    {
        get => _pairingState;
        private set
        {
            if (SetProperty(ref _pairingState, value))
            {
                PairingStateLabel = NexusPairingProtocolClient.GetPairingStateLabel(value);
            }
        }
    }

    public string PairingStateLabel { get => _pairingStateLabel; private set => SetProperty(ref _pairingStateLabel, value); }
    public string VerificationCode { get => _verificationCode; private set => SetProperty(ref _verificationCode, value); }
    public string PairedHostName { get => _pairedHostName; private set => SetProperty(ref _pairedHostName, value); }
    public string PairedHostFingerprint { get => _pairedHostFingerprint; private set => SetProperty(ref _pairedHostFingerprint, value); }
    public string PairingFeedback { get => _pairingFeedback; private set => SetProperty(ref _pairingFeedback, value); }
    public void SetPairingFeedback(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            PairingFeedback = message;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _settingsService.LoadAsync(cancellationToken);
        if (loaded.IsSuccess)
        {
            ServerEndpoint = loaded.Value.ServerEndpoint;
        }

        var session = await _pairingClient.GetStoredSessionAsync(cancellationToken);
        if (session.IsSuccess && session.Value is not null)
        {
            PairedHostName = session.Value.HostDeviceName;
            PairedHostFingerprint = NexusPairingProtocolClient.FormatFingerprint(session.Value.HostFingerprint);
            if (PairingState == PairingSessionState.Idle)
            {
                PairingState = PairingSessionState.Connected;
                PairingFeedback = $"Conectado de forma segura ao host '{session.Value.HostDeviceName}'.";
            }
        }

        ApplySnapshot(_coordinator.CurrentSnapshot);
    }

    public async Task SubmitQrPairingAsync(CancellationToken cancellationToken = default)
    {
        CancelPolling();
        PairingState = PairingSessionState.Validating;
        PairingFeedback = "Validando assinatura criptográfica, validade do QR e pinagem TLS...";

        var claim = await _pairingClient.SubmitQrPairingAsync(
            QrInputText,
            NexusPermission.ViewStatus | NexusPermission.Notifications | NexusPermission.Clipboard | NexusPermission.FileTransfer,
            cancellationToken);

        if (claim.IsFailure)
        {
            PairingState = string.Equals(claim.Error!.Code, "PAIRING_EXPIRED", StringComparison.Ordinal)
                ? PairingSessionState.Expired
                : PairingSessionState.Failed;
            PairingFeedback = claim.Error.Message;
            return;
        }

        _activeQr = claim.Value.QrPayload;
        ServerEndpoint = claim.Value.QrPayload.ServerEndpoint;
        PairedHostName = claim.Value.QrPayload.HostDeviceName;
        PairedHostFingerprint = NexusPairingProtocolClient.FormatFingerprint(claim.Value.QrPayload.HostPublicKeyFingerprint);
        VerificationCode = FormatCode(claim.Value.VerificationCode);
        PairingState = claim.Value.Snapshot.State;
        PairingFeedback = $"Confirme no Windows se o código é {VerificationCode}. Aguardando aprovação...";
        CheckPairingStatusCommand.NotifyCanExecuteChanged();

        StartBackgroundPoll();
    }

    public async Task CheckPairingStatusOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_activeQr is null)
        {
            return;
        }

        var check = await _pairingClient.CheckAndFinalizeAsync(_activeQr, cancellationToken);
        if (check.IsFailure)
        {
            CancelPolling();
            PairingState = PairingSessionState.Failed;
            PairingFeedback = check.Error!.Message;
            return;
        }

        var snapshot = check.Value.Snapshot;
        PairingState = snapshot.State;

        if (snapshot.State == PairingSessionState.Connected && check.Value.Tokens is not null)
        {
            CancelPolling();
            PairingFeedback = $"Pareamento concluído! Confiança estabelecida com '{PairedHostName}'.";
            await _devicesViewModel.LoadAsync(cancellationToken);
        }
        else if (snapshot.State is PairingSessionState.Expired or PairingSessionState.Rejected or PairingSessionState.Failed)
        {
            CancelPolling();
            PairingFeedback = snapshot.FailureReason ?? "Solicitação de pareamento encerrada.";
        }
    }

    private async Task RefreshTokensAsync(CancellationToken cancellationToken)
    {
        var result = await _pairingClient.RefreshStoredTokensAsync(cancellationToken);
        PairingFeedback = result.IsSuccess
            ? $"Tokens renovados! Novo vencimento: {result.Value.AccessTokenExpiresAtUtc.ToLocalTime():HH:mm:ss}."
            : result.Error!.Message;
    }

    private Task ResetPairingAsync(CancellationToken cancellationToken)
    {
        CancelPolling();
        _activeQr = null;
        QrInputText = string.Empty;
        VerificationCode = "--- ---";
        PairingState = PairingSessionState.Idle;
        PairingFeedback = "Pronto para escanear um novo QR Code NEXUS.";
        CheckPairingStatusCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var result = await _coordinator.TestConnectionAsync(ServerEndpoint, SelectedTransport, cancellationToken);
        if (result.IsSuccess)
        {
            ApplySnapshot(result.Value);
        }
        else
        {
            StatusLabel = "Entrada inválida";
            DetailText = result.Error!.Message;
        }
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var result = await _coordinator.DisconnectAsync(cancellationToken);
        if (result.IsSuccess)
        {
            ApplySnapshot(result.Value);
        }
    }

    private void ApplySnapshot(ConnectionStateSnapshot snapshot)
    {
        StatusLabel = snapshot.State.ToString();
        DetailText = snapshot.Reason ?? "Sem eventos adicionais.";
        LatencyLabel = snapshot.LatencyMs.HasValue ? $"{snapshot.LatencyMs} ms" : "--";
    }

    private static string FormatCode(string? code)
        => !string.IsNullOrWhiteSpace(code) && code.Length == 6 ? $"{code[..3]} {code[3..]}" : code ?? "--- ---";

    private void StartBackgroundPoll()
    {
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = RunPollLoopAsync(token);
    }

    private async Task RunPollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && PairingState == PairingSessionState.AwaitingConfirmation)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.2), token);
                if (!token.IsCancellationRequested && _activeQr is not null)
                {
                    await CheckPairingStatusOnceAsync(token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on state change
        }
    }

    private void CancelPolling()
    {
        if (_pollCts is null)
        {
            return;
        }

        _pollCts.Cancel();
        _pollCts.Dispose();
        _pollCts = null;
    }

    public void Dispose() => CancelPolling();
}

public sealed record MobileTrustedDevicePresenceItem(
    TrustedDeviceRecord Device,
    bool IsRealtimeConnected,
    ConnectionState ConnectionState,
    string ConnectionBadgeText);

public sealed class MobileDevicesViewModel : MobileObservableObject
{
    private readonly IDeviceRegistry _deviceRegistry;
    private readonly IMobilePairingClient _pairingClient;
    private readonly IRealtimeClientChannel? _realtimeClient;

    private string _feedbackText = string.Empty;
    private TrustedDeviceRecord? _selectedTrustedDevice;
    private MobileTrustedDevicePresenceItem? _selectedTrustedItem;
    private string _editDeviceName = string.Empty;
    private string _selectedFingerprintDisplay = "-";
    private string _selectedLastAccessText = "-";
    private string _selectedTrustBadge = "-";
    private string _selectedConnectionBadge = "Desconectado (Offline)";

    public MobileDevicesViewModel(
        IDeviceRegistry deviceRegistry,
        IMobilePairingClient pairingClient,
        IRealtimeClientChannel? realtimeClient = null)
    {
        _deviceRegistry = deviceRegistry;
        _pairingClient = pairingClient;
        _realtimeClient = realtimeClient;

        RefreshCommand = new MobileAsyncCommand(LoadAsync);
        RenameSelectedCommand = new MobileAsyncCommand(RenameSelectedAsync, () => SelectedTrustedDevice is not null);
        RevokeSelectedCommand = new MobileAsyncCommand(RevokeSelectedAsync, () => SelectedTrustedDevice is not null && SelectedTrustedDevice.TrustStatus != DeviceTrustStatus.Revoked);
        RemoveSelectedCommand = new MobileAsyncCommand(RemoveSelectedAsync, () => SelectedTrustedDevice is not null);

        if (_realtimeClient is not null)
        {
            _realtimeClient.PresenceUpdated += (_, _) => RebuildTrustedPresenceItems();
            _realtimeClient.StateChanged += (_, _) => RebuildTrustedPresenceItems();
        }
    }

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<TrustedDeviceRecord> TrustedDevices { get; } = [];
    public ObservableCollection<MobileTrustedDevicePresenceItem> TrustedDeviceItems { get; } = [];

    public MobileAsyncCommand RefreshCommand { get; }
    public MobileAsyncCommand RenameSelectedCommand { get; }
    public MobileAsyncCommand RevokeSelectedCommand { get; }
    public MobileAsyncCommand RemoveSelectedCommand { get; }

    public string FeedbackText { get => _feedbackText; private set => SetProperty(ref _feedbackText, value); }
    public string EditDeviceName { get => _editDeviceName; set => SetProperty(ref _editDeviceName, value); }
    public string SelectedFingerprintDisplay { get => _selectedFingerprintDisplay; private set => SetProperty(ref _selectedFingerprintDisplay, value); }
    public string SelectedLastAccessText { get => _selectedLastAccessText; private set => SetProperty(ref _selectedLastAccessText, value); }
    public string SelectedTrustBadge { get => _selectedTrustBadge; private set => SetProperty(ref _selectedTrustBadge, value); }
    public string SelectedConnectionBadge { get => _selectedConnectionBadge; private set => SetProperty(ref _selectedConnectionBadge, value); }

    public MobileTrustedDevicePresenceItem? SelectedTrustedItem
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

    public TrustedDeviceRecord? SelectedTrustedDevice
    {
        get => _selectedTrustedDevice;
        set
        {
            if (SetProperty(ref _selectedTrustedDevice, value))
            {
                PopulateSelectedDetails(value);
                RenameSelectedCommand.NotifyCanExecuteChanged();
                RevokeSelectedCommand.NotifyCanExecuteChanged();
                RemoveSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var prevId = SelectedTrustedDevice?.DeviceId;
        var trustedRes = await _pairingClient.ListTrustedDevicesAsync(cancellationToken);
        if (trustedRes.IsSuccess)
        {
            TrustedDevices.Clear();
            foreach (var item in trustedRes.Value)
            {
                TrustedDevices.Add(item);
            }

            SelectedTrustedDevice = prevId.HasValue
                ? TrustedDevices.FirstOrDefault(d => d.DeviceId == prevId.Value) ?? TrustedDevices.FirstOrDefault()
                : TrustedDevices.FirstOrDefault();

            RebuildTrustedPresenceItems();
        }

        var list = await _deviceRegistry.GetAllDevicesAsync(cancellationToken);
        if (list.IsFailure)
        {
            FeedbackText = list.Error!.Message;
            return;
        }

        Devices.Clear();
        foreach (var device in list.Value)
        {
            Devices.Add(device);
        }

        var authorizedCount = TrustedDevices.Count(t => t.TrustStatus == DeviceTrustStatus.Authorized);
        FeedbackText = $"{authorizedCount} dispositivo(s) confiáveis · {Devices.Count} no inventário local";
    }

    private async Task RenameSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        var res = await _pairingClient.RenameTrustedDeviceAsync(SelectedTrustedDevice.DeviceId, EditDeviceName, cancellationToken);
        if (res.IsFailure)
        {
            FeedbackText = res.Error!.Message;
            return;
        }

        await LoadAsync(cancellationToken);
        FeedbackText = $"Dispositivo renomeado para '{res.Value.CustomName}'.";
    }

    private async Task RevokeSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        var name = SelectedTrustedDevice.CustomName;
        var res = await _pairingClient.RevokeTrustedDeviceAsync(SelectedTrustedDevice.DeviceId, "Revogado pelo usuário no Android", cancellationToken);
        if (res.IsFailure)
        {
            FeedbackText = res.Error!.Message;
            return;
        }

        await LoadAsync(cancellationToken);
        FeedbackText = $"Acesso de '{name}' revogado.";
    }

    private async Task RemoveSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        var name = SelectedTrustedDevice.CustomName;
        var res = await _pairingClient.RemoveTrustedDeviceAsync(SelectedTrustedDevice.DeviceId, cancellationToken);
        if (res.IsFailure)
        {
            FeedbackText = res.Error!.Message;
            return;
        }

        await LoadAsync(cancellationToken);
        FeedbackText = $"Dispositivo '{name}' removido da lista de confiança.";
    }

    private void PopulateSelectedDetails(TrustedDeviceRecord? record)
    {
        if (record is null)
        {
            EditDeviceName = string.Empty;
            SelectedFingerprintDisplay = "-";
            SelectedLastAccessText = "-";
            SelectedTrustBadge = "-";
            SelectedConnectionBadge = "Desconectado (Offline)";
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

        EditDeviceName = record.CustomName;
        SelectedFingerprintDisplay = NexusPairingProtocolClient.FormatFingerprint(record.PublicKeyFingerprint);
        SelectedLastAccessText = $"{record.LastAccessAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
        SelectedTrustBadge = record.TrustStatus == DeviceTrustStatus.Authorized ? "Autorizado" : record.TrustStatus.ToString();
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

            TrustedDeviceItems.Add(new MobileTrustedDevicePresenceItem(
                device,
                isLive,
                isLive ? ConnectionState.Connected : ConnectionState.Disconnected,
                badge));
        }

        PopulateSelectedDetails(SelectedTrustedDevice);
    }
}

public sealed class MobileSettingsViewModel : MobileObservableObject
{
    private readonly IMobileSettingsService _settingsService;
    private readonly ICryptographicIdentityProvider _identityProvider;

    private string _deviceName = "Android Device";
    private string _serverEndpoint = "http://192.168.1.100:5280";
    private bool _autoReconnect = true;
    private bool _backgroundTelemetryEnabled = true;
    private string _feedbackMessage = string.Empty;

    public MobileSettingsViewModel(
        IMobileSettingsService settingsService,
        ICryptographicIdentityProvider identityProvider)
    {
        _settingsService = settingsService;
        _identityProvider = identityProvider;
        LoadCommand = new MobileAsyncCommand(LoadAsync);
        SaveCommand = new MobileAsyncCommand(SaveAsync);
        ResetCommand = new MobileAsyncCommand(ResetAsync);
    }

    public MobileAsyncCommand LoadCommand { get; }
    public MobileAsyncCommand SaveCommand { get; }
    public MobileAsyncCommand ResetCommand { get; }

    public string DeviceName { get => _deviceName; set => SetProperty(ref _deviceName, value); }
    public string ServerEndpoint { get => _serverEndpoint; set => SetProperty(ref _serverEndpoint, value); }
    public bool AutoReconnect { get => _autoReconnect; set => SetProperty(ref _autoReconnect, value); }
    public bool BackgroundTelemetryEnabled { get => _backgroundTelemetryEnabled; set => SetProperty(ref _backgroundTelemetryEnabled, value); }
    public string FeedbackMessage { get => _feedbackMessage; private set => SetProperty(ref _feedbackMessage, value); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _settingsService.LoadAsync(cancellationToken);
        if (loaded.IsSuccess)
        {
            Apply(loaded.Value);
            FeedbackMessage = "Preferências carregadas do SQLite.";
        }
        else
        {
            FeedbackMessage = loaded.Error!.Message;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var result = await _settingsService.SaveAsync(new MobileSettingsModel
        {
            DeviceName = DeviceName,
            ServerEndpoint = ServerEndpoint,
            AutoReconnect = AutoReconnect,
            BackgroundTelemetryEnabled = BackgroundTelemetryEnabled
        }, cancellationToken);

        if (result.IsSuccess)
        {
            await _identityProvider.RenameLocalDeviceAsync(DeviceName, cancellationToken);
            FeedbackMessage = "Configurações e identidade salvas.";
        }
        else
        {
            FeedbackMessage = result.Error!.Message;
        }
    }

    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        var result = await _settingsService.ResetAsync(cancellationToken);
        if (result.IsSuccess)
        {
            Apply(result.Value);
            FeedbackMessage = "Padrões restaurados.";
        }
    }

    private void Apply(MobileSettingsModel model)
    {
        DeviceName = model.DeviceName;
        ServerEndpoint = model.ServerEndpoint;
        AutoReconnect = model.AutoReconnect;
        BackgroundTelemetryEnabled = model.BackgroundTelemetryEnabled;
    }
}
