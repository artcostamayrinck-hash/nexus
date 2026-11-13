using Nexus.Contracts.Connections;
using Nexus.Core.Errors;

namespace Nexus.Core.Connections;

/// <summary>
/// Tracks an in-flight logical operation so the route orchestrator does not abruptly switch
/// transport routes while critical operations are actively running.
/// </summary>
public interface IOperationLease : IAsyncDisposable, IDisposable
{
    Guid LeaseId { get; }
    string OperationName { get; }
    DateTimeOffset StartedAtUtc { get; }
}

/// <summary>
/// Persists configured and discovered endpoints for local LAN and remote/Tailscale routes.
/// </summary>
public interface IEndpointRegistryRepository
{
    Task<Result<IReadOnlyList<DeviceEndpointRecord>>> ListAsync(Guid? deviceId = null, CancellationToken cancellationToken = default);
    Task<Result<DeviceEndpointRecord?>> GetByIdAsync(Guid endpointId, CancellationToken cancellationToken = default);
    Task<Result<DeviceEndpointRecord>> UpsertAsync(DeviceEndpointRecord endpoint, CancellationToken cancellationToken = default);
    Task<Result> DeleteAsync(Guid endpointId, CancellationToken cancellationToken = default);
    Task<Result> UpdateTelemetryAsync(
        Guid endpointId,
        int? latencyMs,
        DateTimeOffset? verifiedAtUtc,
        DateTimeOffset? connectedAtUtc,
        string? lastError,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Low-level OS/platform network state reader (injectable for mobile, desktop, and deterministic tests).
/// </summary>
public interface INetworkConnectivityAdapter
{
    NetworkEnvironmentSnapshot CaptureSnapshot();
}

/// <summary>
/// Inspects network interfaces, detects Tailscale private VPN connectivity, classifies endpoints,
/// and raises change notifications when connectivity interfaces shift.
/// </summary>
public interface INetworkEnvironmentInspector
{
    NetworkEnvironmentSnapshot CurrentSnapshot { get; }
    event EventHandler<NetworkEnvironmentSnapshot>? NetworkEnvironmentChanged;

    Task<NetworkEnvironmentSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    ConnectionRouteKind ClassifyEndpoint(string endpointUrl);
    bool IsTailscaleHostOrAddress(string host);
    bool IsLocalLanHostOrAddress(string host);
}

/// <summary>
/// Advertises and discovers NEXUS host services over LAN multicast DNS (mDNS / RFC 6762).
/// Local discovery enhances convenience but is never mandatory for connecting.
/// </summary>
public interface IMdnsServiceDiscovery : IAsyncDisposable
{
    bool IsAdvertising { get; }

    Task<Result<IReadOnlyList<MdnsDiscoveredEndpoint>>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<Result> StartAdvertisingAsync(
        MdnsDiscoveredEndpoint announcement,
        CancellationToken cancellationToken = default);

    Task StopAdvertisingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies reachability, RTT latency, TLS pinning, and host cryptographic fingerprint for a candidate endpoint.
/// </summary>
public interface IRouteProber
{
    Task<RouteProbeResult> ProbeAsync(
        DeviceEndpointRecord endpoint,
        NetworkEnvironmentSnapshot networkEnvironment,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Central NEXUS Connection Manager orchestrating mDNS discovery, endpoint registration,
/// local-first then remote/VPN route selection, health monitoring, latency tracking,
/// auto-reconnect, and non-disruptive route handover.
/// </summary>
public interface INexusRouteOrchestrator : IAsyncDisposable
{
    ConnectivityStatusReport CurrentStatus { get; }
    event EventHandler<ConnectivityStatusReport>? StatusChanged;

    Task<Result<ConnectivityStatusReport>> InitializeAsync(CancellationToken cancellationToken = default);
    Task<Result<DeviceEndpointRecord>> RegisterEndpointAsync(RegisterEndpointRequest request, CancellationToken cancellationToken = default);
    Task<Result> RemoveEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default);
    Task<Result<DeviceEndpointRecord>> SetEndpointEnabledAsync(Guid endpointId, bool enabled, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<MdnsDiscoveredEndpoint>>> DiscoverLocalEndpointsAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task<Result<ConnectivityStatusReport>> SelectAndConnectBestRouteAsync(SelectRouteRequest? request = null, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<RouteProbeResult>>> ProbeAllEndpointsAsync(CancellationToken cancellationToken = default);
    Task<Result<ConnectivityStatusReport>> HandleNetworkChangedAsync(CancellationToken cancellationToken = default);
    Task<Result<ConnectivityStatusReport>> DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default);
    void SetAutoReconnect(bool enabled);

    Task<Result<IOperationLease>> BeginOperationScopeAsync(string operationName, CancellationToken cancellationToken = default);
}

