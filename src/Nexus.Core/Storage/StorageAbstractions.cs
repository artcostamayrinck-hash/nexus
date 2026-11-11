using Nexus.Contracts.Devices;
using Nexus.Contracts.Identity;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Sync;
using Nexus.Core.Errors;

namespace Nexus.Core.Storage;

public sealed record StorageStats(
    string RootDirectory,
    long DatabaseSizeBytes,
    int ManagedFileCount,
    long ManagedFilesSizeBytes,
    bool IsAvailable);

public interface IStorageInitializer
{
    Task<Result> InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IRepository<TEntity, in TKey>
{
    Task<Result<TEntity?>> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TEntity>>> ListAsync(CancellationToken cancellationToken = default);
    Task<Result> UpsertAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task<Result<bool>> DeleteAsync(TKey id, CancellationToken cancellationToken = default);
}

public interface IDeviceRepository : IRepository<DeviceInfo, Guid>
{
    Task<Result<IReadOnlyList<DeviceInfo>>> ListByPlatformAsync(DevicePlatform platform, CancellationToken cancellationToken = default);
    Task<Result> UpdateStatusAsync(Guid deviceId, DeviceStatus status, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default);
}

public interface ISessionRepository : IRepository<SessionInfo, Guid>
{
    Task<Result<IReadOnlyList<SessionInfo>>> ListActiveByDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<Result> RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface INotificationRepository : IRepository<DeviceNotification, Guid>
{
    Task<Result<IReadOnlyList<DeviceNotification>>> ListPendingAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<Result> MarkDismissedAsync(Guid notificationId, CancellationToken cancellationToken = default);
}

public interface ISyncStateRepository
{
    Task<Result<long>> GetRevisionAsync(string scope, Guid targetDeviceId, CancellationToken cancellationToken = default);
    Task<Result> SaveCheckpointAsync(SyncBatchContract batch, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<Result<T?>> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task<Result<T>> GetOrDefaultAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default);
    Task<Result> SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
    Task<Result<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyDictionary<string, string>>> GetAllRawAsync(CancellationToken cancellationToken = default);
}

public interface IFileStorageService
{
    string RootDirectory { get; }
    Result<string> ResolveSafePath(string bucket, string relativePath);
    Task<Result<string>> WriteBytesAsync(string bucket, string relativePath, byte[] content, CancellationToken cancellationToken = default);
    Task<Result<byte[]>> ReadBytesAsync(string bucket, string relativePath, CancellationToken cancellationToken = default);
    Task<Result<bool>> ExistsAsync(string bucket, string relativePath, CancellationToken cancellationToken = default);
    Task<Result<bool>> DeleteAsync(string bucket, string relativePath, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> ListFilesAsync(string bucket, CancellationToken cancellationToken = default);
    Task<Result<StorageStats>> GetStatsAsync(CancellationToken cancellationToken = default);
}

