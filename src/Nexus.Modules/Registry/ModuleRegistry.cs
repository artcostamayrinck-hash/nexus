using System.Collections.Concurrent;
using Nexus.Core.Errors;
using Nexus.Modules.Abstractions;

namespace Nexus.Modules.Registry;

public interface IModuleRegistry
{
    IReadOnlyList<ModuleDescriptor> GetDescriptors();
    Result Register(INexusModule module);
    Task<Result> InitializeAllAsync(IServiceProvider services, CancellationToken cancellationToken = default);
    Task<Result> ShutdownAllAsync(CancellationToken cancellationToken = default);
}

public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly ConcurrentDictionary<string, ModuleEntry> _modules = new(StringComparer.OrdinalIgnoreCase);

    public ModuleRegistry(IEnumerable<INexusModule>? initialModules = null)
    {
        if (initialModules is null)
        {
            return;
        }

        foreach (var module in initialModules)
        {
            Register(module);
        }
    }

    public IReadOnlyList<ModuleDescriptor> GetDescriptors()
    {
        return _modules.Values
            .OrderBy(e => e.Module.ModuleId, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ModuleDescriptor(
                e.Module.ModuleId,
                e.Module.DisplayName,
                e.Module.Version,
                e.Module.Description,
                e.Module.RequiredCapabilities,
                e.State,
                e.LastError))
            .ToArray();
    }

    public Result Register(INexusModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.ModuleId))
        {
            return Result.Failure(NexusError.Validation("ModuleId cannot be empty.", code: "MODULE_ID_EMPTY"));
        }

        var entry = new ModuleEntry(module);
        return _modules.TryAdd(module.ModuleId, entry)
            ? Result.Success()
            : Result.Failure(NexusError.Conflict($"Module '{module.ModuleId}' is already registered.", "MODULE_DUPLICATE"));
    }

    public async Task<Result> InitializeAllAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        foreach (var entry in _modules.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var init = await entry.Module.InitializeAsync(services, cancellationToken).ConfigureAwait(false);
                if (init.IsSuccess)
                {
                    entry.State = ModuleLifecycleState.Active;
                    entry.LastError = null;
                }
                else
                {
                    entry.State = ModuleLifecycleState.Faulted;
                    entry.LastError = init.Error?.Message;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entry.State = ModuleLifecycleState.Faulted;
                entry.LastError = ex.Message;
            }
        }

        return Result.Success();
    }

    public async Task<Result> ShutdownAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in _modules.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await entry.Module.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                entry.State = ModuleLifecycleState.Stopped;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entry.State = ModuleLifecycleState.Faulted;
                entry.LastError = ex.Message;
            }
        }

        return Result.Success();
    }

    private sealed class ModuleEntry(INexusModule module)
    {
        public INexusModule Module { get; } = module;
        public ModuleLifecycleState State { get; set; } = ModuleLifecycleState.Registered;
        public string? LastError { get; set; }
    }
}

