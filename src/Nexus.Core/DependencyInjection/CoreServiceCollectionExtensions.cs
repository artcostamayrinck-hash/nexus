using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Contracts.Devices;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;

namespace Nexus.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddNexusCore(
        this IServiceCollection services,
        Action<LocalDeviceOptions>? configureLocalDevice = null)
    {
        var deviceOptions = new LocalDeviceOptions();
        configureLocalDevice?.Invoke(deviceOptions);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<IJsonSerializer, SystemTextJsonSerializer>();
        services.TryAddSingleton<IDeviceCapabilityProvider, DefaultDeviceCapabilityProvider>();
        services.TryAddSingleton<IEventBus>(_ => new InProcessEventBus());

        services.TryAddSingleton<ILocalDeviceContext>(sp =>
        {
            var caps = sp.GetRequiredService<IDeviceCapabilityProvider>().GetLocalCapabilities();
            return new DefaultLocalDeviceContext(
                deviceOptions.DeviceId,
                deviceOptions.DeviceName,
                deviceOptions.Platform,
                deviceOptions.DeviceType,
                deviceOptions.OperatingSystemVersion,
                deviceOptions.AppVersion,
                deviceOptions.Capabilities ?? caps);
        });

        services.TryAddSingleton<IStateManager<NexusRuntimeState>>(sp =>
        {
            var local = sp.GetRequiredService<ILocalDeviceContext>();
            return new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState
            {
                LocalDevice = local.ToDeviceInfo(),
                UpdatedAtUtc = sp.GetRequiredService<IClock>().UtcNow
            });
        });

        services.TryAddSingleton<IConnectionManager, ConnectionStateService>();
        services.TryAddTransient<IMessageOrderingPipeline, RealtimeMessagePipeline>();
        services.TryAddScoped<IDeviceRegistry, DeviceCoordinationService>();
        services.TryAddScoped<DiagnosticHealthService>();

        // Future NAT/relay abstractions (Phase 04 extension points)
        services.TryAddSingleton<ISignalingChannel, UnconfiguredSignalingChannel>();
        services.TryAddSingleton<IRelayTransport, UnconfiguredRelayTransport>();
        services.TryAddSingleton<IVpsGatewayProvider, UnconfiguredVpsGatewayProvider>();
        services.TryAddSingleton<INatTraversalProbe, UnconfiguredNatTraversalProbe>();
        services.TryAddSingleton<IStunTurnClient, UnconfiguredStunTurnClient>();

        return services;
    }
}

public sealed class LocalDeviceOptions
{
    public Guid DeviceId { get; set; } = Guid.CreateVersion7();
    public string DeviceName { get; set; } = Environment.MachineName;
    public DevicePlatform Platform { get; set; } = OperatingSystem.IsWindows()
        ? DevicePlatform.Windows
        : OperatingSystem.IsAndroid() ? DevicePlatform.Android : DevicePlatform.Linux;
    public DeviceType DeviceType { get; set; } = OperatingSystem.IsAndroid() ? DeviceType.Phone : DeviceType.Desktop;
    public string OperatingSystemVersion { get; set; } = Environment.OSVersion.VersionString;
    public string AppVersion { get; set; } = typeof(LocalDeviceOptions).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    public DeviceCapability? Capabilities { get; set; }
}

