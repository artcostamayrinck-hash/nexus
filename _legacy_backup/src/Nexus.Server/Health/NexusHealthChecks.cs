using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Persistence;
using Nexus.Modules.Registry;

namespace Nexus.Server.Health;

public sealed class SqliteStorageHealthCheck : IHealthCheck
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IFileStorageService _fileStorage;

    public SqliteStorageHealthCheck(
        ISqliteConnectionFactory connectionFactory,
        IFileStorageService fileStorage)
    {
        _connectionFactory = connectionFactory;
        _fileStorage = fileStorage;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var stats = await _fileStorage.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["databasePath"] = _connectionFactory.DatabasePath,
                ["storageAvailable"] = stats.IsSuccess && stats.Value.IsAvailable
            };

            return HealthCheckResult.Healthy("SQLite persistence and internal storage are ready.", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQLite storage check failed.", ex);
        }
    }
}

public sealed class NexusRuntimeHealthCheck : IHealthCheck
{
    private readonly IStateManager<NexusRuntimeState> _stateManager;
    private readonly IModuleRegistry _moduleRegistry;

    public NexusRuntimeHealthCheck(
        IStateManager<NexusRuntimeState> stateManager,
        IModuleRegistry moduleRegistry)
    {
        _stateManager = stateManager;
        _moduleRegistry = moduleRegistry;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _stateManager.CurrentState;
        var modules = _moduleRegistry.GetDescriptors();

        var data = new Dictionary<string, object>
        {
            ["storageInitialized"] = state.IsStorageInitialized,
            ["registeredModules"] = modules.Count,
            ["knownDevices"] = state.KnownDevicesCount
        };

        var result = state.IsStorageInitialized
            ? HealthCheckResult.Healthy("NEXUS runtime state is ready.", data)
            : HealthCheckResult.Degraded("NEXUS storage initialization pending.", data: data);

        return Task.FromResult(result);
    }
}

