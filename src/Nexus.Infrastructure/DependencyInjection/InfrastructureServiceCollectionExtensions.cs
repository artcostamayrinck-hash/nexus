using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Transfers;
using Nexus.Core.DependencyInjection;
using Nexus.Core.Pairing;
using Nexus.Core.Storage;
using Nexus.Core.Connections;
using Nexus.Core.Automation;
using Nexus.Core.Modules;
using Nexus.Core.Sync;
using Nexus.Core.Transfers;
using Nexus.Core.Updates;
using Nexus.Infrastructure.Automation;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Files;
using Nexus.Infrastructure.Logging;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Settings;
using Nexus.Infrastructure.Sync;
using Nexus.Infrastructure.Transfers;
using Nexus.Infrastructure.Updates;
using Nexus.Security.DependencyInjection;
using Nexus.Security.Updates;
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

        services.TryAddSingleton(_ => new TransferQueueOptions());
        services.TryAddSingleton<TransferStoragePolicyOptions>();
        services.TryAddSingleton<ITransferRepository, SqliteTransferRepository>();
        services.TryAddSingleton<ITransferStorageProvider, SafeTransferStorageProvider>();
        services.TryAddSingleton<IFileTransferTransport, HttpTransferTransport>();
        services.TryAddSingleton<ITransferManager, TransferManagerService>();

        services.TryAddSingleton(_ => new Modules.WindowsControlOptions());
        services.TryAddSingleton<IModuleSettingsService, Modules.ModuleSettingsService>();
        services.TryAddSingleton<IMobileNotificationFeedRepository, Repositories.SqliteMobileNotificationFeedRepository>();
        services.TryAddSingleton<INexusNotifyService, Modules.NexusNotifyService>();
        services.TryAddSingleton<IWindowsControlExecutor, Modules.WindowsControlExecutor>();
        services.TryAddSingleton<IHostMonitorService, Modules.HostMonitorService>();
        services.TryAddSingleton<IWindowsMediaService, Modules.WindowsMediaService>();
        services.TryAddSingleton<IIntegrationModulesClient, Modules.HttpIntegrationModulesClient>();

        services.TryAddSingleton<IDeviceBatteryProvider, WindowsDeviceBatteryProvider>();
        services.TryAddSingleton<IApprovedAppLauncher, WindowsApprovedAppLauncher>();
        services.TryAddSingleton<IPanelProfileRepository, SqlitePanelProfileRepository>();
        services.TryAddSingleton<IAutomationRuleRepository, SqliteAutomationRuleRepository>();
        services.TryAddSingleton<IAutomationExecutionRepository, SqliteAutomationExecutionRepository>();
        services.TryAddSingleton<IPanelLayoutService, PanelLayoutService>();
        services.TryAddSingleton<IAutomationEngine, AutomationEngineService>();
        services.TryAddSingleton<IPanelAndAutomationClient, HttpPanelAndAutomationClient>();

        services.TryAddSingleton(_ => new NexusUpdateOptions());
        services.TryAddSingleton<IApkArchiveReader, ManagedApkArchiveReader>();
        services.TryAddSingleton<IApkPackageVerifier, ApkPackageVerifier>();
        services.TryAddSingleton<IAppPackageInspector, DefaultAppPackageInspector>();
        services.TryAddSingleton<IAndroidUpdateInstaller, UnsupportedPlatformUpdateInstaller>();
        services.TryAddSingleton<IUpdateHistoryRepository, SqliteUpdateHistoryRepository>();
        services.TryAddSingleton<IGitHubReleaseSource, GitHubReleaseClient>();
        services.TryAddSingleton<INexusUpdateService, NexusUpdateService>();

        return services;
    }
}

