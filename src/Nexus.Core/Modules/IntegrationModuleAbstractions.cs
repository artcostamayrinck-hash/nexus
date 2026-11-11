using Nexus.Contracts.Modules;
using Nexus.Core.Errors;

namespace Nexus.Core.Modules;

public interface IModuleSettingsService
{
    event EventHandler<IntegrationModuleKind>? ModuleSettingsChanged;

    Task<Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>> GetModuleStatusesAsync(CancellationToken cancellationToken = default);
    Task<Result<IntegrationModuleStatusSnapshot>> SetModuleEnabledAsync(UpdateModuleToggleRequest request, CancellationToken cancellationToken = default);

    Task<Result<ControlModuleSettings>> GetControlSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<ControlModuleSettings>> SaveControlSettingsAsync(ControlModuleSettings settings, CancellationToken cancellationToken = default);

    Task<Result<NotifyModuleSettings>> GetNotifySettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<NotifyModuleSettings>> SaveNotifySettingsAsync(NotifyModuleSettings settings, CancellationToken cancellationToken = default);

    Task<Result<MonitorModuleSettings>> GetMonitorSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<MonitorModuleSettings>> SaveMonitorSettingsAsync(MonitorModuleSettings settings, CancellationToken cancellationToken = default);

    Task<Result<MediaModuleSettings>> GetMediaSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<MediaModuleSettings>> SaveMediaSettingsAsync(MediaModuleSettings settings, CancellationToken cancellationToken = default);
}

public interface IWindowsControlExecutor
{
    bool IsSupportedOnCurrentOs { get; }
    Task<Result<RemoteControlExecutionReceipt>> ExecuteAsync(
        RemoteControlCommandRequest request,
        CancellationToken cancellationToken = default);
}

public interface IHostMonitorService
{
    Task<Result<HostTelemetrySnapshot>> CollectSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IWindowsMediaService
{
    bool IsSupportedOnCurrentOs { get; }
    Task<Result<MediaPlaybackStateContract>> GetCurrentStateAsync(CancellationToken cancellationToken = default);
    Task<Result<MediaPlaybackStateContract>> ExecuteCommandAsync(
        MediaControlCommandRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMobileNotificationFeedRepository
{
    Task<Result> SaveAsync(MobileNotificationRecord record, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<MobileNotificationRecord>>> ListRecentAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<Result> MarkDismissedAsync(Guid notificationId, CancellationToken cancellationToken = default);
    Task<Result<int>> ClearAsync(CancellationToken cancellationToken = default);
}

public interface INexusNotifyService
{
    event EventHandler<MobileNotificationRecord>? NotificationReceived;

    Task<Result<PushMobileNotificationReceipt>> IngestAsync(
        PushMobileNotificationRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<MobileNotificationRecord>>> ListHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<Result> DismissAsync(Guid notificationId, CancellationToken cancellationToken = default);
    Task<Result<int>> ClearHistoryAsync(CancellationToken cancellationToken = default);
}

public interface IIntegrationModulesClient
{
    Task<Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>> GetRemoteModuleStatusesAsync(CancellationToken cancellationToken = default);
    Task<Result<IntegrationModuleStatusSnapshot>> SetRemoteModuleEnabledAsync(UpdateModuleToggleRequest request, CancellationToken cancellationToken = default);

    Task<Result<RemoteControlExecutionReceipt>> SendControlCommandAsync(RemoteControlCommandRequest request, CancellationToken cancellationToken = default);
    Task<Result<ControlModuleSettings>> GetRemoteControlSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<ControlModuleSettings>> SaveRemoteControlSettingsAsync(ControlModuleSettings settings, CancellationToken cancellationToken = default);

    Task<Result<PushMobileNotificationReceipt>> PushMobileNotificationAsync(PushMobileNotificationRequest request, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<MobileNotificationRecord>>> GetRemoteNotificationHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<Result> DismissRemoteNotificationAsync(Guid notificationId, CancellationToken cancellationToken = default);

    Task<Result<HostTelemetrySnapshot>> GetRemoteMonitorSnapshotAsync(CancellationToken cancellationToken = default);
    Task<Result<MediaPlaybackStateContract>> GetRemoteMediaStateAsync(CancellationToken cancellationToken = default);
    Task<Result<MediaPlaybackStateContract>> SendMediaCommandAsync(MediaControlCommandRequest request, CancellationToken cancellationToken = default);
}

