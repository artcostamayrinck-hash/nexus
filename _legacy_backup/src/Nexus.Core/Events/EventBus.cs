using System.Collections.Concurrent;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;

namespace Nexus.Core.Events;

public interface IDomainEvent
{
    Guid EventId { get; }
    Guid CorrelationId { get; }
    DateTimeOffset OccurredAtUtc { get; }
}

public abstract record DomainEventBase : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public Guid CorrelationId { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DeviceRegisteredDomainEvent(DeviceInfo Device) : DomainEventBase;
public sealed record DeviceStatusChangedDomainEvent(Guid DeviceId, DeviceStatus PreviousStatus, DeviceStatus NewStatus) : DomainEventBase;
public sealed record ConnectionStateChangedDomainEvent(ConnectionStateSnapshot Snapshot) : DomainEventBase;
public sealed record DiagnosticDomainEvent(string Source, string Message, bool IsError = false) : DomainEventBase;
public sealed record PairingStateChangedDomainEvent(Guid PairingId, Nexus.Contracts.Pairing.PairingSessionState State, string? Detail = null) : DomainEventBase;
public sealed record DeviceTrustAuthorizedDomainEvent(Guid DeviceId, string CustomName, string PublicKeyFingerprint) : DomainEventBase;
public sealed record DeviceTrustRevokedDomainEvent(Guid DeviceId, string? Reason) : DomainEventBase;
public sealed record DeviceRenamedDomainEvent(Guid DeviceId, string NewName) : DomainEventBase;

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent;

    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : class, IDomainEvent;
}

/// <summary>
/// Thread-safe in-process asynchronous event bus with isolated handler dispatch.
/// </summary>
public sealed class InProcessEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Func<IDomainEvent, CancellationToken, Task>>> _handlers = new();
    private readonly Action<Exception, Type>? _onHandlerError;

    public InProcessEventBus(Action<Exception, Type>? onHandlerError = null)
    {
        _onHandlerError = onHandlerError;
    }

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_handlers.TryGetValue(typeof(TEvent), out var subscribers) || subscribers.IsEmpty)
        {
            return;
        }

        var snapshot = subscribers.Values.ToArray();
        foreach (var handler in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await handler(domainEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _onHandlerError?.Invoke(ex, typeof(TEvent));
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscriptionId = Guid.NewGuid();
        var map = _handlers.GetOrAdd(typeof(TEvent), _ => new ConcurrentDictionary<Guid, Func<IDomainEvent, CancellationToken, Task>>());
        map[subscriptionId] = (evt, ct) => handler((TEvent)evt, ct);

        return new Subscription(() =>
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var existing))
            {
                existing.TryRemove(subscriptionId, out _);
            }
        });
    }

    private sealed class Subscription(Action disposeAction) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                disposeAction();
            }
        }
    }
}

