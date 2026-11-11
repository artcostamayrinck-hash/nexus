using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Modules.Abstractions;
using Nexus.Modules.BuiltIn;
using Nexus.Modules.Registry;
using Xunit;

namespace Nexus.UnitTests;

public sealed class CoreServicesTests
{
    [Fact]
    public void Result_MapBindMatch_BehavePredictably()
    {
        var ok = Result.Success(21)
            .Map(v => v * 2)
            .Bind(v => Result.Success(v.ToString()));

        Assert.True(ok.IsSuccess);
        Assert.Equal("42", ok.Value);

        var err = Result.Failure<int>(NexusError.NotFound("Missing"))
            .Map(v => v + 1)
            .Match(_ => "ok", e => e.Code);

        Assert.Equal("NOT_FOUND", err);
    }

    [Fact]
    public async Task EventBus_PublishesToSubscribers_AndIsolatesFaultyHandler()
    {
        var bus = new InProcessEventBus();
        var delivered = 0;

        using var badSub = bus.Subscribe<DiagnosticDomainEvent>((_, _) => throw new InvalidOperationException("Boom"));
        using var goodSub = bus.Subscribe<DiagnosticDomainEvent>((_, _) =>
        {
            delivered++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new DiagnosticDomainEvent("Test", "Ping"));
        Assert.Equal(1, delivered);

        goodSub.Dispose();
        await bus.PublishAsync(new DiagnosticDomainEvent("Test", "Ping 2"));
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task DeviceCoordinationService_RegistersUpdatesAndPublishesEvents()
    {
        var repo = new InMemoryDeviceRepository();
        var bus = new InProcessEventBus();
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero));
        var localDevice = new DefaultLocalDeviceContext(
            Guid.CreateVersion7(), "TestHost", DevicePlatform.Windows, DeviceType.Desktop, "Win11", "0.2.0", DeviceCapability.Telemetry);
        var state = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState { LocalDevice = localDevice.ToDeviceInfo() });

        var events = new List<IDomainEvent>();
        using var s1 = bus.Subscribe<DeviceRegisteredDomainEvent>((e, _) => { events.Add(e); return Task.CompletedTask; });
        using var s2 = bus.Subscribe<DeviceStatusChangedDomainEvent>((e, _) => { events.Add(e); return Task.CompletedTask; });

        var service = new DeviceCoordinationService(repo, bus, clock, state);
        var reg = new DeviceRegistrationContract
        {
            DeviceId = Guid.CreateVersion7(),
            Name = "Pixel 9",
            Platform = DevicePlatform.Android,
            Type = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "0.2.0",
            Capabilities = DeviceCapability.Notifications
        };

        var created = await service.RegisterOrUpdateAsync(reg);
        Assert.True(created.IsSuccess);
        Assert.Equal(1, state.CurrentState.KnownDevicesCount);
        Assert.Single(events.OfType<DeviceRegisteredDomainEvent>());

        var statusChange = await service.UpdateDeviceStatusAsync(reg.DeviceId, DeviceStatus.Busy);
        Assert.True(statusChange.IsSuccess);
        Assert.Single(events.OfType<DeviceStatusChangedDomainEvent>());
    }

    [Fact]
    public async Task ConnectionStateService_ConnectAndDisconnectTransitions()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var bus = new InProcessEventBus();
        var local = new DefaultLocalDeviceContext(Guid.CreateVersion7(), "Host", DevicePlatform.Windows, DeviceType.Desktop, "11", "0.2", DeviceCapability.None);
        var state = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState());

        var manager = new ConnectionStateService(local, clock, bus, state);

        var bad = await manager.ConnectAsync("   ", TransportKind.LocalNetwork);
        Assert.True(bad.IsFailure);
        Assert.Equal(ErrorCategory.Validation, bad.Error!.Category);

        var connected = await manager.ConnectAsync("http://127.0.0.1:5280", TransportKind.SignalR);
        Assert.True(connected.IsSuccess);
        Assert.Equal(ConnectionState.Connected, manager.CurrentSnapshot.State);
        Assert.Equal(ConnectionState.Connected, state.CurrentState.ConnectionState);

        var disconnected = await manager.DisconnectAsync();
        Assert.True(disconnected.IsSuccess);
        Assert.Equal(ConnectionState.Disconnected, manager.CurrentSnapshot.State);
    }

    [Fact]
    public async Task ModuleRegistry_InitializesAndPreventsDuplicates()
    {
        var registry = new ModuleRegistry();
        var module = new CoreDiagnosticsModule();

        Assert.True(registry.Register(module).IsSuccess);
        Assert.False(registry.Register(module).IsSuccess);

        await registry.InitializeAllAsync(new EmptyServiceProvider());
        var descriptor = Assert.Single(registry.GetDescriptors());
        Assert.Equal(ModuleLifecycleState.Active, descriptor.State);

        await registry.ShutdownAllAsync();
        Assert.Equal(ModuleLifecycleState.Stopped, registry.GetDescriptors().Single().State);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class InMemoryDeviceRepository : IDeviceRepository
    {
        private readonly Dictionary<Guid, DeviceInfo> _store = [];

        public Task<Result<DeviceInfo?>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<DeviceInfo?>.Success(_store.GetValueOrDefault(id)));

        public Task<Result<IReadOnlyList<DeviceInfo>>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<DeviceInfo>>.Success(_store.Values.ToArray()));

        public Task<Result<IReadOnlyList<DeviceInfo>>> ListByPlatformAsync(DevicePlatform platform, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<DeviceInfo>>.Success(_store.Values.Where(d => d.Platform == platform).ToArray()));

        public Task<Result> UpsertAsync(DeviceInfo entity, CancellationToken cancellationToken = default)
        {
            _store[entity.Id] = entity;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateStatusAsync(Guid deviceId, DeviceStatus status, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
        {
            if (_store.TryGetValue(deviceId, out var existing))
            {
                _store[deviceId] = existing with { Status = status, LastSeenAtUtc = seenAtUtc };
            }
            return Task.FromResult(Result.Success());
        }

        public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<bool>.Success(_store.Remove(id)));
    }
}

