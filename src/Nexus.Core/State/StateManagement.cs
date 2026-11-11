using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;

namespace Nexus.Core.State;

public sealed record StateChangedEventArgs<TState>(TState PreviousState, TState NewState, DateTimeOffset ChangedAtUtc);

public interface IStateManager<TState>
{
    TState CurrentState { get; }
    event EventHandler<StateChangedEventArgs<TState>>? StateChanged;
    void Update(Func<TState, TState> reducer);
    void Set(TState nextState);
}

/// <summary>
/// Thread-safe state container supporting atomic transformations and change notifications.
/// </summary>
public sealed class InMemoryStateManager<TState> : IStateManager<TState>
{
    private readonly Lock _sync = new();
    private TState _currentState;

    public InMemoryStateManager(TState initialState)
    {
        _currentState = initialState;
    }

    public TState CurrentState
    {
        get
        {
            lock (_sync)
            {
                return _currentState;
            }
        }
    }

    public event EventHandler<StateChangedEventArgs<TState>>? StateChanged;

    public void Update(Func<TState, TState> reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);

        TState previous;
        TState next;

        lock (_sync)
        {
            previous = _currentState;
            next = reducer(previous);
            if (EqualityComparer<TState>.Default.Equals(previous, next))
            {
                return;
            }

            _currentState = next;
        }

        StateChanged?.Invoke(this, new StateChangedEventArgs<TState>(previous, next, DateTimeOffset.UtcNow));
    }

    public void Set(TState nextState) => Update(_ => nextState);
}

public sealed record NexusRuntimeState
{
    public DeviceInfo? LocalDevice { get; init; }
    public ConnectionState ConnectionState { get; init; } = ConnectionState.Disconnected;
    public string? ActiveEndpoint { get; init; }
    public int KnownDevicesCount { get; init; }
    public int UnreadNotificationsCount { get; init; }
    public bool IsStorageInitialized { get; init; }
    public string? LastDiagnosticMessage { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

