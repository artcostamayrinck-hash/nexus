using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Connections;

public enum ConnectionState
{
    Disconnected = 0,
    Discovering = 1,
    Connecting = 2,
    Connected = 3,
    Reconnecting = 4,
    Degraded = 5,
    Faulted = 6,
    Authenticating = 7
}

public enum TransportKind
{
    Unknown = 0,
    LocalNetwork = 1,
    SignalR = 2,
    WebSocket = 3,
    Bluetooth = 4,
    UsbTether = 5
}

public sealed record ConnectionStateSnapshot : IVersionedContract, IValidatableContract
{
    public Guid ConnectionId { get; init; } = Guid.NewGuid();
    public Guid LocalDeviceId { get; init; }
    public Guid? RemoteDeviceId { get; init; }
    public ConnectionState State { get; init; } = ConnectionState.Disconnected;
    public TransportKind Transport { get; init; } = TransportKind.Unknown;

    [StringLength(256)]
    public string? Endpoint { get; init; }

    [Range(0, 600000)]
    public int? LatencyMs { get; init; }

    [StringLength(512)]
    public string? Reason { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (ConnectionId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(ConnectionId), "CONNECTION_ID_EMPTY", "ConnectionId cannot be empty.");
        }

        if (LocalDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(LocalDeviceId), "LOCAL_DEVICE_ID_EMPTY", "LocalDeviceId cannot be empty.");
        }

        if (State == ConnectionState.Connected && Transport == TransportKind.Unknown)
        {
            yield return new ContractValidationError(nameof(Transport), "CONNECTED_TRANSPORT_UNKNOWN", "Connected state requires a known TransportKind.");
        }
    }
}

