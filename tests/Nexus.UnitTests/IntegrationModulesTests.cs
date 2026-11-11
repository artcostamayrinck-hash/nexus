using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Security;
using Nexus.Contracts.Sync;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Modules;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.Modules;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Settings;
using Nexus.Mobile.Services.ViewModels;
using Xunit;

namespace Nexus.UnitTests;

public sealed class IntegrationModulesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-mod-unit-" + Guid.NewGuid().ToString("N"));
    private readonly SystemClock _clock = new();

    public IntegrationModulesTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ModuleSettings_IndependentToggles_DoNotAffectOtherModulesAndPersistInSqlite()
    {
        var (settingsService, _, _) = await CreateServicesAsync();

        var initial = await settingsService.GetModuleStatusesAsync();
        Assert.True(initial.IsSuccess);
        Assert.Equal(4, initial.Value.Count);
        Assert.All(initial.Value, m => Assert.True(m.IsEnabled));

        var disabledNotify = await settingsService.SetModuleEnabledAsync(
            new UpdateModuleToggleRequest(IntegrationModuleKind.Notify, IsEnabled: false));
        Assert.True(disabledNotify.IsSuccess);
        Assert.False(disabledNotify.Value.IsEnabled);
        Assert.Equal(ModuleAvailabilityState.Disabled, disabledNotify.Value.Availability);

        var after = await settingsService.GetModuleStatusesAsync();
        Assert.True(after.IsSuccess);
        Assert.False(after.Value.Single(m => m.ModuleKind == IntegrationModuleKind.Notify).IsEnabled);
        Assert.True(after.Value.Single(m => m.ModuleKind == IntegrationModuleKind.Control).IsEnabled);
        Assert.True(after.Value.Single(m => m.ModuleKind == IntegrationModuleKind.Monitor).IsEnabled);
        Assert.True(after.Value.Single(m => m.ModuleKind == IntegrationModuleKind.Media).IsEnabled);
    }

    [Fact]
    public async Task WindowsControlExecutor_HighImpactPowerActions_EnforceConfirmationAndRespectDryRun()
    {
        var (settingsService, _, localCtx) = await CreateServicesAsync();
        var executor = new WindowsControlExecutor(
            settingsService,
            localCtx,
            new WindowsControlOptions
            {
                DryRunPowerActions = true,
                SimulateInputInHeadlessTests = true
            },
            _clock,
            NullLogger<WindowsControlExecutor>.Instance);

        var unconfirmed = await executor.ExecuteAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = Guid.NewGuid(),
            Action = RemoteControlActionType.PowerShutdown,
            ConfirmedByUser = false
        });
        Assert.True(unconfirmed.IsSuccess);
        Assert.False(unconfirmed.Value.Executed);
        Assert.True(unconfirmed.Value.RequiresConfirmation);
        Assert.Equal("CONFIRMATION_REQUIRED", unconfirmed.Value.ErrorCode);

        var confirmed = await executor.ExecuteAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = Guid.NewGuid(),
            Action = RemoteControlActionType.PowerLock,
            ConfirmedByUser = true
        });
        Assert.True(confirmed.IsSuccess);
        Assert.True(confirmed.Value.Executed);
        Assert.False(confirmed.Value.RequiresConfirmation);

        // Disabling control module blocks execution cleanly
        await settingsService.SetModuleEnabledAsync(new UpdateModuleToggleRequest(IntegrationModuleKind.Control, false));
        var whileDisabled = await executor.ExecuteAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = Guid.NewGuid(),
            Action = RemoteControlActionType.MouseLeftClick
        });
        Assert.True(whileDisabled.IsSuccess);
        Assert.False(whileDisabled.Value.Executed);
        Assert.Equal("MODULE_DISABLED", whileDisabled.Value.ErrorCode);
    }

    [Fact]
    public void RemoteControlContracts_ValidateRequiredArgumentsAndIndependentPermissions()
    {
        var invalidKey = new RemoteControlCommandRequest { Action = RemoteControlActionType.KeyPress, KeyName = "" };
        Assert.NotEmpty(invalidKey.Validate());

        var invalidVol = new RemoteControlCommandRequest { Action = RemoteControlActionType.VolumeSet, VolumePercent = 150 };
        Assert.NotEmpty(invalidVol.Validate());

        Assert.True(NexusPermission.All.HasFlag(NexusPermission.ControlInput));
        Assert.True(NexusPermission.All.HasFlag(NexusPermission.SystemPower));
        Assert.True(NexusPermission.All.HasFlag(NexusPermission.HardwareMonitor));
        Assert.True(NexusPermission.All.HasFlag(NexusPermission.MediaControl));
        Assert.False(NexusPermission.ControlInput.HasFlag(NexusPermission.SystemPower));
    }

    [Fact]
    public async Task NexusNotifyService_AppliesAllowListBlockListAndDisableToggle()
    {
        var (settingsService, feedRepo, _) = await CreateServicesAsync();
        var notifyService = new NexusNotifyService(
            settingsService,
            feedRepo,
            _clock,
            NullLogger<NexusNotifyService>.Instance);

        var forwardedEvents = 0;
        notifyService.NotificationReceived += (_, _) => forwardedEvents++;

        await settingsService.SaveNotifySettingsAsync(new NotifyModuleSettings
        {
            IsEnabled = true,
            FilterMode = NotificationFilterMode.AllowList,
            AllowedApps = ["WhatsApp", "com.Slack"]
        });

        var senderId = Guid.NewGuid();
        var blocked = await notifyService.IngestAsync(new PushMobileNotificationRequest
        {
            SourceDeviceId = senderId,
            AppName = "PromoApp",
            PackageName = "com.promo.ads",
            Title = "Oferta",
            Body = "Desconto"
        });
        Assert.True(blocked.IsSuccess);
        Assert.False(blocked.Value.Forwarded);
        Assert.Equal("BlockedNotInAllowList", blocked.Value.FilterOutcome);

        var allowed = await notifyService.IngestAsync(new PushMobileNotificationRequest
        {
            SourceDeviceId = senderId,
            AppName = "Slack",
            PackageName = "com.slack",
            Title = "Deploy concluído",
            Body = "Build 100% ok"
        });
        Assert.True(allowed.IsSuccess);
        Assert.True(allowed.Value.Forwarded);
        Assert.Equal(1, forwardedEvents);

        // Disable forwarding explicitly
        await settingsService.SetModuleEnabledAsync(new UpdateModuleToggleRequest(IntegrationModuleKind.Notify, false));
        var whenDisabled = await notifyService.IngestAsync(new PushMobileNotificationRequest
        {
            SourceDeviceId = senderId,
            AppName = "WhatsApp",
            Title = "Oi",
            Body = "Teste"
        });
        Assert.True(whenDisabled.IsSuccess);
        Assert.False(whenDisabled.Value.Forwarded);
        Assert.Equal("ModuleDisabled", whenDisabled.Value.FilterOutcome);

        var history = await notifyService.ListHistoryAsync();
        Assert.True(history.IsSuccess);
        Assert.Single(history.Value);
        Assert.True((await notifyService.DismissAsync(history.Value[0].NotificationId)).IsSuccess);
        Assert.True((await notifyService.ListHistoryAsync()).Value[0].IsDismissed);
    }

    [Fact]
    public async Task HostMonitorService_CollectsRealHostTelemetryAndHonestSensorAvailability()
    {
        var (settingsService, _, localCtx) = await CreateServicesAsync();
        var monitor = new HostMonitorService(settingsService, localCtx, _clock, NullLogger<HostMonitorService>.Instance);

        var result = await monitor.CollectSnapshotAsync();
        Assert.True(result.IsSuccess, result.Error?.Message);
        var snap = result.Value;

        Assert.Equal(Environment.MachineName, snap.HostName);
        Assert.True(snap.Cpu.LogicalCores > 0);
        Assert.NotEmpty(snap.Disks);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(snap.Cpu.UsagePercent.IsAvailable);
            Assert.InRange(snap.Cpu.UsagePercent.Value, 0d, 100d);
            Assert.True(snap.Memory.TotalMb.IsAvailable);
            Assert.True(snap.Memory.TotalMb.Value > 128d);
        }

        // Thermal sensors are never simulated when OS/hardware lacks unprivileged thermal access
        Assert.False(snap.Temperatures.IsAvailable);
        Assert.False(snap.Temperatures.PrimaryCelsius.IsAvailable);
        Assert.NotEmpty(snap.Temperatures.StatusMessage);

        // Disabling Monitor returns explicit MODULE_DISABLED error instead of stale/fake values
        await settingsService.SetModuleEnabledAsync(new UpdateModuleToggleRequest(IntegrationModuleKind.Monitor, false));
        var disabledSnap = await monitor.CollectSnapshotAsync();
        Assert.True(disabledSnap.IsFailure);
        Assert.Equal("MODULE_DISABLED", disabledSnap.Error!.Code);
    }

    [Fact]
    public async Task WindowsMediaService_And_ViewModels_OperateCleanlyAndPreserveSafetyConfirmation()
    {
        var (settingsService, feedRepo, localCtx) = await CreateServicesAsync();
        var controlExecutor = new WindowsControlExecutor(
            settingsService,
            localCtx,
            new WindowsControlOptions { DryRunPowerActions = true, SimulateInputInHeadlessTests = true },
            _clock,
            NullLogger<WindowsControlExecutor>.Instance);
        var hostMonitor = new HostMonitorService(settingsService, localCtx, _clock, NullLogger<HostMonitorService>.Instance);
        var mediaService = new WindowsMediaService(settingsService, controlExecutor, localCtx, _clock, NullLogger<WindowsMediaService>.Instance);
        var notifyService = new NexusNotifyService(settingsService, feedRepo, _clock, NullLogger<NexusNotifyService>.Instance);
        var stubRealtime = new StubRealtimeChannel();

        using var desktopVm = new ModulesViewModel(
            settingsService,
            controlExecutor,
            hostMonitor,
            mediaService,
            notifyService,
            stubRealtime,
            localCtx);

        await desktopVm.InitializeAsync();
        Assert.Equal(4, desktopVm.ModuleStatuses.Count);
        Assert.NotEmpty(desktopVm.Hotkeys);

        desktopVm.NewHotkeyName = "Abrir Terminal";
        desktopVm.NewHotkeyKeys = "Ctrl+Alt+T";
        await desktopVm.AddHotkeyCommand.ExecuteAsync();
        Assert.Contains(desktopVm.Hotkeys, h => h.DisplayName == "Abrir Terminal");

        await desktopVm.MediaPlayPauseCommand.ExecuteAsync();
        Assert.Equal(MediaPlaybackStatus.Playing.ToString(), desktopVm.MediaPlaybackBadge);

        var stubClient = new StubModulesClient(settingsService, controlExecutor, hostMonitor, mediaService, notifyService);
        using var mobileVm = new MobileModulesViewModel(stubClient, settingsService, stubRealtime, localCtx);
        await mobileVm.InitializeAsync();
        Assert.False(mobileVm.HasPendingPowerConfirmation);

        await mobileVm.RequestShutdownCommand.ExecuteAsync();
        Assert.True(mobileVm.HasPendingPowerConfirmation);
        await mobileVm.ConfirmPowerActionCommand.ExecuteAsync();
        Assert.False(mobileVm.HasPendingPowerConfirmation);
        Assert.Equal(1, stubClient.ConfirmedPowerCalls);
    }

    private async Task<(ModuleSettingsService Settings, SqliteMobileNotificationFeedRepository FeedRepo, ILocalDeviceContext Device)> CreateServicesAsync()
    {
        var storageOptions = new NexusStorageOptions
        {
            RootDirectory = _tempDir,
            DatabaseFileName = $"unit-{Guid.NewGuid():N}.db",
            EnablePooling = false
        };
        var serializer = new SystemTextJsonSerializer();
        var connFactory = new SqliteConnectionFactory(storageOptions);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var localCtx = new DefaultLocalDeviceContext(
            Guid.NewGuid(),
            "UnitTestWin",
            DevicePlatform.Windows,
            DeviceType.Desktop,
            "Windows 11",
            "0.8.0",
            DeviceCapability.All);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState { LocalDevice = localCtx.ToDeviceInfo() });
        var init = new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance);
        Assert.True((await init.InitializeAsync()).IsSuccess);

        var settingsStore = new SqliteSettingsStore(connFactory, failureHandler, serializer, _clock);
        var settingsService = new ModuleSettingsService(settingsStore, _clock, NullLogger<ModuleSettingsService>.Instance);
        var feedRepo = new SqliteMobileNotificationFeedRepository(connFactory, failureHandler);
        return (settingsService, feedRepo, localCtx);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class StubRealtimeChannel : IRealtimeClientChannel
    {
        public TransportKind Transport => TransportKind.WebSocket;
        public bool IsConnected => false;
        public ConnectionState State => ConnectionState.Disconnected;
        public RealtimeSessionSnapshot? CurrentSession => null;
        public IReadOnlyList<DevicePresenceSnapshot> LivePresence => Array.Empty<DevicePresenceSnapshot>();
        public IReadOnlyList<RealtimeMessageEnvelope> RecentMessages => Array.Empty<RealtimeMessageEnvelope>();
        public IReadOnlyList<RealtimeMessageAck> RecentAcknowledgements => Array.Empty<RealtimeMessageAck>();
        public string? LastError => null;
        public DateTimeOffset? LastHeartbeatUtc => null;

#pragma warning disable CS0067
        public event EventHandler<ConnectionStateSnapshot>? StateChanged;
        public event EventHandler<RealtimeMessageEnvelope>? MessageReceived;
        public event EventHandler<RealtimeMessageAck>? AckReceived;
        public event EventHandler<DeviceCommand>? CommandReceived;
        public event EventHandler<CommandCancellationRequest>? CommandCancelled;
        public event EventHandler<CommandExecutionResult>? CommandResultReceived;
        public event EventHandler<IReadOnlyList<DevicePresenceSnapshot>>? PresenceUpdated;
        public event EventHandler<ClipboardEntryContract>? ClipboardReceived;
        public event EventHandler<NexusNoteContract>? NoteChangeReceived;
        public event EventHandler<FileTransferProgressEvent>? TransferProgressReceived;
        public event EventHandler<FileTransferRecord>? TransferAvailableReceived;
        public event EventHandler<MobileNotificationRecord>? MobileNotificationReceived;
        public event EventHandler<MediaPlaybackStateContract>? MediaStateReceived;
        public event EventHandler<IntegrationModuleStatusSnapshot>? ModuleStatusChangedReceived;
        public event EventHandler<Nexus.Contracts.Automation.PanelProfile>? PanelProfileUpdatedReceived;
        public event EventHandler<Nexus.Contracts.Automation.AutomationExecutionRecord>? AutomationExecutionReceived;
#pragma warning restore CS0067

        public Task<Result> OpenAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> CloseAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(NexusRequest<TRequest> request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<NexusResponse<TResponse>>.Failure(NexusError.Network("Stub offline")));
        public Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<int>> SendHeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result<int>.Success(0));
        public Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(string? endpointOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeSessionSnapshot>.Failure(NexusError.Cancelled()));
        public Task<Result<RealtimeMessageAck>> SendEnvelopeAsync(RealtimeMessageEnvelope envelope, bool waitForAck = true, TimeSpan? ackTimeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Failure(NexusError.Cancelled()));
        public Task<Result<RealtimeMessageAck>> SendCommandAsync(DeviceCommand command, bool waitForAck = true, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Failure(NexusError.Cancelled()));
        public Task<Result> CancelCommandAsync(Guid commandId, Guid targetDeviceId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result> SendCommandResultAsync(CommandExecutionResult result, Guid? targetDeviceId = null, Guid? correlationId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result> AcknowledgeAsync(RealtimeMessageAck ack, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<RealtimeMessageAck>> PublishIntegrationEventAsync(IntegrationEvent integrationEvent, Guid? targetDeviceId = null, bool ordered = true, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Failure(NexusError.Cancelled()));
        public Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<DevicePresenceSnapshot>>.Success(Array.Empty<DevicePresenceSnapshot>()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubModulesClient(
        IModuleSettingsService settings,
        IWindowsControlExecutor control,
        IHostMonitorService monitor,
        IWindowsMediaService media,
        INexusNotifyService notify) : IIntegrationModulesClient
    {
        public int ConfirmedPowerCalls { get; private set; }

        public Task<Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>> GetRemoteModuleStatusesAsync(CancellationToken cancellationToken = default)
            => settings.GetModuleStatusesAsync(cancellationToken);

        public Task<Result<IntegrationModuleStatusSnapshot>> SetRemoteModuleEnabledAsync(UpdateModuleToggleRequest request, CancellationToken cancellationToken = default)
            => settings.SetModuleEnabledAsync(request, cancellationToken);

        public Task<Result<RemoteControlExecutionReceipt>> SendControlCommandAsync(RemoteControlCommandRequest request, CancellationToken cancellationToken = default)
        {
            if (RemoteControlCommandRequest.IsHighImpactAction(request.Action) && request.ConfirmedByUser)
            {
                ConfirmedPowerCalls++;
            }

            return control.ExecuteAsync(request, cancellationToken);
        }

        public Task<Result<ControlModuleSettings>> GetRemoteControlSettingsAsync(CancellationToken cancellationToken = default)
            => settings.GetControlSettingsAsync(cancellationToken);

        public Task<Result<ControlModuleSettings>> SaveRemoteControlSettingsAsync(ControlModuleSettings s, CancellationToken cancellationToken = default)
            => settings.SaveControlSettingsAsync(s, cancellationToken);

        public Task<Result<PushMobileNotificationReceipt>> PushMobileNotificationAsync(PushMobileNotificationRequest request, CancellationToken cancellationToken = default)
            => notify.IngestAsync(request, cancellationToken);

        public Task<Result<IReadOnlyList<MobileNotificationRecord>>> GetRemoteNotificationHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
            => notify.ListHistoryAsync(limit, cancellationToken);

        public Task<Result> DismissRemoteNotificationAsync(Guid notificationId, CancellationToken cancellationToken = default)
            => notify.DismissAsync(notificationId, cancellationToken);

        public Task<Result<HostTelemetrySnapshot>> GetRemoteMonitorSnapshotAsync(CancellationToken cancellationToken = default)
            => monitor.CollectSnapshotAsync(cancellationToken);

        public Task<Result<MediaPlaybackStateContract>> GetRemoteMediaStateAsync(CancellationToken cancellationToken = default)
            => media.GetCurrentStateAsync(cancellationToken);

        public Task<Result<MediaPlaybackStateContract>> SendMediaCommandAsync(MediaControlCommandRequest request, CancellationToken cancellationToken = default)
            => media.ExecuteCommandAsync(request, cancellationToken);
    }
}
