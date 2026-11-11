using Nexus.Contracts.Devices;
using Nexus.Core.Errors;

namespace Nexus.Modules.Abstractions;

public enum ModuleLifecycleState
{
    Registered = 0,
    Initialized = 1,
    Active = 2,
    Faulted = 3,
    Stopped = 4
}

public sealed record ModuleDescriptor(
    string ModuleId,
    string DisplayName,
    string Version,
    string Description,
    DeviceCapability RequiredCapabilities,
    ModuleLifecycleState State,
    string? LastError = null);

public interface INexusModule
{
    string ModuleId { get; }
    string DisplayName { get; }
    string Version { get; }
    string Description { get; }
    DeviceCapability RequiredCapabilities { get; }

    Task<Result> InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default);
    Task<Result> ShutdownAsync(CancellationToken cancellationToken = default);
}

