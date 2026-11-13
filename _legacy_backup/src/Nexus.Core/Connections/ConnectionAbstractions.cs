using Nexus.Contracts.Connections;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Core.Errors;

namespace Nexus.Core.Connections;

public interface IConnectionManager
{
    ConnectionStateSnapshot CurrentSnapshot { get; }
    Task<Result<ConnectionStateSnapshot>> TransitionAsync(
        ConnectionState targetState,
        TransportKind transport = TransportKind.Unknown,
        string? endpoint = null,
        Guid? remoteDeviceId = null,
        int? latencyMs = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<Result<ConnectionStateSnapshot>> ConnectAsync(
        string endpoint,
        TransportKind transport,
        Guid? remoteDeviceId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ConnectionStateSnapshot>> DisconnectAsync(
        string? reason = null,
        CancellationToken cancellationToken = default);
}

public interface ITransportChannel
{
    TransportKind Transport { get; }
    bool IsConnected { get; }
    Task<Result> OpenAsync(string endpoint, CancellationToken cancellationToken = default);
    Task<Result> CloseAsync(CancellationToken cancellationToken = default);
    Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(
        NexusRequest<TRequest> request,
        CancellationToken cancellationToken = default);
    Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}

public interface IHeartbeatService
{
    DateTimeOffset? LastHeartbeatUtc { get; }
    Task<Result<int>> SendHeartbeatAsync(CancellationToken cancellationToken = default);
}

