using Nexus.Contracts.Connections;

namespace Nexus.Core.Events;

public sealed record RouteSelectedDomainEvent(ConnectivityStatusReport Report) : DomainEventBase;

public sealed record EndpointDiscoveredDomainEvent(MdnsDiscoveredEndpoint Endpoint) : DomainEventBase;

public sealed record VpnStatusChangedDomainEvent(
    VpnStatus PreviousStatus,
    VpnStatus NewStatus,
    string? Detail = null) : DomainEventBase;

public sealed record ConnectivityHandoverDeferredDomainEvent(
    string? CurrentEndpoint,
    string CandidateEndpoint,
    int ActiveOperationsCount) : DomainEventBase;

