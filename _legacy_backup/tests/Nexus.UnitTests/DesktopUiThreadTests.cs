using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Contracts.Automation;
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
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Modules;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Sync;
using Nexus.Core.Transfers;
using Nexus.Desktop.Services.DependencyInjection;
using Nexus.Desktop.Services.Mvvm;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Modules.BuiltIn;
using Xunit;

namespace Nexus.UnitTests;

public sealed class DesktopUiThreadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-desktop-ui-tests-" + Guid.NewGuid().ToString("N"));

    public DesktopUiThreadTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task StartupInitialization_AllViewModelsInitialize_WithoutOffThreadNotifications()
    {
        using var uiDispatcher = new StrictUiThreadDispatcher();
        var realtime = new ControllableRealtimeChannel();
        var (provider, shell, tracker) = await BuildDesktopShellAsync(uiDispatcher, realtime, "startup-init");

        await using (provider)
        {
            // Trigger initialization from a worker thread just like App.OnStartup + ConfigureAwait(false)
            await Task.Run(() => shell.InitializeAsync());
            await uiDispatcher.DrainAsync();

            Assert.False(shell.IsInitializing);
            Assert.Equal(11, shell.NavigationTabs.Count);
            Assert.NotEmpty(shell.Modules.ModuleStatuses);
            Assert.NotEmpty(shell.Automation.Profiles);
            Assert.Empty(tracker.Violations);
            Assert.True(uiDispatcher.InvokeAsyncCount + uiDispatcher.EnqueueCount > 0);
            Assert.True(tracker.CollectionChangedCount > 0);
            Assert.True(tracker.PropertyChangedCount > 0);
        }
    }

    [Fact]
    public async Task AsyncCommandsWithConfigureAwaitFalse_MarshalCollectionsPropertiesAndCanExecuteToUiThread()
    {
        using var uiDispatcher = new StrictUiThreadDispatcher();
        var realtime = new ControllableRealtimeChannel();
        var clipboardAdapter = new ControllableClipboardAdapter("Texto de área de transferência UI");
        var (provider, shell, tracker) = await BuildDesktopShellAsync(
            uiDispatcher,
            realtime,
            "async-commands",
            clipboardAdapter: clipboardAdapter);

        await using (provider)
        {
            await uiDispatcher.InvokeAsync(() => { });
            await shell.InitializeAsync();

            var tempTransferFile = Path.Combine(_tempDir, "doc-async-test.txt");
            await File.WriteAllTextAsync(tempTransferFile, "payload-seguro-nexus-ui-thread");

            var localDeviceId = provider.GetRequiredService<ILocalDeviceContext>().DeviceId;

            // Register a trusted remote peer so Transfer/Devices pick it up
            var deviceRegistry = provider.GetRequiredService<IDeviceRegistry>();
            var trustedRepo = provider.GetRequiredService<ITrustedDeviceRepository>();
            var remotePeerId = Guid.NewGuid();
            await RegisterDeviceAndTrustAsync(deviceRegistry, trustedRepo, remotePeerId, "Pixel Async Peer");

            // Execute ViewModel async operations across ThreadPool callers with ConfigureAwait(false)
            await Task.Run(async () =>
            {
                await uiDispatcher.InvokeAsync(() =>
                {
                    shell.Connectivity.NewEndpointLabel = "Rota VPN Teste";
                    shell.Connectivity.NewEndpointUrl = "https://100.64.0.10:7443";
                    shell.Connectivity.NewEndpointRouteKind = ConnectionRouteKind.RemoteVpnTailscale;
                });
                await shell.Connectivity.AddEndpointCommand.ExecuteAsync();
                await shell.Connectivity.ProbeAllCommand.ExecuteAsync();
                await shell.Connectivity.SelectBestRouteCommand.ExecuteAsync();

                await uiDispatcher.InvokeAsync(() =>
                {
                    shell.Sync.ClipboardInputText = "Clipboard enviado via ThreadPool";
                    shell.Sync.EditorTitle = "Nota Assíncrona";
                    shell.Sync.EditorBody = "Corpo salvo após ConfigureAwait(false)";
                });
                await shell.Sync.SendClipboardTextCommand.ExecuteAsync();
                await shell.Sync.CaptureOsClipboardCommand.ExecuteAsync();
                await shell.Sync.SaveNoteCommand.ExecuteAsync();

                await shell.Pairing.GenerateQrCommand.ExecuteAsync();
                await shell.Pairing.PollStatusCommand.ExecuteAsync();

                await shell.Devices.LoadDevicesCommand.ExecuteAsync();
                await uiDispatcher.InvokeAsync(() =>
                {
                    shell.Transfers.AddSelectedFiles([tempTransferFile]);
                    shell.Transfers.SelectedPeer = shell.Transfers.AuthorizedPeers.FirstOrDefault();
                });
                await shell.Transfers.ApplyQueueConfigCommand.ExecuteAsync();
                await shell.Transfers.RefreshCommand.ExecuteAsync();

                await shell.Modules.RefreshAllCommand.ExecuteAsync();
                await uiDispatcher.InvokeAsync(() =>
                {
                    shell.Modules.NewHotkeyId = "ui-test-key";
                    shell.Modules.NewHotkeyName = "Atalho UI";
                    shell.Modules.NewHotkeyKeys = "Ctrl+Shift+U";
                });
                await shell.Modules.AddHotkeyCommand.ExecuteAsync();

                await uiDispatcher.InvokeAsync(() =>
                {
                    shell.Automation.NewProfileName = "Perfil UI Assíncrono";
                    shell.Automation.RuleName = "Regra UI Segura";
                    shell.Automation.ActionTitle = "Indicador UI";
                    shell.Automation.ActionMessage = "Executado na UI thread";
                });
                await shell.Automation.CreateProfileCommand.ExecuteAsync();
                await shell.Automation.AddWidgetCommand.ExecuteAsync();
                await shell.Automation.SaveRuleCommand.ExecuteAsync();

                await shell.Dashboard.RefreshCommand.ExecuteAsync();
                await shell.Dashboard.EmitDiagnosticEventCommand.ExecuteAsync();
                await shell.SystemStatus.RefreshCommand.ExecuteAsync();
                await shell.Realtime.RefreshPresenceCommand.ExecuteAsync();
            });

            await uiDispatcher.DrainAsync();

            Assert.Empty(tracker.Violations);
            Assert.NotEmpty(shell.Connectivity.Endpoints);
            Assert.NotEmpty(shell.Sync.ClipboardHistory);
            Assert.NotEmpty(shell.Sync.Notes);
            Assert.Contains(shell.Automation.Profiles, p => p.Name == "Perfil UI Assíncrono");
            Assert.NotEmpty(shell.Automation.Rules);
            Assert.Contains(shell.Devices.TrustedDevices, d => d.DeviceId == remotePeerId);
            Assert.NotEmpty(shell.Dashboard.RecentActivities);
            Assert.True(localDeviceId != Guid.Empty);
        }
    }

    [Fact]
    public async Task BackgroundAndSignalREvents_FireFromThreadPool_NeverMutateCollectionsOffUiThread()
    {
        using var uiDispatcher = new StrictUiThreadDispatcher();
        var realtime = new ControllableRealtimeChannel();
        var (provider, shell, tracker) = await BuildDesktopShellAsync(uiDispatcher, realtime, "bg-events");

        await using (provider)
        {
            await shell.InitializeAsync();
            await uiDispatcher.DrainAsync();

            var localId = provider.GetRequiredService<ILocalDeviceContext>().DeviceId;
            var peerId = Guid.NewGuid();
            var notifyService = provider.GetRequiredService<INexusNotifyService>();
            var routeOrchestrator = provider.GetRequiredService<INexusRouteOrchestrator>();
            var transferManager = provider.GetRequiredService<ITransferManager>();
            var stateManager = provider.GetRequiredService<IStateManager<NexusRuntimeState>>();
            var clipboardService = provider.GetRequiredService<IClipboardSyncService>();
            var notesService = provider.GetRequiredService<INotesSyncService>();
            var panelService = provider.GetRequiredService<IPanelLayoutService>();
            var automationEngine = provider.GetRequiredService<IAutomationEngine>();

            var ruleSaveRes = await automationEngine.SaveRuleAsync(new AutomationRule
            {
                Name = "Regra Evento Background",
                Trigger = AutomationTriggerType.Manual,
                Actions =
                [
                    new AutomationAction
                    {
                        ActionType = AutomationActionType.ShowPanelIndicator,
                        Title = "BG Indicador",
                        Message = "Disparado de worker thread"
                    }
                ]
            });
            Assert.True(ruleSaveRes.IsSuccess);

            var sampleUpload = Path.Combine(_tempDir, "bg-transfer.bin");
            await File.WriteAllBytesAsync(sampleUpload, new byte[2048]);

            // Emit every service + SignalR event explicitly from ThreadPool background threads
            await Task.Run(async () =>
            {
                realtime.RaiseStateChanged(new ConnectionStateSnapshot
                {
                    LocalDeviceId = localId,
                    RemoteDeviceId = peerId,
                    State = ConnectionState.Connected,
                    Transport = TransportKind.SignalR,
                    Endpoint = "https://192.168.1.50:7443/hub",
                    LatencyMs = 7
                });

                realtime.RaisePresenceUpdated([
                    new DevicePresenceSnapshot
                    {
                        DeviceId = peerId,
                        DeviceName = "Android SignalR",
                        Platform = DevicePlatform.Android,
                        Type = DeviceType.Phone,
                        TrustStatus = DeviceTrustStatus.Authorized,
                        IsRealtimeConnected = true,
                        ConnectionState = ConnectionState.Connected
                    }
                ]);

                realtime.RaiseMessageReceived(new RealtimeMessageEnvelope
                {
                    SourceDeviceId = peerId,
                    TargetDeviceId = localId,
                    MessageType = "test.background.event",
                    Kind = RealtimeMessageKind.Event,
                    PayloadJson = "{\"ok\":true}",
                    SequenceNumber = 1
                });

                realtime.RaiseAckReceived(new RealtimeMessageAck
                {
                    MessageId = Guid.CreateVersion7(),
                    RespondingDeviceId = peerId,
                    Status = RealtimeAckStatus.Processed,
                    Detail = "ACK BG"
                });

                realtime.RaiseMobileNotificationReceived(new MobileNotificationRecord
                {
                    NotificationId = Guid.NewGuid(),
                    SourceDeviceId = peerId,
                    AppName = "Signal",
                    Title = "Nova Mensagem BG",
                    Body = "Entregue fora da UI thread",
                    ForwardedToDesktop = true,
                    FilterOutcome = "Allowed",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ReceivedAtUtc = DateTimeOffset.UtcNow
                });

                realtime.RaiseMediaStateReceived(new MediaPlaybackStateContract
                {
                    HostDeviceId = localId,
                    IsAvailable = true,
                    IsMetadataAvailable = true,
                    Title = "Faixa SignalR BG",
                    Artist = "NEXUS Synth",
                    PlaybackStatus = MediaPlaybackStatus.Playing,
                    VolumePercent = SensorReading<int>.Available(62, "%"),
                    IsMuted = SensorReading<bool>.Available(false)
                });

                realtime.RaiseModuleStatusChangedReceived(new IntegrationModuleStatusSnapshot
                {
                    ModuleKind = IntegrationModuleKind.Notify,
                    ModuleId = "nexus.notify",
                    DisplayName = "NEXUS Notify",
                    IsEnabled = true,
                    Availability = ModuleAvailabilityState.Available,
                    StatusMessage = "Atualizado por SignalR BG"
                });

                await routeOrchestrator.RegisterEndpointAsync(new RegisterEndpointRequest
                {
                    DeviceId = peerId,
                    DeviceName = "Android Endpoint",
                    Label = "LAN Test",
                    RouteKind = ConnectionRouteKind.LocalLan,
                    EndpointUrl = "https://192.168.1.99:7443"
                });

                await notifyService.IngestAsync(new PushMobileNotificationRequest
                {
                    SourceDeviceId = peerId,
                    AppName = "WhatsApp",
                    Title = "Notificação Persistida",
                    Body = "Ingestão via thread secundária"
                });

                await transferManager.EnqueueSendFileAsync(peerId, "Android Peer", sampleUpload);
                await clipboardService.CaptureAndSendTextAsync("Clipboard BG");
                await notesService.CreateNoteAsync("Nota BG", "Criada em Task.Run");
                await panelService.CreateProfileAsync("Painel BG");
                await automationEngine.TriggerRuleManuallyAsync(ruleSaveRes.Value.RuleId);

                stateManager.Update(s => s with
                {
                    KnownDevicesCount = 2,
                    ConnectionState = ConnectionState.Connected,
                    UnreadNotificationsCount = 3
                });
            });

            await uiDispatcher.DrainAsync();

            Assert.Empty(tracker.Violations);
            Assert.NotEmpty(shell.Realtime.PresenceItems);
            Assert.NotEmpty(shell.Realtime.RecentMessages);
            Assert.NotEmpty(shell.Realtime.RecentAcks);
            Assert.True(shell.Modules.NotificationFeed.Count >= 2);
            Assert.Contains("Faixa SignalR BG", shell.Modules.MediaTrackText);
            Assert.NotEmpty(shell.Connectivity.Endpoints);
            Assert.True(shell.Transfers.ActiveTransfers.Count + shell.Transfers.HistoryItems.Count >= 1);
            Assert.NotEmpty(shell.Sync.ClipboardHistory);
            Assert.NotEmpty(shell.Sync.Notes);
            Assert.NotEmpty(shell.Automation.ExecutionHistory);
            Assert.Contains("Conexão: Connected", shell.FooterStatusText);
        }
    }

    [Fact]
    public async Task SimultaneousMultiModuleUpdates_StressTest_PreservesThreadSafetyAndNoExceptions()
    {
        using var uiDispatcher = new StrictUiThreadDispatcher();
        var realtime = new ControllableRealtimeChannel();
        var (provider, shell, tracker) = await BuildDesktopShellAsync(uiDispatcher, realtime, "multi-stress");

        await using (provider)
        {
            await shell.InitializeAsync();
            await uiDispatcher.DrainAsync();

            var localId = provider.GetRequiredService<ILocalDeviceContext>().DeviceId;
            var peerId = Guid.NewGuid();
            var deviceRegistry = provider.GetRequiredService<IDeviceRegistry>();
            var trustedRepo = provider.GetRequiredService<ITrustedDeviceRepository>();
            await RegisterDeviceAndTrustAsync(deviceRegistry, trustedRepo, peerId, "Android Stress Peer");

            var notifyService = provider.GetRequiredService<INexusNotifyService>();
            var transferManager = provider.GetRequiredService<ITransferManager>();
            var clipboardService = provider.GetRequiredService<IClipboardSyncService>();
            var automationEngine = provider.GetRequiredService<IAutomationEngine>();
            var stateManager = provider.GetRequiredService<IStateManager<NexusRuntimeState>>();

            var ruleRes = await automationEngine.SaveRuleAsync(new AutomationRule
            {
                Name = "Stress Indicator",
                Trigger = AutomationTriggerType.Manual,
                CooldownSeconds = 0,
                MaxExecutionsPerMinute = 120,
                Actions =
                [
                    new AutomationAction
                    {
                        ActionType = AutomationActionType.ShowPanelIndicator,
                        Title = "Stress",
                        Message = "Tick"
                    }
                ]
            });
            Assert.True(ruleRes.IsSuccess);

            var stressFile = Path.Combine(_tempDir, "stress.dat");
            await File.WriteAllBytesAsync(stressFile, new byte[1024]);

            using var cts = new CancellationTokenSource();

            // Simulate WPF UI layout constantly enumerating ViewModel collections on UI thread during background load
            var uiEnumTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await uiDispatcher.InvokeAsync(() =>
                    {
                        _ = shell.Dashboard.RecentActivities.Count;
                        foreach (var _ in shell.Modules.NotificationFeed) { }
                        foreach (var _ in shell.Transfers.ActiveTransfers) { }
                        foreach (var _ in shell.Realtime.RecentMessages) { }
                        foreach (var _ in shell.Automation.ExecutionHistory) { }
                        foreach (var _ in shell.Devices.TrustedDeviceItems) { }
                    });
                    await Task.Delay(5);
                }
            });

            // Simultaneous burst across modules on ThreadPool threads
            var workers = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            {
                realtime.RaiseMessageReceived(new RealtimeMessageEnvelope
                {
                    SourceDeviceId = peerId,
                    TargetDeviceId = localId,
                    MessageType = $"stress.msg.{i}",
                    PayloadJson = $"{{\"i\":{i}}}",
                    SequenceNumber = i + 1
                });

                realtime.RaiseAckReceived(new RealtimeMessageAck
                {
                    MessageId = Guid.CreateVersion7(),
                    RespondingDeviceId = peerId,
                    Status = RealtimeAckStatus.Received
                });

                await notifyService.IngestAsync(new PushMobileNotificationRequest
                {
                    SourceDeviceId = peerId,
                    AppName = "StressApp",
                    Title = $"Alert #{i}",
                    Body = "Simultaneous event"
                });

                await clipboardService.CaptureAndSendTextAsync($"Clip-{i}-{Guid.NewGuid():N}");

                if (i % 4 == 0)
                {
                    await transferManager.EnqueueSendFileAsync(peerId, "Android Stress Peer", stressFile);
                    await shell.Devices.LoadDevicesCommand.ExecuteAsync();
                    await shell.Modules.RefreshAllCommand.ExecuteAsync();
                    await automationEngine.TriggerRuleManuallyAsync(ruleRes.Value.RuleId);
                    stateManager.Update(s => s with { KnownDevicesCount = i + 1 });
                }
            })).ToArray();

            await Task.WhenAll(workers);
            cts.Cancel();
            await uiEnumTask;
            await uiDispatcher.DrainAsync();

            Assert.Empty(tracker.Violations);
            Assert.True(shell.Modules.NotificationFeed.Count <= 80);
            Assert.True(shell.Realtime.RecentMessages.Count <= 80);
            Assert.True(shell.Realtime.RecentAcks.Count <= 80);
            Assert.True(shell.Automation.ExecutionHistory.Count <= 120);
        }
    }

    [Fact]
    public async Task NavigationAcrossAllSections_DuringBackgroundTraffic_SucceedsSafely()
    {
        using var uiDispatcher = new StrictUiThreadDispatcher();
        var realtime = new ControllableRealtimeChannel();
        var (provider, shell, tracker) = await BuildDesktopShellAsync(uiDispatcher, realtime, "nav-all");

        await using (provider)
        {
            await shell.InitializeAsync();

            using var cts = new CancellationTokenSource();
            var backgroundNoise = Task.Run(async () =>
            {
                int tick = 0;
                while (!cts.IsCancellationRequested)
                {
                    tick++;
                    realtime.RaiseMobileNotificationReceived(new MobileNotificationRecord
                    {
                        NotificationId = Guid.NewGuid(),
                        SourceDeviceId = Guid.NewGuid(),
                        AppName = "NavNoise",
                        Title = $"Tick {tick}",
                        Body = "During navigation",
                        ForwardedToDesktop = true,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ReceivedAtUtc = DateTimeOffset.UtcNow
                    });
                    await shell.SystemStatus.RefreshAsync();
                    await Task.Delay(5);
                }
            });

            foreach (var section in Enum.GetValues<DesktopSection>())
            {
                await uiDispatcher.InvokeAsync(() =>
                {
                    shell.ActiveSection = section;
                    Assert.Equal(section, shell.ActiveSection);
                });
            }

            cts.Cancel();
            await backgroundNoise;
            await uiDispatcher.DrainAsync();

            Assert.Empty(tracker.Violations);
        }
    }

    [Fact]
    public async Task NotificationsTransfersAndDevicesUpdates_ConcurrentBatch_RemainConsistent()
    {
        using var uiDispatcher = new StrictUiThreadDispatcher();
        var realtime = new ControllableRealtimeChannel();
        var (provider, shell, tracker) = await BuildDesktopShellAsync(uiDispatcher, realtime, "ntd-batch");

        await using (provider)
        {
            await shell.InitializeAsync();

            var deviceRegistry = provider.GetRequiredService<IDeviceRegistry>();
            var trustedRepo = provider.GetRequiredService<ITrustedDeviceRepository>();
            var notifyService = provider.GetRequiredService<INexusNotifyService>();
            var transferManager = provider.GetRequiredService<ITransferManager>();

            var deviceIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var filePath = Path.Combine(_tempDir, "ntd-payload.bin");
            await File.WriteAllBytesAsync(filePath, new byte[4096]);

            var deviceTask = Task.Run(async () =>
            {
                for (int i = 0; i < deviceIds.Length; i++)
                {
                    await RegisterDeviceAndTrustAsync(deviceRegistry, trustedRepo, deviceIds[i], $"Android-{i}");
                    await shell.Devices.LoadDevicesCommand.ExecuteAsync();
                }
            });

            var notifyTask = Task.Run(async () =>
            {
                for (int i = 0; i < 6; i++)
                {
                    await notifyService.IngestAsync(new PushMobileNotificationRequest
                    {
                        SourceDeviceId = deviceIds[0],
                        AppName = "Messages",
                        Title = $"Msg {i}",
                        Body = "Concurrent notification"
                    });
                }
            });

            var transferTask = Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    await transferManager.EnqueueSendFileAsync(deviceIds[0], "Android-0", filePath);
                }
                await shell.Transfers.RefreshCommand.ExecuteAsync();
            });

            await Task.WhenAll(deviceTask, notifyTask, transferTask);
            await shell.Devices.LoadDevicesCommand.ExecuteAsync();
            await uiDispatcher.DrainAsync();

            Assert.Empty(tracker.Violations);
            Assert.True(shell.Devices.Devices.Count >= 3);
            Assert.Equal(3, shell.Devices.TrustedDevices.Count);
            Assert.Equal(3, shell.Devices.TrustedDeviceItems.Count);
            Assert.True(shell.Modules.NotificationFeed.Count >= 6);
            Assert.True(shell.Transfers.ActiveTransfers.Count + shell.Transfers.HistoryItems.Count >= 3);
        }
    }

    private static async Task RegisterDeviceAndTrustAsync(
        IDeviceRegistry deviceRegistry,
        ITrustedDeviceRepository trustedRepo,
        Guid deviceId,
        string name)
    {
        var now = DateTimeOffset.UtcNow;
        var reg = await deviceRegistry.RegisterOrUpdateAsync(new DeviceRegistrationContract
        {
            DeviceId = deviceId,
            Name = name,
            Platform = DevicePlatform.Android,
            Type = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "1.0.0",
            RequestedAtUtc = now
        }).ConfigureAwait(false);
        Assert.True(reg.IsSuccess);

        var trust = await trustedRepo.UpsertAsync(new TrustedDeviceRecord
        {
            DeviceId = deviceId,
            CustomName = name,
            Platform = DevicePlatform.Android,
            DeviceType = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "1.0.0",
            PublicKeySpkiBase64 = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAvTest",
            PublicKeyFingerprint = deviceId.ToString("N") + deviceId.ToString("N"),
            Permissions = NexusPermission.All,
            TrustStatus = DeviceTrustStatus.Authorized,
            PairedAtUtc = now,
            LastAccessAtUtc = now
        }).ConfigureAwait(false);
        Assert.True(trust.IsSuccess);
    }

    private async Task<(ServiceProvider Provider, MainShellViewModel Shell, UiThreadViolationTracker Tracker)> BuildDesktopShellAsync(
        StrictUiThreadDispatcher uiDispatcher,
        ControllableRealtimeChannel realtimeChannel,
        string scenarioName,
        ISystemClipboardAdapter? clipboardAdapter = null)
    {
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_tempDir, scenarioName);
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false,
            configureLocalDevice: d =>
            {
                d.DeviceName = "PC UI Thread Test";
                d.Platform = DevicePlatform.Windows;
                d.DeviceType = DeviceType.Desktop;
            });
        services.AddNexusModules();

        services.Replace(ServiceDescriptor.Singleton<IUiDispatcher>(uiDispatcher));
        services.Replace(ServiceDescriptor.Singleton<IRealtimeClientChannel>(realtimeChannel));
        services.Replace(ServiceDescriptor.Singleton<IRealtimeCredentialProvider>(
            sp => new TestCredentialProvider(sp.GetRequiredService<ILocalDeviceContext>())));
        services.Replace(ServiceDescriptor.Singleton<IRouteProber, InstantOfflineRouteProber>());
        services.Replace(ServiceDescriptor.Singleton<IFileTransferTransport, InstantTransferTransport>());
        if (clipboardAdapter is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(clipboardAdapter));
        }

        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddNexusDesktopServices();

        var provider = services.BuildServiceProvider();
        MainShellViewModel shell = null!;
        UiThreadViolationTracker tracker = null!;

        // Instantiate Shell & bind observers on the dedicated UI thread just like WPF Window startup
        await uiDispatcher.InvokeAsync(() =>
        {
            shell = provider.GetRequiredService<MainShellViewModel>();
            tracker = new UiThreadViolationTracker(uiDispatcher);
            tracker.AttachShell(shell);
        }).ConfigureAwait(false);

        return (provider, shell, tracker);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temporary SQLite file locks on test teardown
        }
    }

    private sealed class StrictUiThreadDispatcher : IUiDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _uiThread;
        private int _uiThreadId;
        private volatile bool _disposed;
        private long _enqueueCount;
        private long _invokeAsyncCount;

        public long EnqueueCount => Interlocked.Read(ref _enqueueCount);
        public long InvokeAsyncCount => Interlocked.Read(ref _invokeAsyncCount);

        public StrictUiThreadDispatcher()
        {
            using var ready = new ManualResetEventSlim(false);
            _uiThread = new Thread(() =>
            {
                _uiThreadId = Environment.CurrentManagedThreadId;
                ready.Set();
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = "StrictTestUiThread"
            };
            _uiThread.Start();
            ready.Wait();
        }

        public bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThreadId;

        public void Enqueue(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _enqueueCount);
            if (CheckAccess())
            {
                action();
                return;
            }

            try
            {
                _queue.Add(action);
            }
            catch (InvalidOperationException)
            {
                // Ignored when shutting down
            }
        }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (_disposed)
            {
                return Task.CompletedTask;
            }

            Interlocked.Increment(ref _invokeAsyncCount);
            if (CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    try
                    {
                        action();
                        tcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                tcs.TrySetResult();
            }

            return tcs.Task;
        }

        public Task DrainAsync() => InvokeAsync(static () => { });

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.CompleteAdding();
            if (!_uiThread.Join(TimeSpan.FromSeconds(5)))
            {
                // Ignore timeout on teardown
            }

            _queue.Dispose();
        }
    }

    private sealed class UiThreadViolationTracker
    {
        private readonly StrictUiThreadDispatcher _dispatcher;
        private readonly Lock _sync = new();
        private readonly List<string> _violations = new();
        private long _collectionChangedCount;
        private long _propertyChangedCount;
        private long _canExecuteChangedCount;

        public IReadOnlyList<string> Violations
        {
            get
            {
                lock (_sync)
                {
                    return _violations.ToArray();
                }
            }
        }

        public long CollectionChangedCount => Interlocked.Read(ref _collectionChangedCount);
        public long PropertyChangedCount => Interlocked.Read(ref _propertyChangedCount);
        public long CanExecuteChangedCount => Interlocked.Read(ref _canExecuteChangedCount);

        public UiThreadViolationTracker(StrictUiThreadDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void AttachShell(MainShellViewModel shell)
        {
            WatchPropertyChanged(nameof(MainShellViewModel), shell);

            WatchPropertyChanged(nameof(DashboardViewModel), shell.Dashboard);
            WatchCollection($"{nameof(DashboardViewModel)}.{nameof(shell.Dashboard.RecentActivities)}", shell.Dashboard.RecentActivities);
            WatchCommand($"{nameof(DashboardViewModel)}.{nameof(shell.Dashboard.RefreshCommand)}", shell.Dashboard.RefreshCommand);
            WatchCommand($"{nameof(DashboardViewModel)}.{nameof(shell.Dashboard.ProbeServerCommand)}", shell.Dashboard.ProbeServerCommand);

            WatchPropertyChanged(nameof(DevicesViewModel), shell.Devices);
            WatchCollection($"{nameof(DevicesViewModel)}.{nameof(shell.Devices.Devices)}", shell.Devices.Devices);
            WatchCollection($"{nameof(DevicesViewModel)}.{nameof(shell.Devices.TrustedDevices)}", shell.Devices.TrustedDevices);
            WatchCollection($"{nameof(DevicesViewModel)}.{nameof(shell.Devices.TrustedDeviceItems)}", shell.Devices.TrustedDeviceItems);
            WatchCommand($"{nameof(DevicesViewModel)}.{nameof(shell.Devices.LoadDevicesCommand)}", shell.Devices.LoadDevicesCommand);
            WatchCommand($"{nameof(DevicesViewModel)}.{nameof(shell.Devices.RevokeTrustedDeviceCommand)}", shell.Devices.RevokeTrustedDeviceCommand);

            WatchPropertyChanged(nameof(SystemStatusViewModel), shell.SystemStatus);
            WatchCollection($"{nameof(SystemStatusViewModel)}.{nameof(shell.SystemStatus.Modules)}", shell.SystemStatus.Modules);
            WatchCollection($"{nameof(SystemStatusViewModel)}.{nameof(shell.SystemStatus.RecentLogs)}", shell.SystemStatus.RecentLogs);
            WatchCommand($"{nameof(SystemStatusViewModel)}.{nameof(shell.SystemStatus.RefreshCommand)}", shell.SystemStatus.RefreshCommand);

            WatchPropertyChanged(nameof(PairingViewModel), shell.Pairing);
            WatchCommand($"{nameof(PairingViewModel)}.{nameof(shell.Pairing.GenerateQrCommand)}", shell.Pairing.GenerateQrCommand);
            WatchCommand($"{nameof(PairingViewModel)}.{nameof(shell.Pairing.PollStatusCommand)}", shell.Pairing.PollStatusCommand);
            WatchCommand($"{nameof(PairingViewModel)}.{nameof(shell.Pairing.ApprovePairingCommand)}", shell.Pairing.ApprovePairingCommand);

            WatchPropertyChanged(nameof(ConnectivityViewModel), shell.Connectivity);
            WatchCollection($"{nameof(ConnectivityViewModel)}.{nameof(shell.Connectivity.Endpoints)}", shell.Connectivity.Endpoints);
            WatchCommand($"{nameof(ConnectivityViewModel)}.{nameof(shell.Connectivity.AddEndpointCommand)}", shell.Connectivity.AddEndpointCommand);
            WatchCommand($"{nameof(ConnectivityViewModel)}.{nameof(shell.Connectivity.ProbeAllCommand)}", shell.Connectivity.ProbeAllCommand);

            WatchPropertyChanged(nameof(RealtimeViewModel), shell.Realtime);
            WatchCollection($"{nameof(RealtimeViewModel)}.{nameof(shell.Realtime.PresenceItems)}", shell.Realtime.PresenceItems);
            WatchCollection($"{nameof(RealtimeViewModel)}.{nameof(shell.Realtime.RecentMessages)}", shell.Realtime.RecentMessages);
            WatchCollection($"{nameof(RealtimeViewModel)}.{nameof(shell.Realtime.RecentAcks)}", shell.Realtime.RecentAcks);
            WatchCommand($"{nameof(RealtimeViewModel)}.{nameof(shell.Realtime.ConnectCommand)}", shell.Realtime.ConnectCommand);
            WatchCommand($"{nameof(RealtimeViewModel)}.{nameof(shell.Realtime.SendCommandCommand)}", shell.Realtime.SendCommandCommand);

            WatchPropertyChanged(nameof(SyncViewModel), shell.Sync);
            WatchCollection($"{nameof(SyncViewModel)}.{nameof(shell.Sync.ClipboardHistory)}", shell.Sync.ClipboardHistory);
            WatchCollection($"{nameof(SyncViewModel)}.{nameof(shell.Sync.Notes)}", shell.Sync.Notes);
            WatchCollection($"{nameof(SyncViewModel)}.{nameof(shell.Sync.SelectedNoteRevisions)}", shell.Sync.SelectedNoteRevisions);
            WatchCollection($"{nameof(SyncViewModel)}.{nameof(shell.Sync.UnresolvedConflicts)}", shell.Sync.UnresolvedConflicts);
            WatchCommand($"{nameof(SyncViewModel)}.{nameof(shell.Sync.SaveNoteCommand)}", shell.Sync.SaveNoteCommand);

            WatchPropertyChanged(nameof(TransferViewModel), shell.Transfers);
            WatchCollection($"{nameof(TransferViewModel)}.{nameof(shell.Transfers.PendingSelectedFiles)}", shell.Transfers.PendingSelectedFiles);
            WatchCollection($"{nameof(TransferViewModel)}.{nameof(shell.Transfers.AuthorizedPeers)}", shell.Transfers.AuthorizedPeers);
            WatchCollection($"{nameof(TransferViewModel)}.{nameof(shell.Transfers.ActiveTransfers)}", shell.Transfers.ActiveTransfers);
            WatchCollection($"{nameof(TransferViewModel)}.{nameof(shell.Transfers.ReceivedFiles)}", shell.Transfers.ReceivedFiles);
            WatchCollection($"{nameof(TransferViewModel)}.{nameof(shell.Transfers.HistoryItems)}", shell.Transfers.HistoryItems);
            WatchCommand($"{nameof(TransferViewModel)}.{nameof(shell.Transfers.RefreshCommand)}", shell.Transfers.RefreshCommand);

            WatchPropertyChanged(nameof(ModulesViewModel), shell.Modules);
            WatchCollection($"{nameof(ModulesViewModel)}.{nameof(shell.Modules.ModuleStatuses)}", shell.Modules.ModuleStatuses);
            WatchCollection($"{nameof(ModulesViewModel)}.{nameof(shell.Modules.Hotkeys)}", shell.Modules.Hotkeys);
            WatchCollection($"{nameof(ModulesViewModel)}.{nameof(shell.Modules.NotificationFeed)}", shell.Modules.NotificationFeed);
            WatchCollection($"{nameof(ModulesViewModel)}.{nameof(shell.Modules.Disks)}", shell.Modules.Disks);
            WatchCollection($"{nameof(ModulesViewModel)}.{nameof(shell.Modules.NetworkInterfaces)}", shell.Modules.NetworkInterfaces);
            WatchCollection($"{nameof(ModulesViewModel)}.{nameof(shell.Modules.TopProcesses)}", shell.Modules.TopProcesses);
            WatchCommand($"{nameof(ModulesViewModel)}.{nameof(shell.Modules.RefreshAllCommand)}", shell.Modules.RefreshAllCommand);

            WatchPropertyChanged(nameof(AutomationViewModel), shell.Automation);
            WatchCollection($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.Profiles)}", shell.Automation.Profiles);
            WatchCollection($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.ActiveWidgets)}", shell.Automation.ActiveWidgets);
            WatchCollection($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.ApprovedApps)}", shell.Automation.ApprovedApps);
            WatchCollection($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.Rules)}", shell.Automation.Rules);
            WatchCollection($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.ExecutionHistory)}", shell.Automation.ExecutionHistory);
            WatchCollection($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.ActiveIndicators)}", shell.Automation.ActiveIndicators);
            WatchCommand($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.CreateProfileCommand)}", shell.Automation.CreateProfileCommand);
            WatchCommand($"{nameof(AutomationViewModel)}.{nameof(shell.Automation.SaveRuleCommand)}", shell.Automation.SaveRuleCommand);

            WatchPropertyChanged(nameof(SettingsViewModel), shell.Settings);
        }

        private void WatchPropertyChanged(string owner, INotifyPropertyChanged target)
        {
            target.PropertyChanged += (_, args) =>
            {
                Interlocked.Increment(ref _propertyChangedCount);
                AssertOnUi($"{owner}.PropertyChanged({args.PropertyName})");
            };
        }

        private void WatchCollection(string name, INotifyCollectionChanged collection)
        {
            collection.CollectionChanged += (_, args) =>
            {
                Interlocked.Increment(ref _collectionChangedCount);
                AssertOnUi($"{name}.CollectionChanged({args.Action})");
            };
        }

        private void WatchCommand(string name, ICommand command)
        {
            command.CanExecuteChanged += (_, _) =>
            {
                Interlocked.Increment(ref _canExecuteChangedCount);
                AssertOnUi($"{name}.CanExecuteChanged");
            };
        }

        private void AssertOnUi(string eventDescription)
        {
            if (!_dispatcher.CheckAccess())
            {
                lock (_sync)
                {
                    _violations.Add($"{eventDescription} fired on non-UI thread {Environment.CurrentManagedThreadId} ({Thread.CurrentThread.Name ?? "worker"})");
                }
            }
        }
    }

    private sealed class ControllableClipboardAdapter : ISystemClipboardAdapter
    {
        private string _currentText;

        public ControllableClipboardAdapter(string initialText)
        {
            _currentText = initialText;
        }

        public bool SupportsContinuousBackgroundMonitor => true;
        public string PlatformRestrictionNotice => "Teste determinístico em memória.";
#pragma warning disable CS0067
        public event EventHandler<string>? SystemClipboardTextCaptured;
#pragma warning restore CS0067

        public Task<Result<string?>> ReadTextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<string?>.Success(_currentText));

        public Task<Result> WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            _currentText = text;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class TestCredentialProvider(ILocalDeviceContext localDevice) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(new RealtimeCredentialBundle(
                localDevice.DeviceId,
                localDevice.DeviceName,
                "test-ui-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "https://127.0.0.1:7443",
                null)));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => GetCredentialsAsync(cancellationToken);
    }

    private sealed class InstantOfflineRouteProber : IRouteProber
    {
        public Task<RouteProbeResult> ProbeAsync(
            DeviceEndpointRecord endpoint,
            NetworkEnvironmentSnapshot networkEnvironment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RouteProbeResult
            {
                EndpointId = endpoint.EndpointId,
                RouteKind = endpoint.RouteKind,
                EndpointUrl = endpoint.EndpointUrl,
                IsReachable = false,
                LatencyMs = null,
                FailureReason = RouteFailureReason.HostUnreachable,
                DiagnosticMessage = "Teste offline determinístico",
                ProbedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    private sealed class InstantTransferTransport : IFileTransferTransport
    {
        public Task<Result<InitiateFileTransferResponse>> InitiateTransferAsync(InitiateFileTransferRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<InitiateFileTransferResponse>.Failure(new NexusError("TEST_OFFLINE", "Transporte de teste offline.")));

        public Task<Result<ChunkUploadResponse>> UploadChunkAsync(Guid transferId, long offset, Stream chunkStream, long chunkLength, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<ChunkUploadResponse>.Failure(new NexusError("TEST_OFFLINE", "Transporte de teste offline.")));

        public Task<Result<FileTransferRecord>> FinalizeUploadAsync(Guid transferId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord>.Failure(new NexusError("TEST_OFFLINE", "Transporte de teste offline.")));

        public Task<Result<long>> DownloadToPartFileAsync(FileTransferRecord transfer, string partFilePath, long existingOffsetBytes, Action<long, double>? progressCallback, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<long>.Failure(new NexusError("TEST_OFFLINE", "Transporte de teste offline.")));

        public Task<Result<FileTransferRecord>> AcknowledgeTransferAsync(AcknowledgeFileTransferRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord>.Failure(new NexusError("TEST_OFFLINE", "Transporte de teste offline.")));

        public Task<Result<FileTransferRecord?>> GetTransferStatusAsync(Guid transferId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));

        public Task<Result<IReadOnlyList<FileTransferRecord>>> ListIncomingTransfersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<FileTransferRecord>>.Success(Array.Empty<FileTransferRecord>()));

        public Task<Result<FileTransferRecord?>> NotifyPauseAsync(Guid transferId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));

        public Task<Result<FileTransferRecord?>> NotifyCancelAsync(Guid transferId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));
    }

    private sealed class ControllableRealtimeChannel : IRealtimeClientChannel
    {
        public TransportKind Transport => TransportKind.SignalR;
        public bool IsConnected { get; private set; }
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public RealtimeSessionSnapshot? CurrentSession { get; private set; }
        public IReadOnlyList<DevicePresenceSnapshot> LivePresence { get; private set; } = Array.Empty<DevicePresenceSnapshot>();
        public IReadOnlyList<RealtimeMessageEnvelope> RecentMessages { get; private set; } = Array.Empty<RealtimeMessageEnvelope>();
        public IReadOnlyList<RealtimeMessageAck> RecentAcknowledgements { get; private set; } = Array.Empty<RealtimeMessageAck>();
        public string? LastError { get; private set; }
        public DateTimeOffset? LastHeartbeatUtc { get; private set; }

        public event EventHandler<ConnectionStateSnapshot>? StateChanged;
        public event EventHandler<RealtimeMessageEnvelope>? MessageReceived;
        public event EventHandler<RealtimeMessageAck>? AckReceived;
#pragma warning disable CS0067
        public event EventHandler<DeviceCommand>? CommandReceived;
        public event EventHandler<CommandCancellationRequest>? CommandCancelled;
        public event EventHandler<CommandExecutionResult>? CommandResultReceived;
        public event EventHandler<ClipboardEntryContract>? ClipboardReceived;
        public event EventHandler<NexusNoteContract>? NoteChangeReceived;
        public event EventHandler<FileTransferProgressEvent>? TransferProgressReceived;
        public event EventHandler<FileTransferRecord>? TransferAvailableReceived;
#pragma warning restore CS0067
        public event EventHandler<IReadOnlyList<DevicePresenceSnapshot>>? PresenceUpdated;
        public event EventHandler<MobileNotificationRecord>? MobileNotificationReceived;
        public event EventHandler<MediaPlaybackStateContract>? MediaStateReceived;
        public event EventHandler<IntegrationModuleStatusSnapshot>? ModuleStatusChangedReceived;
        public event EventHandler<PanelProfile>? PanelProfileUpdatedReceived;
        public event EventHandler<AutomationExecutionRecord>? AutomationExecutionReceived;

        public void RaiseStateChanged(ConnectionStateSnapshot snapshot)
        {
            State = snapshot.State;
            IsConnected = snapshot.State == ConnectionState.Connected;
            StateChanged?.Invoke(this, snapshot);
        }

        public void RaisePresenceUpdated(IReadOnlyList<DevicePresenceSnapshot> items)
        {
            LivePresence = items;
            PresenceUpdated?.Invoke(this, items);
        }

        public void RaiseMessageReceived(RealtimeMessageEnvelope envelope)
        {
            RecentMessages = RecentMessages.Prepend(envelope).Take(50).ToArray();
            MessageReceived?.Invoke(this, envelope);
        }

        public void RaiseAckReceived(RealtimeMessageAck ack)
        {
            RecentAcknowledgements = RecentAcknowledgements.Prepend(ack).Take(50).ToArray();
            AckReceived?.Invoke(this, ack);
        }

        public void RaiseMobileNotificationReceived(MobileNotificationRecord record)
            => MobileNotificationReceived?.Invoke(this, record);

        public void RaiseMediaStateReceived(MediaPlaybackStateContract state)
            => MediaStateReceived?.Invoke(this, state);

        public void RaiseModuleStatusChangedReceived(IntegrationModuleStatusSnapshot status)
            => ModuleStatusChangedReceived?.Invoke(this, status);

        public void RaisePanelProfileUpdatedReceived(PanelProfile profile)
            => PanelProfileUpdatedReceived?.Invoke(this, profile);

        public void RaiseAutomationExecutionReceived(AutomationExecutionRecord record)
            => AutomationExecutionReceived?.Invoke(this, record);

        public Task<Result> OpenAsync(string endpoint, CancellationToken ct = default)
        {
            IsConnected = true;
            State = ConnectionState.Connected;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> CloseAsync(CancellationToken ct = default)
        {
            IsConnected = false;
            State = ConnectionState.Disconnected;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(NexusRequest<TRequest> request, CancellationToken ct = default)
            => Task.FromResult(Result<NexusResponse<TResponse>>.Failure(new NexusError("TEST_UNSUPPORTED", "Não utilizado no teste.")));

        public Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<int>> SendHeartbeatAsync(CancellationToken ct = default)
        {
            LastHeartbeatUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(Result<int>.Success(4));
        }

        public Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(string? endpointOverride = null, CancellationToken ct = default)
        {
            IsConnected = true;
            State = ConnectionState.Connected;
            CurrentSession = new RealtimeSessionSnapshot
            {
                ConnectionId = "test-conn",
                DeviceId = Guid.NewGuid(),
                DeviceName = "TestHost",
                State = ConnectionState.Connected,
                Endpoint = endpointOverride ?? "https://127.0.0.1:7443/hubs/nexus",
                ConnectedAtUtc = DateTimeOffset.UtcNow,
                LastActivityAtUtc = DateTimeOffset.UtcNow
            };
            return Task.FromResult(Result<RealtimeSessionSnapshot>.Success(CurrentSession));
        }

        public Task<Result<RealtimeMessageAck>> SendEnvelopeAsync(RealtimeMessageEnvelope envelope, bool waitForAck = true, TimeSpan? ackTimeout = null, CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Success(new RealtimeMessageAck
            {
                MessageId = envelope.MessageId,
                RespondingDeviceId = envelope.TargetDeviceId ?? envelope.SourceDeviceId,
                Status = RealtimeAckStatus.Processed
            }));

        public Task<Result<RealtimeMessageAck>> SendCommandAsync(DeviceCommand command, bool waitForAck = true, CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Success(new RealtimeMessageAck
            {
                MessageId = Guid.CreateVersion7(),
                RespondingDeviceId = command.TargetDeviceId,
                Status = RealtimeAckStatus.Processed
            }));

        public Task<Result> CancelCommandAsync(Guid commandId, Guid targetDeviceId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> SendCommandResultAsync(CommandExecutionResult result, Guid? targetDeviceId = null, Guid? correlationId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> AcknowledgeAsync(RealtimeMessageAck ack, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<RealtimeMessageAck>> PublishIntegrationEventAsync(IntegrationEvent integrationEvent, Guid? targetDeviceId = null, bool ordered = true, CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Success(new RealtimeMessageAck
            {
                MessageId = Guid.CreateVersion7(),
                RespondingDeviceId = targetDeviceId ?? Guid.NewGuid(),
                Status = RealtimeAckStatus.Processed
            }));

        public Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<DevicePresenceSnapshot>>.Success(LivePresence));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
