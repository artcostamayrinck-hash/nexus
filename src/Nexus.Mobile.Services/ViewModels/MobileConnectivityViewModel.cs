using System.Collections.ObjectModel;
using Nexus.Contracts.Connections;
using Nexus.Core.Connections;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

public sealed record MobileEndpointItemDisplay(
    Guid EndpointId,
    string Label,
    string DeviceName,
    string RouteKindText,
    string EndpointUrl,
    string LatencyText,
    string LastConnectedText,
    string StatusText,
    bool IsActive);

/// <summary>
/// Android ViewModel for local mDNS + Tailscale VPN connectivity telemetry,
/// automatic route prioritization, and manual endpoint registration.
/// </summary>
public sealed class MobileConnectivityViewModel : MobileObservableObject
{
    private readonly INexusRouteOrchestrator _orchestrator;

    private string _connectionStateText = "Desconectado";
    private string _deviceNameText = "Nenhum dispositivo";
    private string _routeTypeText = "Nenhuma";
    private string _activeEndpointText = "-";
    private string _latencyText = "-- ms";
    private string _lastConnectedText = "-";
    private string _vpnStateText = "Verificando Tailscale...";
    private string _diagnosticMessage = "Toque em Conectar Melhor Rota ou cadastre seu endereço Tailscale.";

    private string _newEndpointLabel = "PC Tailscale";
    private string _newEndpointUrl = "https://100.x.y.z:5443";
    private ConnectionRouteKind _newEndpointRouteKind = ConnectionRouteKind.RemoteVpnTailscale;
    private bool _autoReconnectEnabled = true;

    public MobileConnectivityViewModel(INexusRouteOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;

        InitializeCommand = new MobileAsyncCommand(InitializeAsync);
        DiscoverMdnsCommand = new MobileAsyncCommand(DiscoverMdnsAsync);
        SelectBestRouteCommand = new MobileAsyncCommand(SelectBestRouteAsync);
        DisconnectCommand = new MobileAsyncCommand(DisconnectAsync);
        AddEndpointCommand = new MobileAsyncCommand(AddEndpointAsync);
        RemoveEndpointCommand = new MobileAsyncCommand<Guid>(RemoveEndpointAsync);

        _orchestrator.StatusChanged += (_, report) => ApplyReport(report);
    }

    public ObservableCollection<MobileEndpointItemDisplay> Endpoints { get; } = [];

    public IReadOnlyList<ConnectionRouteKind> AvailableRouteKinds { get; } =
    [
        ConnectionRouteKind.LocalLan,
        ConnectionRouteKind.RemoteVpnTailscale,
        ConnectionRouteKind.RemotePrivate
    ];

    public MobileAsyncCommand InitializeCommand { get; }
    public MobileAsyncCommand DiscoverMdnsCommand { get; }
    public MobileAsyncCommand SelectBestRouteCommand { get; }
    public MobileAsyncCommand DisconnectCommand { get; }
    public MobileAsyncCommand AddEndpointCommand { get; }
    public MobileAsyncCommand<Guid> RemoveEndpointCommand { get; }

    public string ConnectionStateText { get => _connectionStateText; private set => SetProperty(ref _connectionStateText, value); }
    public string DeviceNameText { get => _deviceNameText; private set => SetProperty(ref _deviceNameText, value); }
    public string RouteTypeText { get => _routeTypeText; private set => SetProperty(ref _routeTypeText, value); }
    public string ActiveEndpointText { get => _activeEndpointText; private set => SetProperty(ref _activeEndpointText, value); }
    public string LatencyText { get => _latencyText; private set => SetProperty(ref _latencyText, value); }
    public string LastConnectedText { get => _lastConnectedText; private set => SetProperty(ref _lastConnectedText, value); }
    public string VpnStateText { get => _vpnStateText; private set => SetProperty(ref _vpnStateText, value); }
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
        if (init.IsSuccess)
        {
            ApplyReport(init.Value);
        }
    }

    private async Task DiscoverMdnsAsync(CancellationToken cancellationToken)
    {
        DiagnosticMessage = "Buscando PC Windows na mesma rede Wi-Fi via mDNS...";
        var res = await _orchestrator.DiscoverLocalEndpointsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = res.IsSuccess
            ? $"Descoberta concluída: {res.Value.Count} serviço(s) encontrado(s)."
            : res.Error!.Message;
        ApplyReport(_orchestrator.CurrentStatus);
    }

    private async Task SelectBestRouteAsync(CancellationToken cancellationToken)
    {
        DiagnosticMessage = "Testando rotas (Wi-Fi Local -> VPN Tailscale)...";
        var res = await _orchestrator.SelectAndConnectBestRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = res.IsSuccess
            ? res.Value.StatusReason ?? "Conectado com sucesso."
            : res.Error!.Message;
        ApplyReport(_orchestrator.CurrentStatus);
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _orchestrator.DisconnectAsync("Desconectado manualmente no Android.", cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = "Desconectado.";
    }

    private async Task AddEndpointAsync(CancellationToken cancellationToken)
    {
        var req = new RegisterEndpointRequest
        {
            DeviceId = Guid.Empty,
            DeviceName = "Computador NEXUS",
            Label = string.IsNullOrWhiteSpace(NewEndpointLabel) ? "Personalizado" : NewEndpointLabel.Trim(),
            RouteKind = NewEndpointRouteKind,
            EndpointUrl = NewEndpointUrl.Trim(),
            Origin = NewEndpointRouteKind == ConnectionRouteKind.RemoteVpnTailscale
                ? EndpointOrigin.TailscalePrivate
                : EndpointOrigin.Manual
        };

        var saved = await _orchestrator.RegisterEndpointAsync(req, cancellationToken).ConfigureAwait(false);
        DiagnosticMessage = saved.IsSuccess
            ? $"Endpoint salvo: {saved.Value.EndpointUrl}"
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
            Endpoints.Add(new MobileEndpointItemDisplay(
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

    private static string FormatRouteKind(ConnectionRouteKind kind) => kind switch
    {
        ConnectionRouteKind.LocalLan => "Local (Wi-Fi / LAN)",
        ConnectionRouteKind.RemoteVpnTailscale => "Remota (VPN Tailscale)",
        ConnectionRouteKind.RemotePrivate => "Remota Privada",
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

