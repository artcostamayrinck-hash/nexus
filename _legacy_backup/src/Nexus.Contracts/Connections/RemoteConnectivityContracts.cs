using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Connections;

public enum ConnectionRouteKind
{
    Unknown = 0,
    LocalLan = 1,
    RemoteVpnTailscale = 2,
    RemotePrivate = 3,
    RelayFuture = 4
}

public enum VpnStatus
{
    Unknown = 0,
    Connected = 1,
    Disconnected = 2,
    Unavailable = 3
}

public enum EndpointOrigin
{
    Manual = 0,
    MdnsDiscovered = 1,
    PairingQr = 2,
    TailscalePrivate = 3
}

public enum RouteFailureReason
{
    None = 0,
    InvalidEndpoint = 1,
    NoEndpointsConfigured = 2,
    LocalNetworkUnavailable = 3,
    VpnUnavailable = 4,
    InternetUnavailable = 5,
    HostUnreachable = 6,
    Timeout = 7,
    TlsPinMismatch = 8,
    IdentityMismatch = 9,
    Unauthorized = 10,
    DeviceRevoked = 11,
    Cancelled = 12
}

/// <summary>
/// Persisted local or remote (Tailscale/private) endpoint registered for a host device.
/// </summary>
public sealed record DeviceEndpointRecord : IVersionedContract, IValidatableContract
{
    public Guid EndpointId { get; init; } = Guid.CreateVersion7();
    public Guid DeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string DeviceName { get; init; } = "NEXUS Host";

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string Label { get; init; } = "Principal";

    public ConnectionRouteKind RouteKind { get; init; } = ConnectionRouteKind.LocalLan;
    public TransportKind Transport { get; init; } = TransportKind.LocalNetwork;

    [Required]
    [StringLength(512, MinimumLength = 8)]
    public string EndpointUrl { get; init; } = string.Empty;

    public int Priority { get; init; } = 10;
    public string? ExpectedTlsThumbprint { get; init; }
    public string? ExpectedHostKeyFingerprint { get; init; }
    public EndpointOrigin Origin { get; init; } = EndpointOrigin.Manual;
    public bool IsEnabled { get; init; } = true;
    public int? LastLatencyMs { get; init; }
    public DateTimeOffset? LastVerifiedAtUtc { get; init; }
    public DateTimeOffset? LastConnectedAtUtc { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public static int DefaultPriorityFor(ConnectionRouteKind routeKind) => routeKind switch
    {
        ConnectionRouteKind.LocalLan => 10,
        ConnectionRouteKind.RemoteVpnTailscale => 50,
        ConnectionRouteKind.RemotePrivate => 80,
        ConnectionRouteKind.RelayFuture => 200,
        _ => 100
    };

    public IEnumerable<ContractValidationError> Validate()
    {
        if (EndpointId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(EndpointId), "ENDPOINT_ID_EMPTY", "EndpointId cannot be empty.");
        }

        if (RouteKind == ConnectionRouteKind.Unknown)
        {
            yield return new ContractValidationError(nameof(RouteKind), "ROUTE_KIND_UNKNOWN", "RouteKind must be specified.");
        }

        if (Priority < 0)
        {
            yield return new ContractValidationError(nameof(Priority), "ENDPOINT_PRIORITY_NEGATIVE", "Priority must be non-negative.");
        }

        if (!IsValidHttpOrWsUri(EndpointUrl))
        {
            yield return new ContractValidationError(nameof(EndpointUrl), "ENDPOINT_URL_INVALID", "EndpointUrl must be a valid HTTP, HTTPS, WS, or WSS URL with a host.");
        }

        if (LastLatencyMs is < 0)
        {
            yield return new ContractValidationError(nameof(LastLatencyMs), "LATENCY_NEGATIVE", "LastLatencyMs cannot be negative.");
        }
    }

    public static bool IsValidHttpOrWsUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var schemeValid = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

        return schemeValid && !string.IsNullOrWhiteSpace(uri.Host) && uri.Port > 0;
    }
}

public sealed record NetworkEnvironmentSnapshot : IVersionedContract
{
    public bool IsNetworkAvailable { get; init; }
    public bool HasInternetAccess { get; init; }
    public bool HasLocalLanInterface { get; init; }
    public bool IsCellularActive { get; init; }
    public VpnStatus VpnStatus { get; init; } = VpnStatus.Unknown;
    public string? VpnProvider { get; init; }
    public IReadOnlyList<string> LocalIpv4Addresses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TailscaleAddresses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InterfaceSummaries { get; init; } = Array.Empty<string>();
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record MdnsDiscoveredEndpoint : IVersionedContract, IValidatableContract
{
    public Guid ServiceDeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string ServiceName { get; init; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string HostName { get; init; } = string.Empty;

    [Required]
    [StringLength(512, MinimumLength = 8)]
    public string EndpointUrl { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 7443;

    public string? TlsThumbprint { get; init; }
    public string? HostKeyFingerprint { get; init; }
    public IReadOnlyDictionary<string, string> TxtProperties { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset DiscoveredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (!DeviceEndpointRecord.IsValidHttpOrWsUri(EndpointUrl))
        {
            yield return new ContractValidationError(nameof(EndpointUrl), "MDNS_ENDPOINT_INVALID", "Discovered mDNS EndpointUrl is invalid.");
        }
    }
}

public sealed record RouteProbeResult : IVersionedContract
{
    public Guid EndpointId { get; init; }
    public ConnectionRouteKind RouteKind { get; init; } = ConnectionRouteKind.Unknown;
    public string EndpointUrl { get; init; } = string.Empty;
    public bool IsReachable { get; init; }
    public int? LatencyMs { get; init; }
    public Guid? ObservedServerDeviceId { get; init; }
    public string? ObservedServerName { get; init; }
    public string? ObservedTlsThumbprint { get; init; }
    public string? ObservedHostKeyFingerprint { get; init; }
    public RouteFailureReason FailureReason { get; init; } = RouteFailureReason.None;
    public string? DiagnosticMessage { get; init; }
    public DateTimeOffset ProbedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record ConnectivityStatusReport : IVersionedContract
{
    public ConnectionState State { get; init; } = ConnectionState.Disconnected;
    public string DeviceName { get; init; } = "Nenhum dispositivo";
    public Guid? RemoteDeviceId { get; init; }
    public Guid? ActiveEndpointId { get; init; }
    public ConnectionRouteKind RouteKind { get; init; } = ConnectionRouteKind.Unknown;
    public TransportKind Transport { get; init; } = TransportKind.Unknown;
    public string? ActiveEndpoint { get; init; }
    public int? LatencyMs { get; init; }
    public DateTimeOffset? LastConnectedAtUtc { get; init; }
    public DateTimeOffset? LastProbeAtUtc { get; init; }
    public VpnStatus VpnStatus { get; init; } = VpnStatus.Unknown;
    public string? VpnDetail { get; init; }
    public RouteFailureReason LastFailureReason { get; init; } = RouteFailureReason.None;
    public string? StatusReason { get; init; }
    public bool AutoReconnectEnabled { get; init; } = true;
    public int ActiveOperationsCount { get; init; }
    public bool HandoverDeferred { get; init; }
    public IReadOnlyList<DeviceEndpointRecord> KnownEndpoints { get; init; } = Array.Empty<DeviceEndpointRecord>();
    public IReadOnlyList<RouteProbeResult> LastProbeResults { get; init; } = Array.Empty<RouteProbeResult>();
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record RegisterEndpointRequest : IVersionedContract, IValidatableContract
{
    public Guid DeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string DeviceName { get; init; } = "NEXUS Host";

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string Label { get; init; } = "Manual";

    public ConnectionRouteKind RouteKind { get; init; } = ConnectionRouteKind.LocalLan;

    [Required]
    [StringLength(512, MinimumLength = 8)]
    public string EndpointUrl { get; init; } = string.Empty;

    public int? Priority { get; init; }
    public string? ExpectedTlsThumbprint { get; init; }
    public string? ExpectedHostKeyFingerprint { get; init; }
    public EndpointOrigin Origin { get; init; } = EndpointOrigin.Manual;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (RouteKind == ConnectionRouteKind.Unknown)
        {
            yield return new ContractValidationError(nameof(RouteKind), "ROUTE_KIND_UNKNOWN", "RouteKind cannot be Unknown.");
        }

        if (!DeviceEndpointRecord.IsValidHttpOrWsUri(EndpointUrl))
        {
            yield return new ContractValidationError(nameof(EndpointUrl), "ENDPOINT_URL_INVALID", "EndpointUrl must be a valid absolute HTTP(S) or WS(S) URL.");
        }
    }
}

public sealed record SelectRouteRequest : IVersionedContract
{
    public Guid? PreferredEndpointId { get; init; }
    public bool ForceReprobe { get; init; } = true;
    public bool AllowInterruptActiveOperations { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record ServerConnectivityAddressesDto : IVersionedContract
{
    public Guid DeviceId { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public int Port { get; init; }
    public bool UseTls { get; init; }
    public IReadOnlyList<string> LocalEndpoints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TailscaleEndpoints { get; init; } = Array.Empty<string>();
    public string? TailscaleHostHint { get; init; }
    public VpnStatus VpnStatus { get; init; } = VpnStatus.Unknown;
    public string MdnsServiceType { get; init; } = "_nexus._tcp.local.";
    public bool MdnsActive { get; init; }
    public string? TlsThumbprint { get; init; }
    public string? HostKeyFingerprint { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

