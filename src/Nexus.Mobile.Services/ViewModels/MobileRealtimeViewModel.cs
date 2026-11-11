using System.Collections.ObjectModel;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

public sealed class MobileRealtimeViewModel : MobileObservableObject
{
    private readonly IRealtimeClientChannel _realtimeClient;
    private readonly IHttpBulkTransferChannel _bulkChannel;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IClock _clock;

    private ConnectionState _currentState = ConnectionState.Disconnected;
    private string _connectionStateText = "Desconectado";
    private string _endpointText = "-";
    private string _statusText = "Canal SignalR pronto. Conecte após parear.";
    private string _commandName = "android.ping";
    private string _commandPayload = "Olá do Android";
    private string _eventType = NexusEventTypes.NotificationReceived;
    private string _eventPayload = "{\"source\":\"android\"}";
    private string _lastAckText = "-";
    private DevicePresenceSnapshot? _selectedPeer;
    private Guid? _lastCommandId;
    private Guid? _lastCommandTargetId;
    private int _onlineCount;
    private int _offlineCount;

    public MobileRealtimeViewModel(
        IRealtimeClientChannel realtimeClient,
        IHttpBulkTransferChannel bulkChannel,
        ILocalDeviceContext localDevice,
        IClock clock)
    {
        _realtimeClient = realtimeClient;
        _bulkChannel = bulkChannel;
        _localDevice = localDevice;
        _clock = clock;

        ConnectCommand = new MobileAsyncCommand(ConnectAsync);
        DisconnectCommand = new MobileAsyncCommand(DisconnectAsync);
        RefreshPresenceCommand = new MobileAsyncCommand(RefreshPresenceAsync, () => _realtimeClient.IsConnected);
        SendHeartbeatCommand = new MobileAsyncCommand(SendHeartbeatAsync, () => _realtimeClient.IsConnected);
        SendCommandCommand = new MobileAsyncCommand(SendCommandAsync, () => _realtimeClient.IsConnected && SelectedPeer is not null);
        CancelLastCommandCommand = new MobileAsyncCommand(CancelLastCommandAsync, () => _realtimeClient.IsConnected && _lastCommandId.HasValue && _lastCommandTargetId.HasValue);
        SendEventCommand = new MobileAsyncCommand(SendEventAsync, () => _realtimeClient.IsConnected);
        SendBulkHttpCommand = new MobileAsyncCommand(SendBulkHttpAsync);

        _realtimeClient.StateChanged += (_, s) =>
        {
            CurrentState = s.State;
            EndpointText = s.Endpoint ?? "-";
            if (!string.IsNullOrWhiteSpace(s.Reason))
            {
                StatusText = s.Reason;
            }
        };
        _realtimeClient.PresenceUpdated += (_, list) => ApplyPresence(list);
        _realtimeClient.MessageReceived += (_, msg) => Prepend(RecentMessages, msg, 25);
        _realtimeClient.AckReceived += (_, ack) =>
        {
            Prepend(RecentAcks, ack, 25);
            LastAckText = $"{ack.Status}: {ack.Detail}";
        };
    }

    public ObservableCollection<DevicePresenceSnapshot> PresenceItems { get; } = [];
    public ObservableCollection<RealtimeMessageEnvelope> RecentMessages { get; } = [];
    public ObservableCollection<RealtimeMessageAck> RecentAcks { get; } = [];

    public MobileAsyncCommand ConnectCommand { get; }
    public MobileAsyncCommand DisconnectCommand { get; }
    public MobileAsyncCommand RefreshPresenceCommand { get; }
    public MobileAsyncCommand SendHeartbeatCommand { get; }
    public MobileAsyncCommand SendCommandCommand { get; }
    public MobileAsyncCommand CancelLastCommandCommand { get; }
    public MobileAsyncCommand SendEventCommand { get; }
    public MobileAsyncCommand SendBulkHttpCommand { get; }

    public ConnectionState CurrentState
    {
        get => _currentState;
        private set
        {
            if (SetProperty(ref _currentState, value))
            {
                ConnectionStateText = value switch
                {
                    ConnectionState.Connected => "Conectado",
                    ConnectionState.Connecting => "Conectando",
                    ConnectionState.Authenticating => "Autenticando",
                    ConnectionState.Reconnecting => "Reconectando",
                    ConnectionState.Faulted => "Falha",
                    ConnectionState.Degraded => "Instável",
                    _ => "Desconectado"
                };
                NotifyActionCommands();
            }
        }
    }

    public string ConnectionStateText { get => _connectionStateText; private set => SetProperty(ref _connectionStateText, value); }
    public string EndpointText { get => _endpointText; private set => SetProperty(ref _endpointText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string CommandName { get => _commandName; set => SetProperty(ref _commandName, value); }
    public string CommandPayload { get => _commandPayload; set => SetProperty(ref _commandPayload, value); }
    public string EventType { get => _eventType; set => SetProperty(ref _eventType, value); }
    public string EventPayload { get => _eventPayload; set => SetProperty(ref _eventPayload, value); }
    public string LastAckText { get => _lastAckText; private set => SetProperty(ref _lastAckText, value); }
    public int OnlineCount { get => _onlineCount; private set => SetProperty(ref _onlineCount, value); }
    public int OfflineCount { get => _offlineCount; private set => SetProperty(ref _offlineCount, value); }

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
        ApplyPresence(_realtimeClient.LivePresence);
        if (_realtimeClient.IsConnected)
        {
            await RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        StatusText = "Conectando e autenticando SignalR...";
        var res = await _realtimeClient.OpenAuthenticatedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (res.IsFailure)
        {
            CurrentState = _realtimeClient.State;
            StatusText = $"Falha: {res.Error!.Message}";
            return;
        }

        CurrentState = res.Value.State;
        ApplyPresence(_realtimeClient.LivePresence);
        StatusText = $"Conectado (Sessão {res.Value.ConnectionId}).";
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _realtimeClient.CloseAsync(cancellationToken).ConfigureAwait(false);
        CurrentState = _realtimeClient.State;
        ApplyPresence(_realtimeClient.LivePresence);
        StatusText = "Sessão SignalR desconectada.";
    }

    private async Task RefreshPresenceAsync(CancellationToken cancellationToken)
    {
        var res = await _realtimeClient.RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            ApplyPresence(res.Value);
            StatusText = $"{OnlineCount} online · {OfflineCount} offline";
        }
        else
        {
            StatusText = res.Error!.Message;
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var ping = await _realtimeClient.SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        StatusText = ping.IsSuccess ? $"Heartbeat OK ({ping.Value} ms)." : ping.Error!.Message;
    }

    private async Task SendCommandAsync(CancellationToken cancellationToken)
    {
        if (SelectedPeer is null)
        {
            StatusText = "Selecione um destino na lista.";
            return;
        }

        var cmd = new DeviceCommand
        {
            SourceDeviceId = _realtimeClient.CurrentSession?.DeviceId ?? _localDevice.DeviceId,
            TargetDeviceId = SelectedPeer.DeviceId,
            CommandName = CommandName.Trim(),
            Arguments = new Dictionary<string, string> { ["payload"] = CommandPayload ?? string.Empty },
            IssuedAtUtc = _clock.UtcNow
        };

        _lastCommandId = cmd.CommandId;
        _lastCommandTargetId = cmd.TargetDeviceId;
        CancelLastCommandCommand.NotifyCanExecuteChanged();

        var ack = await _realtimeClient.SendCommandAsync(cmd, waitForAck: SelectedPeer.IsRealtimeConnected, cancellationToken)
            .ConfigureAwait(false);
        StatusText = ack.IsSuccess
            ? $"Comando confirmado ({ack.Value.Status})."
            : $"Rejeitado ({ack.Error!.Code}): {ack.Error.Message}";
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
            "Cancelado no Android",
            cancellationToken).ConfigureAwait(false);
        StatusText = res.IsSuccess ? "Cancelamento enviado." : res.Error!.Message;
    }

    private async Task SendEventAsync(CancellationToken cancellationToken)
    {
        var evt = new IntegrationEvent
        {
            EventType = string.IsNullOrWhiteSpace(EventType) ? NexusEventTypes.NotificationReceived : EventType.Trim(),
            SourceDeviceId = _realtimeClient.CurrentSession?.DeviceId ?? _localDevice.DeviceId,
            PayloadJson = string.IsNullOrWhiteSpace(EventPayload) ? "{}" : EventPayload,
            OccurredAtUtc = _clock.UtcNow
        };

        var res = await _realtimeClient.PublishIntegrationEventAsync(
            evt,
            SelectedPeer?.IsRealtimeConnected == true ? SelectedPeer.DeviceId : null,
            ordered: true,
            cancellationToken).ConfigureAwait(false);

        StatusText = res.IsSuccess ? $"Evento enviado ({res.Value.Status})." : res.Error!.Message;
    }

    private async Task SendBulkHttpAsync(CancellationToken cancellationToken)
    {
        var bytes = new byte[32 * 1024];
        Random.Shared.NextBytes(bytes);
        var res = await _bulkChannel.UploadLargePayloadAsync(bytes, "application/octet-stream", SelectedPeer?.DeviceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        StatusText = res.IsSuccess
            ? $"HTTP Bulk OK: {res.Value.ByteLength} B"
            : $"HTTP Bulk falhou: {res.Error!.Message}";
    }

    private void ApplyPresence(IReadOnlyList<DevicePresenceSnapshot> items)
    {
        var prevId = SelectedPeer?.DeviceId;
        PresenceItems.Clear();
        foreach (var item in items.OrderByDescending(i => i.IsRealtimeConnected).ThenBy(i => i.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            PresenceItems.Add(item);
        }

        OnlineCount = PresenceItems.Count(i => i.IsRealtimeConnected);
        OfflineCount = PresenceItems.Count(i => !i.IsRealtimeConnected);
        SelectedPeer = prevId.HasValue
            ? PresenceItems.FirstOrDefault(p => p.DeviceId == prevId.Value) ?? PresenceItems.FirstOrDefault()
            : PresenceItems.FirstOrDefault();
    }

    private void NotifyActionCommands()
    {
        RefreshPresenceCommand.NotifyCanExecuteChanged();
        SendHeartbeatCommand.NotifyCanExecuteChanged();
        SendCommandCommand.NotifyCanExecuteChanged();
        CancelLastCommandCommand.NotifyCanExecuteChanged();
        SendEventCommand.NotifyCanExecuteChanged();
    }

    private static void Prepend<T>(ObservableCollection<T> col, T item, int cap)
    {
        col.Insert(0, item);
        while (col.Count > cap)
        {
            col.RemoveAt(col.Count - 1);
        }
    }
}

