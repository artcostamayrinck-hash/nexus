using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nexus.Core.DependencyInjection;
using Nexus.Core.Pairing;
using Nexus.Core.Storage;
using Nexus.Core.Connections;
using Nexus.Core.Sync;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Files;
using Nexus.Infrastructure.Logging;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Settings;
using Nexus.Infrastructure.Sync;
using Nexus.Security.DependencyInjection;
using Serilog;

namespace Nexus.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddNexusInfrastructure(
        this IServiceCollection services,
        Action<NexusStorageOptions>? configureStorage = null,
        Action<NexusLoggingOptions>? configureLogging = null,
        Action<LocalDeviceOptions>? configureLocalDevice = null,
        bool registerLoggingProvider = true)
    {
        var storageOptions = new NexusStorageOptions();
        configureStorage?.Invoke(storageOptions);

        var loggingOptions = new NexusLoggingOptions
        {
            LogsDirectory = Path.Combine(storageOptions.RootDirectory, "logs")
        };
        configureLogging?.Invoke(loggingOptions);

        services.TryAddSingleton(storageOptions);
        services.TryAddSingleton(loggingOptions);
        services.TryAddSingleton(sp => new Nexus.Security.Credentials.SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(sp.GetRequiredService<NexusStorageOptions>().RootDirectory, "vault")
        });

        services.AddNexusCore(configureLocalDevice);
        services.AddNexusSecurity();

        var memorySink = new InMemoryLogSink();
        services.TryAddSingleton(memorySink);

        if (registerLoggingProvider)
        {
            var serilogLogger = NexusLoggingBuilder.CreateLogger(loggingOptions, memorySink);
            services.TryAddSingleton<Serilog.ILogger>(serilogLogger);
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(serilogLogger, dispose: true);
            });
        }

        services.TryAddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.TryAddSingleton<StorageFailureHandler>();
        services.TryAddSingleton<IStorageInitializer, SqliteStorageInitializer>();
        services.TryAddSingleton<IFileStorageService, LocalFileStorageService>();
        services.TryAddSingleton<ISettingsStore, SqliteSettingsStore>();

        services.TryAddScoped<IDeviceRepository, SqliteDeviceRepository>();
        services.TryAddScoped<ISessionRepository, SqliteSessionRepository>();
        services.TryAddScoped<INotificationRepository, SqliteNotificationRepository>();
        services.TryAddScoped<ISyncStateRepository, SqliteSyncStateRepository>();
        services.TryAddScoped<ITrustedDeviceRepository, SqliteTrustedDeviceRepository>();
        services.TryAddScoped<IPairingSessionRepository, SqlitePairingSessionRepository>();
        services.TryAddScoped<IRefreshTokenRepository, SqliteRefreshTokenRepository>();
        services.TryAddScoped<IPairingService, PairingCoordinatorService>();
        services.TryAddScoped<ITrustedDeviceService, TrustedDeviceManagementService>();
        services.TryAddSingleton(_ => new Security.LocalHostTokenBridgeOptions { Enabled = false });
        services.TryAddSingleton<Security.ILocalHostTokenBridge, Security.LocalHostTokenBridge>();
        services.TryAddSingleton<Security.NexusPairingProtocolClient>();

        services.TryAddSingleton<ConnectivityOptions>();
        services.TryAddSingleton<IEndpointRegistryRepository, SqliteEndpointRepository>();
        services.TryAddSingleton<INetworkConnectivityAdapter, SystemNetworkConnectivityAdapter>();
        services.TryAddSingleton<INetworkEnvironmentInspector, NetworkEnvironmentInspector>();
        services.TryAddSingleton<IMdnsPacketTransport, UdpMdnsPacketTransport>();
        services.TryAddSingleton<IMdnsServiceDiscovery, MdnsServiceDiscovery>();
        services.TryAddSingleton<IRouteProber, HttpRouteProber>();
        services.TryAddSingleton<INexusRouteOrchestrator, NexusRouteOrchestrator>();

        services.TryAddSingleton<RealtimeClientOptions>();
        services.TryAddSingleton<IRealtimeCredentialProvider, HostBridgeRealtimeCredentialProvider>();
        services.TryAddSingleton<IRealtimeClientChannel, SignalRRealtimeClient>();
        services.TryAddSingleton<IHttpBulkTransferChannel, HttpBulkTransferClient>();

        services.TryAddSingleton<IClipboardRepository, SqliteClipboardRepository>();
        services.TryAddSingleton<INotesRepository, SqliteNotesRepository>();
        services.TryAddSingleton<ISyncQueueRepository, SqliteSyncQueueRepository>();
        services.TryAddSingleton<ISyncPrivacySettingsService, SyncPrivacySettingsService>();
        services.TryAddSingleton<ISystemClipboardAdapter, DefaultSystemClipboardAdapter>();
        services.TryAddSingleton<INexusSyncTransport, ServerBasicSyncTransport>();
        services.TryAddSingleton<IClipboardSyncService, ClipboardSyncCoordinator>();
        services.TryAddSingleton<INotesSyncService, NotesSyncCoordinator>();
        services.TryAddSingleton<ISyncOrchestrator, BasicSyncOrchestrator>();

        return services;
    }
}

