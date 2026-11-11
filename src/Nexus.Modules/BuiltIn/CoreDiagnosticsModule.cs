using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Contracts.Devices;
using Nexus.Core.Errors;
using Nexus.Modules.Abstractions;
using Nexus.Modules.Registry;

namespace Nexus.Modules.BuiltIn;

public sealed class CoreDiagnosticsModule : INexusModule
{
    public string ModuleId => "nexus.core.diagnostics";
    public string DisplayName => "Core Diagnostics";
    public string Version => "1.0.0";
    public string Description => "Provides runtime health, device status, and storage readiness inspection.";
    public DeviceCapability RequiredCapabilities => DeviceCapability.Telemetry;

    public Task<Result> InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success());

    public Task<Result> ShutdownAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success());
}

public static class ModulesServiceCollectionExtensions
{
    public static IServiceCollection AddNexusModules(this IServiceCollection services)
    {
        services.AddSingleton<INexusModule, CoreDiagnosticsModule>();
        services.TryAddSingleton<IModuleRegistry>(sp =>
        {
            var modules = sp.GetServices<INexusModule>();
            return new ModuleRegistry(modules);
        });
        return services;
    }
}

