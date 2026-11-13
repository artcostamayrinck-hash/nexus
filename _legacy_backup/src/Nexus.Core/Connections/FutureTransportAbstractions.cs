using Nexus.Core.Errors;

namespace Nexus.Core.Connections;

// ============================================================================
// Future Transport & NAT Traversal Abstractions (Fase 04 Preparation Only)
// Cloud / VPS / Relay implementations are deferred to dedicated future phases.
// ============================================================================

public enum NatType
{
    Unknown = 0,
    OpenInternet = 1,
    FullCone = 2,
    RestrictedCone = 3,
    PortRestrictedCone = 4,
    Symmetric = 5,
    Blocked = 6
}

public sealed record SignalingEnvelope(
    Guid SessionId,
    Guid SenderDeviceId,
    Guid RecipientDeviceId,
    string SignalType,
    string PayloadJson,
    DateTimeOffset SentAtUtc);

/// <summary>
/// Future abstraction for peer signaling exchange (SDP/ICE/session rendezvous).
/// </summary>
public interface ISignalingChannel : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<Result> ConnectAsync(Uri signalingServerUri, CancellationToken cancellationToken = default);
    Task<Result> SendSignalAsync(SignalingEnvelope envelope, CancellationToken cancellationToken = default);
    Task<Result> DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed record RelayAllocation(
    Guid AllocationId,
    Uri RelayEndpoint,
    string AllocationToken,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Future abstraction for encrypted communication relay when direct/VPN routes are unavailable.
/// </summary>
public interface IRelayTransport : IAsyncDisposable
{
    Task<Result<RelayAllocation>> AllocateRelaySessionAsync(
        Guid targetDeviceId,
        CancellationToken cancellationToken = default);

    Task<Result> SendRelayedFrameAsync(
        Guid allocationId,
        ReadOnlyMemory<byte> encryptedPayload,
        CancellationToken cancellationToken = default);
}

public sealed record VpsGatewayDescriptor(
    string GatewayName,
    Uri PublicEndpoint,
    string? ExpectedTlsThumbprint,
    bool SupportsWireGuardBridge,
    bool SupportsWebSocketRelay);

/// <summary>
/// Future abstraction for connecting through a self-hosted private VPS gateway.
/// </summary>
public interface IVpsGatewayProvider
{
    Task<Result<VpsGatewayDescriptor>> ResolveGatewayAsync(CancellationToken cancellationToken = default);
    Task<Result<int>> CheckGatewayLatencyAsync(VpsGatewayDescriptor gateway, CancellationToken cancellationToken = default);
}

public sealed record NatMappingResult(
    NatType DetectedNatType,
    string? MappedPublicAddress,
    int? MappedPublicPort,
    bool DirectHolePunchPossible,
    DateTimeOffset ProbedAtUtc);

/// <summary>
/// Future abstraction for NAT traversal classification and UDP/QUIC hole punching readiness.
/// </summary>
public interface INatTraversalProbe
{
    Task<Result<NatMappingResult>> ProbeNatAsync(CancellationToken cancellationToken = default);
}

public sealed record IceServerConfiguration(
    Uri ServerUri,
    string? Username = null,
    string? Credential = null);

public sealed record IceCandidateDescriptor(
    string CandidateType,
    string Address,
    int Port,
    string Protocol,
    int Priority);

/// <summary>
/// Future abstraction for STUN binding discovery and TURN relay candidate gathering.
/// </summary>
public interface IStunTurnClient
{
    Task<Result<IReadOnlyList<IceCandidateDescriptor>>> GatherCandidatesAsync(
        IReadOnlyList<IceServerConfiguration> servers,
        CancellationToken cancellationToken = default);
}

