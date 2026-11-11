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

public sealed class RealtimeViewModel : ObservableObject
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
        IClock clock)
    {
        _realtimeClient = realtimeClient;
        _bulkChannel = bulkChannel;
        _localDevice = localDevice;
        _clock = clock;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        RefreshPresenceCommand = new AsyncRelayCommand(RefreshPresenceAsync, () => _realtimeClient.IsConnected);
        SendHeartbeatCommand = new AsyncRelayCommand(SendHeartbeatAsync, () => _realtimeClient.IsConnected);
        SendCommandCommand = new AsyncRelayCommand(SendCommandAsync, () => _realtimeClient.IsConnected && SelectedPeer is not null);
        CancelLastCommandCommand = new AsyncRelayCommand(CancelLastCommandAsync, () => _realtimeClient.IsConnected && _lastCommandId.HasValue && _lastCommandTargetId.HasValue);
        SendIntegrationEventCommand = new AsyncRelayCommand(SendIntegrationEventAsync, () => _realtimeClient.IsConnected);
        SendBulkHttpTestCommand = new AsyncRelayCommand(SendBulkHttpTestAsync);

        _realtimeClient.StateChanged += (_, snapshot) => SyncStateSnapshot(snapshot);
        _realtimeClient.PresenceUpdated += (_, list) => ApplyPresenceList(list);
        _realtimeClient.MessageReceived += (_, env) => PrependItem(RecentMessages, env, 30);
        _realtimeClient.AckReceived += (_, ack) => PrependItem(RecentAcks, ack, 30);
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
        CurrentState = _realtimeClient.State;
        ApplyPresenceList(_realtimeClient.LivePresence);
        if (_realtimeClient.CurrentSession is { } session)
        {
            SessionIdText = session.SessionId.ToString("D");
        }

        if (_realtimeClient.IsConnected)
        {
            await RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        StatusBanner = "Conectando e autenticando no NexusHub (SignalR)...";
        var opened = await _realtimeClient.OpenAuthenticatedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            CurrentState = _realtimeClient.State;
            StatusBanner = $"Falha ao conectar: {opened.Error!.Message}";
            return;
        }

        CurrentState = opened.Value.State;
        SessionIdText = opened.Value.SessionId.ToString("D");
        EndpointText = _realtimeClient.LivePresence.FirstOrDefault(p => p.DeviceId == _localDevice.DeviceId)?.RouteSummary ?? "SignalR Ativo";
        ApplyPresenceList(_realtimeClient.LivePresence);
        StatusBanner = $"Conectado ao SignalR (Sessão {opened.Value.ConnectionId}).";
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _realtimeClient.CloseAsync(cancellationToken).ConfigureAwait(false);
        CurrentState = _realtimeClient.State;
        SessionIdText = "-";
        ApplyPresenceList(_realtimeClient.LivePresence);
        StatusBanner = "Sessão SignalR encerrada manualmente.";
    }

    private async Task RefreshPresenceAsync(CancellationToken cancellationToken)
    {
        var result = await _realtimeClient.RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            StatusBanner = result.Error!.Message;
            return;
        }

        ApplyPresenceList(result.Value);
        StatusBanner = $"Presença atualizada: {OnlinePeersCount} online, {OfflinePeersCount} desconectado(s).";
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var ping = await _realtimeClient.SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        if (ping.IsFailure)
        {
            StatusBanner = ping.Error!.Message;
            return;
        }

        LastHeartbeatText = $"{ping.Value} ms às {_clock.UtcNow.ToLocalTime():HH:mm:ss}";
        StatusBanner = $"Heartbeat SignalR confirmado em {ping.Value} ms.";
    }

    private async Task SendCommandAsync(CancellationToken cancellationToken)
    {
        if (SelectedPeer is null)
        {
            StatusBanner = "Selecione um dispositivo de destino.";
            return;
        }

        var command = new DeviceCommand
        {
            SourceDeviceId = _realtimeClient.CurrentSession?.DeviceId ?? _localDevice.DeviceId,
            TargetDeviceId = SelectedPeer.DeviceId,
            CommandName = CommandNameInput.Trim(),
            Arguments = new Dictionary<string, string> { ["payload"] = CommandArgsInput ?? string.Empty },
            IssuedAtUtc = _clock.UtcNow
        };

        _lastCommandId = command.CommandId;
        _lastCommandTargetId = command.TargetDeviceId;
        CancelLastCommandCommand.NotifyCanExecuteChanged();

        var ack = await _realtimeClient.SendCommandAsync(command, waitForAck: SelectedPeer.IsRealtimeConnected, cancellationToken)
            .ConfigureAwait(false);

        if (ack.IsFailure)
        {
            StatusBanner = $"Comando rejeitado ({ack.Error!.Code}): {ack.Error.Message}";
            return;
        }

        PrependItem(RecentAcks, ack.Value, 30);
        StatusBanner = $"Comando '{command.CommandName}' confirmado ({ack.Value.Status}) para '{SelectedPeer.DeviceName}'.";
    }

    private async Task CancelLastCommandAsync(CancellationToken cancellationToken)
    {
        if (!_lastCommandId.HasValue || !_lastCommandTargetId.HasValue)
        {
            return;
        }

        var res = await _realtimeClient.CancelCommandAsync(
            _lastCommandId.Value,
            _lastCommandTargetId.Value,
            "Cancelado manualmente pelo usuário no Windows",
            cancellationToken).ConfigureAwait(false);

        StatusBanner = res.IsSuccess
            ? $"Cancelamento em tempo real enviado para o comando {_lastCommandId:D}."
            : res.Error!.Message;
    }

    private async Task SendIntegrationEventAsync(CancellationToken cancellationToken)
    {
        var integrationEvent = new IntegrationEvent
        {
            EventType = string.IsNullOrWhiteSpace(EventTypeInput) ? NexusEventTypes.NotificationReceived : EventTypeInput.Trim(),
            SourceDeviceId = _realtimeClient.CurrentSession?.DeviceId ?? _localDevice.DeviceId,
            PayloadJson = string.IsNullOrWhiteSpace(EventPayloadInput) ? "{}" : EventPayloadInput,
            OccurredAtUtc = _clock.UtcNow
        };

        var result = await _realtimeClient.PublishIntegrationEventAsync(
            integrationEvent,
            SelectedPeer?.IsRealtimeConnected == true ? SelectedPeer.DeviceId : null,
            ordered: true,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            StatusBanner = result.Error!.Message;
            return;
        }

        PrependItem(RecentAcks, result.Value, 30);
        StatusBanner = $"Evento '{integrationEvent.EventType}' transmitido ({result.Value.Status}).";
    }

    private async Task SendBulkHttpTestAsync(CancellationToken cancellationToken)
    {
        var sampleBytes = new byte[64 * 1024];
        Random.Shared.NextBytes(sampleBytes);
        var upload = await _bulkChannel.UploadLargePayloadAsync(
            sampleBytes,
            "application/octet-stream",
            SelectedPeer?.DeviceId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        BulkTransferStatus = upload.IsSuccess
            ? $"Upload HTTP OK: {upload.Value.ByteLength} bytes · SHA-256 {upload.Value.Sha256Hex[..12]}..."
            : $"Upload HTTP falhou: {upload.Error!.Message}";
    }

    private void SyncStateSnapshot(ConnectionStateSnapshot snapshot)
    {
        CurrentState = snapshot.State;
        EndpointText = snapshot.Endpoint ?? "-";
        if (!string.IsNullOrWhiteSpace(snapshot.Reason))
        {
            StatusBanner = snapshot.Reason;
        }
    }

    private void ApplyPresenceList(IReadOnlyList<DevicePresenceSnapshot> items)
    {
        var previousId = SelectedPeer?.DeviceId;
        PresenceItems.Clear();

        foreach (var item in items
                     .OrderByDescending(i => i.IsRealtimeConnected)
                     .ThenBy(i => i.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            PresenceItems.Add(item);
        }

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

    private static void PrependItem<T>(ObservableCollection<T> collection, T item, int cap)
    {
        collection.Insert(0, item);
        while (collection.Count > cap)
        {
            collection.RemoveAt(collection.Count - 1);
        }
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
}
