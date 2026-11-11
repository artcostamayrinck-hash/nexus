using Nexus.Core.Errors;

namespace Nexus.Core.Connections;

/// <summary>
/// Default placeholders for future cloud/NAT/relay transport abstractions.
/// Returns clear diagnostic errors without spinning up any cloud infrastructure in Phase 04.
/// </summary>
public sealed class UnconfiguredSignalingChannel : ISignalingChannel
{
    private static readonly NexusError NotConfiguredError = NexusError.Validation(
        "Signaling channel provider is not configured in this deployment phase.",
        code: "SIGNALING_NOT_CONFIGURED");

    public bool IsConnected => false;

    public Task<Result> ConnectAsync(Uri signalingServerUri, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NotConfiguredError));

    public Task<Result> SendSignalAsync(SignalingEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NotConfiguredError));

    public Task<Result> DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class UnconfiguredRelayTransport : IRelayTransport
{
    private static readonly NexusError NotConfiguredError = NexusError.Validation(
        "Relay transport provider is not configured in this deployment phase.",
        code: "RELAY_NOT_CONFIGURED");

    public Task<Result<RelayAllocation>> AllocateRelaySessionAsync(Guid targetDeviceId, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<RelayAllocation>(NotConfiguredError));

    public Task<Result> SendRelayedFrameAsync(Guid allocationId, ReadOnlyMemory<byte> encryptedPayload, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NotConfiguredError));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class UnconfiguredVpsGatewayProvider : IVpsGatewayProvider
{
    private static readonly NexusError NotConfiguredError = NexusError.Validation(
        "Self-hosted VPS gateway is not configured in this deployment phase.",
        code: "VPS_GATEWAY_NOT_CONFIGURED");

    public Task<Result<VpsGatewayDescriptor>> ResolveGatewayAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<VpsGatewayDescriptor>(NotConfiguredError));

    public Task<Result<int>> CheckGatewayLatencyAsync(VpsGatewayDescriptor gateway, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<int>(NotConfiguredError));
}

public sealed class UnconfiguredNatTraversalProbe : INatTraversalProbe
{
    public Task<Result<NatMappingResult>> ProbeNatAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<NatMappingResult>(NexusError.Validation(
            "NAT traversal probe provider is not configured in this deployment phase.",
            code: "NAT_PROBE_NOT_CONFIGURED")));
}

public sealed class UnconfiguredStunTurnClient : IStunTurnClient
{
    public Task<Result<IReadOnlyList<IceCandidateDescriptor>>> GatherCandidatesAsync(
        IReadOnlyList<IceServerConfiguration> servers,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<IceCandidateDescriptor>>(NexusError.Validation(
            "STUN/TURN client provider is not configured in this deployment phase.",
            code: "STUN_TURN_NOT_CONFIGURED")));
}

