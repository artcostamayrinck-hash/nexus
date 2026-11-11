using System.Collections.ObjectModel;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class RealtimeViewModel : ObservableObject, IDisposable
{
    private readonly IRealtimeClientChannel _realtimeClient;
    private readonly IHttpBulkTransferChannel _bulkChannel;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IClock _clock;

    private ConnectionState _currentState = ConnectionState.Disconnected;
    private string _connectionStateText = "Desconectado";
    private string _endpointText = "-";
    private string _sessionIdText = "-";
    private string _lastHeartbeatText = "-";
    private string _statusBanner = "Canal SignalR pronto. Conecte-se após autenticar ou parear o servidor.";
    private string _commandNameInput = "desktop.notify";
    private string _commandArgsInput = "Ping do Windows";
    private string _eventTypeInput = NexusEventTypes.NotificationReceived;
    private string _eventPayloadInput = "{\"preview\":\"Notificação em tempo real via NEXUS\"}";
    private DevicePresenceSnapshot? _selectedPeer;
    private Guid? _lastCommandId;
    private Guid? _lastCommandTargetId;
    private int _onlinePeersCount;
    private int _offlinePeersCount;
    private string _bulkTransferStatus = "Nenhuma transferência HTTP executada nesta sessão.";

    public RealtimeViewModel(
        IRealtimeClientChannel realtimeClient,
        IHttpBulkTransferChannel bulkChannel,
        ILocalDeviceContext localDevice,
        IClock clock,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _realtimeClient = realtimeClient;
        _bulkChannel = bulkChannel;
        _localDevice = localDevice;
        _clock = clock;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, uiDispatcher: uiDispatcher);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, uiDispatcher: uiDispatcher);
        RefreshPresenceCommand = new AsyncRelayCommand(RefreshPresenceAsync, () => _realtimeClient.IsConnected, uiDispatcher: uiDispatcher);
        SendHeartbeatCommand = new AsyncRelayCommand(SendHeartbeatAsync, () => _realtimeClient.IsConnected, uiDispatcher: uiDispatcher);
        SendCommandCommand = new AsyncRelayCommand(SendCommandAsync, () => _realtimeClient.IsConnected && SelectedPeer is not null, uiDispatcher: uiDispatcher);
        CancelLastCommandCommand = new AsyncRelayCommand(CancelLastCommandAsync, () => _realtimeClient.IsConnected && _lastCommandId.HasValue && _lastCommandTargetId.HasValue, uiDispatcher: uiDispatcher);
        SendIntegrationEventCommand = new AsyncRelayCommand(SendIntegrationEventAsync, () => _realtimeClient.IsConnected, uiDispatcher: uiDispatcher);
        SendBulkHttpTestCommand = new AsyncRelayCommand(SendBulkHttpTestAsync, uiDispatcher: uiDispatcher);

        _realtimeClient.StateChanged += OnRealtimeStateChanged;
        _realtimeClient.PresenceUpdated += OnRealtimePresenceUpdated;
        _realtimeClient.MessageReceived += OnRealtimeMessageReceived;
        _realtimeClient.AckReceived += OnRealtimeAckReceived;
    }

    public ObservableCollection<DevicePresenceSnapshot> PresenceItems { get; } = [];
    public ObservableCollection<RealtimeMessageEnvelope> RecentMessages { get; } = [];
    public ObservableCollection<RealtimeMessageAck> RecentAcks { get; } = [];

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RefreshPresenceCommand { get; }
    public AsyncRelayCommand SendHeartbeatCommand { get; }
    public AsyncRelayCommand SendCommandCommand { get; }
    public AsyncRelayCommand CancelLastCommandCommand { get; }
    public AsyncRelayCommand SendIntegrationEventCommand { get; }
    public AsyncRelayCommand SendBulkHttpTestCommand { get; }

    public ConnectionState CurrentState
    {
        get => _currentState;
        private set
        {
            if (SetProperty(ref _currentState, value))
            {
                ConnectionStateText = TranslateState(value);
                NotifyActionCommands();
            }
        }
    }

    public string ConnectionStateText { get => _connectionStateText; private set => SetProperty(ref _connectionStateText, value); }
    public string EndpointText { get => _endpointText; private set => SetProperty(ref _endpointText, value); }
    public string SessionIdText { get => _sessionIdText; private set => SetProperty(ref _sessionIdText, value); }
    public string LastHeartbeatText { get => _lastHeartbeatText; private set => SetProperty(ref _lastHeartbeatText, value); }
    public string StatusBanner { get => _statusBanner; private set => SetProperty(ref _statusBanner, value); }
    public string CommandNameInput { get => _commandNameInput; set => SetProperty(ref _commandNameInput, value); }
    public string CommandArgsInput { get => _commandArgsInput; set => SetProperty(ref _commandArgsInput, value); }
    public string EventTypeInput { get => _eventTypeInput; set => SetProperty(ref _eventTypeInput, value); }
    public string EventPayloadInput { get => _eventPayloadInput; set => SetProperty(ref _eventPayloadInput, value); }
    public int OnlinePeersCount { get => _onlinePeersCount; private set => SetProperty(ref _onlinePeersCount, value); }
    public int OfflinePeersCount { get => _offlinePeersCount; private set => SetProperty(ref _offlinePeersCount, value); }
    public string BulkTransferStatus { get => _bulkTransferStatus; private set => SetProperty(ref _bulkTransferStatus, value); }

    public DevicePresenceSnapshot? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (SetProperty(ref _selectedPeer, value))
            {
                SendCommandCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var initialState = _realtimeClient.State;
        var initialPresence = OrderPresence(_realtimeClient.LivePresence);
        var initialSession = _realtimeClient.CurrentSession;

        await RunOnUiAsync(() =>
        {
            CurrentState = initialState;
            ApplyPresenceList(initialPresence);
            if (initialSession is not null)
            {
                SessionIdText = initialSession.SessionId.ToString("D");
            }
        }, cancellationToken).ConfigureAwait(false);

        if (_realtimeClient.IsConnected)
        {
            await RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await RunOnUiAsync(
            () => StatusBanner = "Conectando e autenticando no NexusHub (SignalR)...",
            cancellationToken).ConfigureAwait(false);

        var opened = await _realtimeClient.OpenAuthenticatedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            var errState = _realtimeClient.State;
            var errMsg = $"Falha ao conectar: {opened.Error!.Message}";
            await RunOnUiAsync(() =>
            {
                CurrentState = errState;
                StatusBanner = errMsg;
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ordered = OrderPresence(_realtimeClient.LivePresence);
        var endpoint = ordered.FirstOrDefault(p => p.DeviceId == _localDevice.DeviceId)?.RouteSummary ?? "SignalR Ativo";
        var session = opened.Value;

        await RunOnUiAsync(() =>
        {
            CurrentState = session.State;
            SessionIdText = session.SessionId.ToString("D");
            EndpointText = endpoint;
            ApplyPresenceList(ordered);
            StatusBanner = $"Conectado ao SignalR (Sessão {session.ConnectionId}).";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _realtimeClient.CloseAsync(cancellationToken).ConfigureAwait(false);
        var nextState = _realtimeClient.State;
        var ordered = OrderPresence(_realtimeClient.LivePresence);

        await RunOnUiAsync(() =>
        {
            CurrentState = nextState;
            SessionIdText = "-";
            ApplyPresenceList(ordered);
            StatusBanner = "Sessão SignalR encerrada manualmente.";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshPresenceAsync(CancellationToken cancellationToken)
    {
        var result = await _realtimeClient.RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var msg = result.Error!.Message;
            await RunOnUiAsync(() => StatusBanner = msg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ordered = OrderPresence(result.Value);
        await RunOnUiAsync(() =>
        {
            ApplyPresenceList(ordered);
            StatusBanner = $"Presença atualizada: {OnlinePeersCount} online, {OfflinePeersCount} desconectado(s).";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var ping = await _realtimeClient.SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        if (ping.IsFailure)
        {
            var msg = ping.Error!.Message;
            await RunOnUiAsync(() => StatusBanner = msg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var rtt = ping.Value;
        var nowLocal = _clock.UtcNow.ToLocalTime();
        await RunOnUiAsync(() =>
        {
            LastHeartbeatText = $"{rtt} ms às {nowLocal:HH:mm:ss}";
            StatusBanner = $"Heartbeat SignalR confirmado em {rtt} ms.";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCommandAsync(CancellationToken cancellationToken)
    {
        DevicePresenceSnapshot? peer = null;
        string cmdName = string.Empty;
        string cmdArgs = string.Empty;

        await RunOnUiAsync(() =>
        {
            peer = SelectedPeer;
            cmdName = CommandNameInput.Trim();
            cmdArgs = CommandArgsInput ?? string.Empty;
        }, cancellationToken).ConfigureAwait(false);

        if (peer is null)
        {
            await RunOnUiAsync(() => StatusBanner = "Selecione um dispositivo de destino.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var command = new DeviceCommand
        {
            SourceDeviceId = _realtimeClient.CurrentSession?.DeviceId ?? _localDevice.DeviceId,
            TargetDeviceId = peer.DeviceId,
            CommandName = cmdName,
            Arguments = new Dictionary<string, string> { ["payload"] = cmdArgs },
            IssuedAtUtc = _clock.UtcNow
        };

        await RunOnUiAsync(() =>
        {
            _lastCommandId = command.CommandId;
            _lastCommandTargetId = command.TargetDeviceId;
            CancelLastCommandCommand.NotifyCanExecuteChanged();
        }, cancellationToken).ConfigureAwait(false);

        var ack = await _realtimeClient.SendCommandAsync(command, waitForAck: peer.IsRealtimeConnected, cancellationToken)
            .ConfigureAwait(false);

        if (ack.IsFailure)
        {
            var errMsg = $"Comando rejeitado ({ack.Error!.Code}): {ack.Error.Message}";
            await RunOnUiAsync(() => StatusBanner = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ackValue = ack.Value;
        await RunOnUiAsync(() =>
        {
            PrependCapped(RecentAcks, ackValue, 30);
            StatusBanner = $"Comando '{command.CommandName}' confirmado ({ackValue.Status}) para '{peer.DeviceName}'.";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelLastCommandAsync(CancellationToken cancellationToken)
    {
        Guid? cmdId = null;
        Guid? targetId = null;
        await RunOnUiAsync(() =>
        {
            cmdId = _lastCommandId;
            targetId = _lastCommandTargetId;
        }, cancellationToken).ConfigureAwait(false);

        if (!cmdId.HasValue || !targetId.HasValue)
        {
            return;
        }

        var res = await _realtimeClient.CancelCommandAsync(
            cmdId.Value,
            targetId.Value,
            "Cancelado manualmente pelo usuário no Windows",
            cancellationToken).ConfigureAwait(false);

        var msg = res.IsSuccess
            ? $"Cancelamento em tempo real enviado para o comando {cmdId:D}."
            : res.Error!.Message;

        await RunOnUiAsync(() => StatusBanner = msg, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendIntegrationEventAsync(CancellationToken cancellationToken)
    {
        string evtType = NexusEventTypes.NotificationReceived;
        string evtPayload = "{}";
        Guid? targetPeerId = null;

        await RunOnUiAsync(() =>
        {
            evtType = string.IsNullOrWhiteSpace(EventTypeInput) ? NexusEventTypes.NotificationReceived : EventTypeInput.Trim();
            evtPayload = string.IsNullOrWhiteSpace(EventPayloadInput) ? "{}" : EventPayloadInput;
            targetPeerId = SelectedPeer?.IsRealtimeConnected == true ? SelectedPeer.DeviceId : null;
        }, cancellationToken).ConfigureAwait(false);

        var integrationEvent = new IntegrationEvent
        {
            EventType = evtType,
            SourceDeviceId = _realtimeClient.CurrentSession?.DeviceId ?? _localDevice.DeviceId,
            PayloadJson = evtPayload,
            OccurredAtUtc = _clock.UtcNow
        };

        var result = await _realtimeClient.PublishIntegrationEventAsync(
            integrationEvent,
            targetPeerId,
            ordered: true,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            var errMsg = result.Error!.Message;
            await RunOnUiAsync(() => StatusBanner = errMsg, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ack = result.Value;
        await RunOnUiAsync(() =>
        {
            PrependCapped(RecentAcks, ack, 30);
            StatusBanner = $"Evento '{integrationEvent.EventType}' transmitido ({ack.Status}).";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendBulkHttpTestAsync(CancellationToken cancellationToken)
    {
        Guid? targetPeerId = null;
        await RunOnUiAsync(() => targetPeerId = SelectedPeer?.DeviceId, cancellationToken).ConfigureAwait(false);

        var sampleBytes = new byte[64 * 1024];
        Random.Shared.NextBytes(sampleBytes);
        var upload = await _bulkChannel.UploadLargePayloadAsync(
            sampleBytes,
            "application/octet-stream",
            targetPeerId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var msg = upload.IsSuccess
            ? $"Upload HTTP OK: {upload.Value.ByteLength} bytes · SHA-256 {upload.Value.Sha256Hex[..12]}..."
            : $"Upload HTTP falhou: {upload.Error!.Message}";

        await RunOnUiAsync(() => BulkTransferStatus = msg, cancellationToken).ConfigureAwait(false);
    }

    private void OnRealtimeStateChanged(object? sender, ConnectionStateSnapshot snapshot)
        => RunOnUi(() => SyncStateSnapshot(snapshot));

    private void OnRealtimePresenceUpdated(object? sender, IReadOnlyList<DevicePresenceSnapshot> list)
    {
        var ordered = OrderPresence(list);
        RunOnUi(() => ApplyPresenceList(ordered));
    }

    private void OnRealtimeMessageReceived(object? sender, RealtimeMessageEnvelope env)
        => RunOnUi(() => PrependCapped(RecentMessages, env, 30));

    private void OnRealtimeAckReceived(object? sender, RealtimeMessageAck ack)
        => RunOnUi(() => PrependCapped(RecentAcks, ack, 30));

    private void SyncStateSnapshot(ConnectionStateSnapshot snapshot)
    {
        CurrentState = snapshot.State;
        EndpointText = snapshot.Endpoint ?? "-";
        if (!string.IsNullOrWhiteSpace(snapshot.Reason))
        {
            StatusBanner = snapshot.Reason;
        }
    }

    private static IReadOnlyList<DevicePresenceSnapshot> OrderPresence(IReadOnlyList<DevicePresenceSnapshot> items)
        => items
            .OrderByDescending(i => i.IsRealtimeConnected)
            .ThenBy(i => i.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void ApplyPresenceList(IReadOnlyList<DevicePresenceSnapshot> orderedItems)
    {
        var previousId = SelectedPeer?.DeviceId;
        ReplaceCollection(PresenceItems, orderedItems);

        OnlinePeersCount = PresenceItems.Count(i => i.IsRealtimeConnected);
        OfflinePeersCount = PresenceItems.Count(i => !i.IsRealtimeConnected);

        SelectedPeer = previousId.HasValue
            ? PresenceItems.FirstOrDefault(p => p.DeviceId == previousId.Value) ?? PresenceItems.FirstOrDefault()
            : PresenceItems.FirstOrDefault();
    }

    private void NotifyActionCommands()
    {
        RefreshPresenceCommand.NotifyCanExecuteChanged();
        SendHeartbeatCommand.NotifyCanExecuteChanged();
        SendCommandCommand.NotifyCanExecuteChanged();
        CancelLastCommandCommand.NotifyCanExecuteChanged();
        SendIntegrationEventCommand.NotifyCanExecuteChanged();
    }

    public static string TranslateState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Conectado",
        ConnectionState.Connecting => "Conectando",
        ConnectionState.Authenticating => "Autenticando",
        ConnectionState.Reconnecting => "Reconectando",
        ConnectionState.Faulted => "Falha",
        ConnectionState.Degraded => "Instável",
        _ => "Desconectado"
    };

    public void Dispose()
    {
        _realtimeClient.StateChanged -= OnRealtimeStateChanged;
        _realtimeClient.PresenceUpdated -= OnRealtimePresenceUpdated;
        _realtimeClient.MessageReceived -= OnRealtimeMessageReceived;
        _realtimeClient.AckReceived -= OnRealtimeAckReceived;
    }
}
