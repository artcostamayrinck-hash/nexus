using System.Collections.ObjectModel;
using Nexus.Contracts.Connections;
using Nexus.Core.Connections;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed record EndpointItemDisplay(
    Guid EndpointId,
    string Label,
    string DeviceName,
    string RouteKindText,
    string EndpointUrl,
    string LatencyText,
    string LastConnectedText,
    string StatusText,
    bool IsActive);

public sealed class ConnectivityViewModel : ObservableObject
{
    private readonly INexusRouteOrchestrator _orchestrator;
    private readonly INetworkEnvironmentInspector _networkInspector;

    private string _connectionStateText = "Desconectado";
    private string _deviceNameText = "Nenhum dispositivo";
    private string _routeTypeText = "-";
    private string _activeEndpointText = "-";
    private string _latencyText = "-- ms";
    private string _lastConnectedText = "-";
    private string _vpnStateText = "Verificando...";
    private string _localHostAddressesText = "Localizando interfaces...";
    private string _diagnosticMessage = "Pronto para descoberta local ou conexão via Tailscale.";

    private string _newEndpointLabel = "Tailscale Casa";
    private string _newEndpointUrl = "https://100.x.y.z:5443";
    private ConnectionRouteKind _newEndpointRouteKind = ConnectionRouteKind.RemoteVpnTailscale;
    private bool _autoReconnectEnabled = true;

    public ConnectivityViewModel(
        INexusRouteOrchestrator orchestrator,
        INetworkEnvironmentInspector networkInspector)
    {
        _orchestrator = orchestrator;
        _networkInspector = networkInspector;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        DiscoverMdnsCommand = new AsyncRelayCommand(DiscoverMdnsAsync);
        SelectBestRouteCommand = new AsyncRelayCommand(SelectBestRouteAsync);
        ProbeAllCommand = new AsyncRelayCommand(ProbeAllAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        AddEndpointCommand = new AsyncRelayCommand(AddEndpointAsync);
        RemoveEndpointCommand = new AsyncRelayCommand<Guid>(RemoveEndpointAsync);

        _orchestrator.StatusChanged += (_, report) => ApplyReport(report);
    }

    public ObservableCollection<EndpointItemDisplay> Endpoints { get; } = [];

    public IReadOnlyList<ConnectionRouteKind> AvailableRouteKinds { get; } =
    [
        ConnectionRouteKind.LocalLan,
        ConnectionRouteKind.RemoteVpnTailscale,
        ConnectionRouteKind.RemotePrivate
    ];

    public AsyncRelayCommand InitializeCommand { get; }
    public AsyncRelayCommand DiscoverMdnsCommand { get; }
    public AsyncRelayCommand SelectBestRouteCommand { get; }
    public AsyncRelayCommand ProbeAllCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand AddEndpointCommand { get; }
    public AsyncRelayCommand<Guid> RemoveEndpointCommand { get; }

    public string ConnectionStateText { get => _connectionStateText; private set => SetProperty(ref _connectionStateText, value); }
    public string DeviceNameText { get => _deviceNameText; private set => SetProperty(ref _deviceNameText, value); }
    public string RouteTypeText { get => _routeTypeText; private set => SetProperty(ref _routeTypeText, value); }
    public string ActiveEndpointText { get => _activeEndpointText; private set => SetProperty(ref _activeEndpointText, value); }
    public string LatencyText { get => _latencyText; private set => SetProperty(ref _latencyText, value); }
    public string LastConnectedText { get => _lastConnectedText; private set => SetProperty(ref _lastConnectedText, value); }
    public string VpnStateText { get => _vpnStateText; private set => SetProperty(ref _vpnStateText, value); }
    public string LocalHostAddressesText { get => _localHostAddressesText; private set => SetProperty(ref _localHostAddressesText, value); }
    public string DiagnosticMessage { get => _diagnosticMessage; private set => SetProperty(ref _diagnosticMessage, value); }

    public string NewEndpointLabel { get => _newEndpointLabel; set => SetProperty(ref _newEndpointLabel, value); }
    public string NewEndpointUrl { get => _newEndpointUrl; set => SetProperty(ref _newEndpointUrl, value); }
    public ConnectionRouteKind NewEndpointRouteKind { get => _newEndpointRouteKind; set => SetProperty(ref _newEndpointRouteKind, value); }

    public bool AutoReconnectEnabled
    {
        get => _autoReconnectEnabled;
        set
        {
            if (SetProperty(ref _autoReconnectEnabled, value))
            {
                _orchestrator.SetAutoReconnect(value);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var init = await _orchestrator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await RefreshLocalInterfacesAsync(cancellationToken).ConfigureAwait(false);
        if (init.IsSuccess)
        {
            ApplyReport(init.Value);
        }
    }

    private async Task RefreshLocalInterfacesAsync(CancellationToken cancellationToken)
    {
        var net = await _networkInspector.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var lanPart = net.LocalIpv4Addresses.Count > 0
            ? $"LAN: {string.Join(", ", net.LocalIpv4Addresses.Select(ip => $"https://{ip}:5443"))}"
            : "LAN: nenhuma interface Wi-Fi/Ethernet ativa";
        var tsPart = net.TailscaleAddresses.Count > 0
            ? $"Tailscale: {string.Join(", ", net.TailscaleAddresses.Select(ip => $"https://{ip}:5443"))}"
            : "Tailscale: não conectado neste PC";

        LocalHostAddressesText = $"{lanPart}  |  {tsPart}";
    }

    private async Task DiscoverMdnsAsync(CancellationToken cancellationToken)
    {
        DiagnosticMessage = "Procurando serviços NEXUS via mDNS na rede local...";
        var res = await _orchestrator.DiscoverLocalEndpointsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = res.IsSuccess
            ? $"Descoberta mDNS concluída: {res.Value.Count} serviço(s) encontrado(s)."
            : res.Error!.Message;
        ApplyReport(_orchestrator.CurrentStatus);
    }

    private async Task SelectBestRouteAsync(CancellationToken cancellationToken)
    {
        DiagnosticMessage = "Avaliando rotas (Local primeiro, depois Remoto/Tailscale)...";
        var res = await _orchestrator.SelectAndConnectBestRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = res.IsSuccess
            ? res.Value.StatusReason ?? "Rota selecionada."
            : res.Error!.Message;
        ApplyReport(_orchestrator.CurrentStatus);
    }

    private async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        var res = await _orchestrator.ProbeAllEndpointsAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = res.IsSuccess
            ? $"Verificação concluída: {res.Value.Count(r => r.IsReachable)}/{res.Value.Count} rota(s) alcançável(eis)."
            : res.Error!.Message;
        ApplyReport(_orchestrator.CurrentStatus);
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _orchestrator.DisconnectAsync("Desconectado manualmente no Desktop.", cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = "Conexão encerrada manualmente.";
    }

    private async Task AddEndpointAsync(CancellationToken cancellationToken)
    {
        var req = new RegisterEndpointRequest
        {
            DeviceId = Guid.Empty,
            DeviceName = "Servidor NEXUS",
            Label = string.IsNullOrWhiteSpace(NewEndpointLabel) ? "Personalizado" : NewEndpointLabel.Trim(),
            RouteKind = NewEndpointRouteKind,
            EndpointUrl = NewEndpointUrl.Trim(),
            Origin = NewEndpointRouteKind == ConnectionRouteKind.RemoteVpnTailscale
                ? EndpointOrigin.TailscalePrivate
                : EndpointOrigin.Manual
        };

        var saved = await _orchestrator.RegisterEndpointAsync(req, cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = saved.IsSuccess
            ? $"Endpoint registrado ({FormatRouteKind(saved.Value.RouteKind)}): {saved.Value.EndpointUrl}"
            : saved.Error!.Message;

        if (saved.IsSuccess)
        {
            ApplyReport(_orchestrator.CurrentStatus);
        }
    }

    private async Task RemoveEndpointAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var res = await _orchestrator.RemoveEndpointAsync(endpointId, cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = res.IsSuccess ? "Endpoint removido." : res.Error!.Message;
        ApplyReport(_orchestrator.CurrentStatus);
    }

    private void ApplyReport(ConnectivityStatusReport report)
    {
        ConnectionStateText = TranslateState(report.State);
        DeviceNameText = report.DeviceName;
        RouteTypeText = FormatRouteKind(report.RouteKind);
        ActiveEndpointText = string.IsNullOrWhiteSpace(report.ActiveEndpoint) ? "-" : report.ActiveEndpoint;
        LatencyText = report.LatencyMs.HasValue ? $"{report.LatencyMs.Value} ms" : "-- ms";
        LastConnectedText = report.LastConnectedAtUtc.HasValue
            ? report.LastConnectedAtUtc.Value.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss")
            : "-";
        VpnStateText = report.VpnDetail ?? report.VpnStatus.ToString();
        _autoReconnectEnabled = report.AutoReconnectEnabled;
        OnPropertyChanged(nameof(AutoReconnectEnabled));

        if (!string.IsNullOrWhiteSpace(report.StatusReason))
        {
            DiagnosticMessage = report.StatusReason;
        }

        Endpoints.Clear();
        foreach (var ep in report.KnownEndpoints)
        {
            var isActive = report.ActiveEndpointId == ep.EndpointId;
            Endpoints.Add(new EndpointItemDisplay(
                ep.EndpointId,
                ep.Label,
                ep.DeviceName,
                FormatRouteKind(ep.RouteKind),
                ep.EndpointUrl,
                ep.LastLatencyMs.HasValue ? $"{ep.LastLatencyMs.Value} ms" : "--",
                ep.LastConnectedAtUtc.HasValue ? ep.LastConnectedAtUtc.Value.LocalDateTime.ToString("dd/MM HH:mm:ss") : "-",
                isActive ? "Ativa" : (string.IsNullOrWhiteSpace(ep.LastError) ? "Configurada" : ep.LastError),
                isActive));
        }
    }

    public static string FormatRouteKind(ConnectionRouteKind kind) => kind switch
    {
        ConnectionRouteKind.LocalLan => "Local (Wi-Fi / LAN)",
        ConnectionRouteKind.RemoteVpnTailscale => "Remoto (VPN Tailscale)",
        ConnectionRouteKind.RemotePrivate => "Remoto Privado",
        ConnectionRouteKind.RelayFuture => "Relay (Futuro)",
        _ => "Nenhuma"
    };

    private static string TranslateState(ConnectionState state) => state switch
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

