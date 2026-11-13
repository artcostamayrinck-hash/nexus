using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Identity;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Sync;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Logging;
using Nexus.Infrastructure.Persistence;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class SqliteInfrastructureTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "nexus-infra-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider BuildProvider(string? overrideRoot = null, bool enableFileLogging = true)
    {
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: opts =>
            {
                opts.RootDirectory = overrideRoot ?? _rootDir;
                opts.EnablePooling = false;
            },
            configureLogging: log =>
            {
                log.ApplicationName = "NEXUS.Tests";
                log.EnableConsole = false;
                log.EnableFile = enableFileLogging;
            });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SqliteRepositories_PersistDevicesSessionsNotificationsSettingsAndSyncCheckpoints()
    {
        await using var provider = BuildProvider();
        var initializer = provider.GetRequiredService<IStorageInitializer>();
        var initResult = await initializer.InitializeAsync();
        Assert.True(initResult.IsSuccess);

        // Idempotent second initialization
        Assert.True((await initializer.InitializeAsync()).IsSuccess);

        // Devices repository
        var deviceRepo = provider.GetRequiredService<IDeviceRepository>();
        var deviceId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var device = new DeviceInfo
        {
            Id = deviceId,
            Name = "Galaxy S25",
            Platform = DevicePlatform.Android,
            Type = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "0.2.0",
            Capabilities = DeviceCapability.Notifications | DeviceCapability.FileTransfer,
            Status = DeviceStatus.Online,
            RegisteredAtUtc = now,
            LastSeenAtUtc = now
        };
        Assert.True((await deviceRepo.UpsertAsync(device)).IsSuccess);
        var loadedDevice = await deviceRepo.GetByIdAsync(deviceId);
        Assert.True(loadedDevice.IsSuccess);
        Assert.Equal("Galaxy S25", loadedDevice.Value?.Name);

        var androidDevices = await deviceRepo.ListByPlatformAsync(DevicePlatform.Android);
        Assert.Single(androidDevices.Value);

        // Sessions repository
        var sessionRepo = provider.GetRequiredService<ISessionRepository>();
        var sessionId = Guid.CreateVersion7();
        var session = new SessionInfo
        {
            SessionId = sessionId,
            UserId = Guid.CreateVersion7(),
            DeviceId = deviceId,
            Status = SessionStatus.Active,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddHours(8),
            ClientAddress = "192.168.1.42"
        };
        Assert.True((await sessionRepo.UpsertAsync(session)).IsSuccess);
        Assert.Single((await sessionRepo.ListActiveByDeviceAsync(deviceId)).Value);
        Assert.True((await sessionRepo.RevokeAsync(sessionId)).IsSuccess);
        Assert.Empty((await sessionRepo.ListActiveByDeviceAsync(deviceId)).Value);

        // Notifications repository
        var notifRepo = provider.GetRequiredService<INotificationRepository>();
        var notifId = Guid.CreateVersion7();
        var notification = new DeviceNotification
        {
            NotificationId = notifId,
            SourceDeviceId = deviceId,
            AppName = "NEXUS",
            Title = "Teste",
            Body = "Persistência SQLite validada",
            Actions = [new NotificationAction("ack", "Confirmar")]
        };
        Assert.True((await notifRepo.UpsertAsync(notification)).IsSuccess);
        Assert.Single((await notifRepo.ListPendingAsync()).Value);
        Assert.True((await notifRepo.MarkDismissedAsync(notifId)).IsSuccess);
        Assert.Empty((await notifRepo.ListPendingAsync()).Value);

        // Sync checkpoints repository
        var syncRepo = provider.GetRequiredService<ISyncStateRepository>();
        var batch = new SyncBatchContract
        {
            SourceDeviceId = deviceId,
            TargetDeviceId = Guid.CreateVersion7(),
            Scope = "notifications",
            BaseRevision = 0,
            TargetRevision = 7,
            Status = SyncStatus.Completed
        };
        Assert.True((await syncRepo.SaveCheckpointAsync(batch)).IsSuccess);
        var revision = await syncRepo.GetRevisionAsync("notifications", batch.TargetDeviceId);
        Assert.True(revision.IsSuccess);
        Assert.Equal(7L, revision.Value);

        // Settings store
        var settings = provider.GetRequiredService<ISettingsStore>();
        Assert.True((await settings.SetAsync("ui.theme", "NavyDark")).IsSuccess);
        var theme = await settings.GetAsync<string>("ui.theme");
        Assert.Equal("NavyDark", theme.Value);
        Assert.True((await settings.RemoveAsync("ui.theme")).Value);

        // Logging sink captured structured logs
        var logger = provider.GetRequiredService<ILogger<SqliteInfrastructureTests>>();
        logger.LogInformation("Structured test entry");
        var sink = provider.GetRequiredService<InMemoryLogSink>();
        Assert.Contains(sink.GetRecent(), e => e.Message.Contains("Structured test entry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalFileStorage_WritesReadsListsAndBlocksPathTraversal()
    {
        await using var provider = BuildProvider();
        var files = provider.GetRequiredService<IFileStorageService>();

        var payload = Encoding.UTF8.GetBytes("NEXUS internal artifact");
        var write = await files.WriteBytesAsync("cache", "diagnostics/report.txt", payload);
        Assert.True(write.IsSuccess);
        Assert.True((await files.ExistsAsync("cache", "diagnostics/report.txt")).Value);

        var read = await files.ReadBytesAsync("cache", "diagnostics/report.txt");
        Assert.True(read.IsSuccess);
        Assert.Equal(payload, read.Value);

        var listed = await files.ListFilesAsync("cache");
        Assert.Contains(listed.Value, f => f.EndsWith("report.txt", StringComparison.OrdinalIgnoreCase));

        var traversal = await files.WriteBytesAsync("cache", "../../outside.txt", payload);
        Assert.True(traversal.IsFailure);
        Assert.Equal(ErrorCategory.Validation, traversal.Error!.Category);

        Assert.True((await files.DeleteAsync("cache", "diagnostics/report.txt")).Value);
    }

    [Fact]
    public async Task StorageFailureHandler_HandlesInvalidDatabaseLocationWithoutThrowing()
    {
        // Point database root to a file instead of a directory to force a storage failure cleanly
        Directory.CreateDirectory(_rootDir);
        var blockingFile = Path.Combine(_rootDir, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "lock");

        await using var provider = BuildProvider(blockingFile, enableFileLogging: false);
        var initializer = provider.GetRequiredService<IStorageInitializer>();

        var result = await initializer.InitializeAsync();
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
            {
                Directory.Delete(_rootDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock in test teardown
        }
    }
}

