using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
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
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Sync;
using Nexus.Core.Transfers;
using Nexus.Desktop.Services;
using Nexus.Desktop.Services.DependencyInjection;
using Nexus.Desktop.Services.Mvvm;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Modules.BuiltIn;
using Xunit;

namespace Nexus.Desktop.Tests;

public sealed class WpfCollectionViewThreadTests : IClassFixture<WpfStaDispatcherFixture>, IDisposable
{
    private readonly WpfStaDispatcherFixture _fixture;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-wpf-tests-" + Guid.NewGuid().ToString("N"));

    public WpfCollectionViewThreadTests(WpfStaDispatcherFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task WpfUiDispatcher_MarshalsToExplicitStaDispatcher_AndHandlesShutdownGracefully()
    {
        Dispatcher? staDispatcher = null;
        int staThreadId = 0;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            staThreadId = Environment.CurrentManagedThreadId;
            staDispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(staDispatcher));
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();

        var wrapper = new WpfUiDispatcher(staDispatcher!);

        Dispatcher? backgroundSeenDispatcher = null;
        bool? backgroundCheckAccess = null;
        int executedThreadId = 0;

        await Task.Run(async () =>
        {
            // Confirm ThreadPool threads do not receive a synthetic Dispatcher from WpfUiDispatcher
            backgroundSeenDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
            backgroundCheckAccess = wrapper.CheckAccess();

            await wrapper.InvokeAsync(() =>
            {
                executedThreadId = Environment.CurrentManagedThreadId;
                Assert.True(wrapper.CheckAccess());
            });
        });

        Assert.Null(backgroundSeenDispatcher);
        Assert.False(backgroundCheckAccess);
        Assert.Equal(staThreadId, executedThreadId);

        // Shut down STA dispatcher and confirm subsequent calls remain safe no-ops
        staDispatcher!.BeginInvokeShutdown(DispatcherPriority.Normal);
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        wrapper.Enqueue(() => { });
        await wrapper.InvokeAsync(() => { });
    }

    [Fact]
    public async Task RealWpfCollectionViews_AndMainWindow_HandleStartupAsyncOperationsBackgroundEventsAndAllSectionNavigation()
    {
        _fixture.ClearUnhandledExceptions();
        var realtime = new TestRealtimeChannel();

        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_tempDir, "wpf-shell");
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false,
            configureLocalDevice: d =>
            {
                d.DeviceName = "PC WPF Integrado";
                d.Platform = DevicePlatform.Windows;
                d.DeviceType = DeviceType.Desktop;
            });
        services.AddNexusModules();

        services.Replace(ServiceDescriptor.Singleton<IUiDispatcher>(_fixture.UiDispatcher));
        services.Replace(ServiceDescriptor.Singleton<IRealtimeClientChannel>(realtime));
        services.Replace(ServiceDescriptor.Singleton<IRealtimeCredentialProvider>(
            sp => new TestCredentialProvider(sp.GetRequiredService<ILocalDeviceContext>())));
        services.Replace(ServiceDescriptor.Singleton<IRouteProber, OfflineRouteProber>());
        services.Replace(ServiceDescriptor.Singleton<IFileTransferTransport, OfflineTransferTransport>());
        services.Replace(ServiceDescriptor.Singleton<ISystemClipboardAdapter, InMemoryClipboardAdapter>());
        services.AddSingleton<IUiDispatcher>(_fixture.UiDispatcher);
        services.AddNexusDesktopServices();

        await using var provider = services.BuildServiceProvider();
        MainShellViewModel shell = null!;
        MainWindow? mainWindow = null;

        // Bind real WPF CollectionViews and instantiate MainWindow BEFORE InitializeAsync() completes
        // (reproduces the exact real-world window startup race that previously threw CollectionView cross-thread errors).
        await _fixture.UiDispatcher.InvokeAsync(() =>
        {
            shell = provider.GetRequiredService<MainShellViewModel>();

            AttachWpfCollectionViews(shell);

            mainWindow = new MainWindow
            {
                DataContext = shell,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Width = 960,
                Height = 640
            };
            mainWindow.Show();
        });

        try
        {
            // 1. Initialize all ViewModels concurrently after MainWindow + CollectionViews are already bound
            await Task.Run(() => shell.InitializeAsync());

            var localId = provider.GetRequiredService<ILocalDeviceContext>().DeviceId;
            var peerId = Guid.NewGuid();
            var deviceRegistry = provider.GetRequiredService<IDeviceRegistry>();
            var trustedRepo = provider.GetRequiredService<ITrustedDeviceRepository>();
            var notifyService = provider.GetRequiredService<INexusNotifyService>();
            var transferManager = provider.GetRequiredService<ITransferManager>();
            var stateManager = provider.GetRequiredService<IStateManager<NexusRuntimeState>>();

            var sampleFile = Path.Combine(_tempDir, "wpf-upload.bin");
            await File.WriteAllBytesAsync(sampleFile, new byte[1024]);

            // 2. Execute async ViewModel commands + ThreadPool background events simultaneously while navigating WPF tabs
            var backgroundOperations = Task.Run(async () =>
            {
                await _fixture.UiDispatcher.InvokeAsync(() =>
                {
                    shell.Connectivity.NewEndpointLabel = "WPF Route";
                    shell.Connectivity.NewEndpointUrl = "https://192.168.10.25:7443";
                    shell.Sync.EditorTitle = "Nota WPF";
                    shell.Sync.EditorBody = "Atualizada em CollectionView";
                });

                await shell.Connectivity.AddEndpointCommand.ExecuteAsync();
                await shell.Sync.SaveNoteCommand.ExecuteAsync();
                await shell.Modules.RefreshAllCommand.ExecuteAsync();
                await shell.Modules.RefreshMonitorCommand.ExecuteAsync();

                var now = DateTimeOffset.UtcNow;
                await deviceRegistry.RegisterOrUpdateAsync(new DeviceRegistrationContract
                {
                    DeviceId = peerId,
                    Name = "Android WPF Peer",
                    Platform = DevicePlatform.Android,
                    Type = DeviceType.Phone,
                    OperatingSystemVersion = "Android 15",
                    AppVersion = "1.0.0",
                    RequestedAtUtc = now
                });
                await trustedRepo.UpsertAsync(new TrustedDeviceRecord
                {
                    DeviceId = peerId,
                    CustomName = "Android WPF Peer",
                    Platform = DevicePlatform.Android,
                    DeviceType = DeviceType.Phone,
                    OperatingSystemVersion = "Android 15",
                    AppVersion = "1.0.0",
                    PublicKeySpkiBase64 = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAvWpfTest",
                    PublicKeyFingerprint = peerId.ToString("N") + peerId.ToString("N"),
                    Permissions = NexusPermission.All,
                    TrustStatus = DeviceTrustStatus.Authorized,
                    PairedAtUtc = now,
                    LastAccessAtUtc = now
                });
                await shell.Devices.LoadDevicesCommand.ExecuteAsync();

                for (int i = 0; i < 6; i++)
                {
                    realtime.RaiseMessageReceived(new RealtimeMessageEnvelope
                    {
                        SourceDeviceId = peerId,
                        TargetDeviceId = localId,
                        MessageType = $"wpf.event.{i}",
                        PayloadJson = "{\"wpf\":true}",
                        SequenceNumber = i + 1
                    });

                    realtime.RaiseAckReceived(new RealtimeMessageAck
                    {
                        MessageId = Guid.CreateVersion7(),
                        RespondingDeviceId = peerId,
                        Status = RealtimeAckStatus.Processed
                    });

                    realtime.RaiseMobileNotificationReceived(new MobileNotificationRecord
                    {
                        NotificationId = Guid.NewGuid(),
                        SourceDeviceId = peerId,
                        AppName = "WPFMonitor",
                        Title = $"Notif WPF {i}",
                        Body = "Coleção observada por WPF",
                        ForwardedToDesktop = true,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ReceivedAtUtc = DateTimeOffset.UtcNow
                    });

                    await notifyService.IngestAsync(new PushMobileNotificationRequest
                    {
                        SourceDeviceId = peerId,
                        AppName = "WhatsApp",
                        Title = $"Persistida {i}",
                        Body = "Teste real WPF"
                    });
                }

                await transferManager.EnqueueSendFileAsync(peerId, "Android WPF Peer", sampleFile);

                stateManager.Update(s => s with
                {
                    ConnectionState = ConnectionState.Connected,
                    KnownDevicesCount = 2
                });
            });

            var navigationTask = Task.Run(async () =>
            {
                foreach (var section in Enum.GetValues<DesktopSection>())
                {
                    await _fixture.UiDispatcher.InvokeAsync(() =>
                    {
                        shell.ActiveSection = section;
                        mainWindow?.UpdateLayout();
                    });
                    await Task.Delay(5);
                }
            });

            await Task.WhenAll(backgroundOperations, navigationTask);
            await _fixture.UiDispatcher.InvokeAsync(() => mainWindow?.UpdateLayout());

            Assert.Empty(_fixture.UnhandledExceptions);
            Assert.False(shell.IsInitializing);
            Assert.Equal(11, shell.NavigationTabs.Count);
            Assert.NotEmpty(shell.Connectivity.Endpoints);
            Assert.NotEmpty(shell.Sync.Notes);
            Assert.NotEmpty(shell.Realtime.RecentMessages);
            Assert.NotEmpty(shell.Modules.NotificationFeed);
            Assert.NotEmpty(shell.Devices.TrustedDeviceItems);
            Assert.True(shell.Transfers.ActiveTransfers.Count + shell.Transfers.HistoryItems.Count >= 1);
        }
        finally
        {
            await _fixture.UiDispatcher.InvokeAsync(() =>
            {
                mainWindow?.Close();
            });
        }
    }

    private static void AttachWpfCollectionViews(MainShellViewModel shell)
    {
        IEnumerable<INotifyCollectionChanged> collections =
        [
            shell.Dashboard.RecentActivities,
            shell.Devices.Devices,
            shell.Devices.TrustedDevices,
            shell.Devices.TrustedDeviceItems,
            shell.SystemStatus.Modules,
            shell.SystemStatus.RecentLogs,
            shell.Connectivity.Endpoints,
            shell.Realtime.PresenceItems,
            shell.Realtime.RecentMessages,
            shell.Realtime.RecentAcks,
            shell.Sync.ClipboardHistory,
            shell.Sync.Notes,
            shell.Sync.SelectedNoteRevisions,
            shell.Sync.UnresolvedConflicts,
            shell.Transfers.PendingSelectedFiles,
            shell.Transfers.AuthorizedPeers,
            shell.Transfers.ActiveTransfers,
            shell.Transfers.ReceivedFiles,
            shell.Transfers.HistoryItems,
            shell.Modules.ModuleStatuses,
            shell.Modules.Hotkeys,
            shell.Modules.NotificationFeed,
            shell.Modules.Disks,
            shell.Modules.NetworkInterfaces,
            shell.Modules.TopProcesses,
            shell.Automation.Profiles,
            shell.Automation.ActiveWidgets,
            shell.Automation.ApprovedApps,
            shell.Automation.Rules,
            shell.Automation.ExecutionHistory,
            shell.Automation.ActiveIndicators
        ];

        foreach (var collection in collections)
        {
            _ = CollectionViewSource.GetDefaultView(collection);
        }
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
            // Ignore temp SQLite locks during cleanup
        }
    }

    private sealed class InMemoryClipboardAdapter : ISystemClipboardAdapter
    {
        private string _text = "clipboard-wpf";
        public bool SupportsContinuousBackgroundMonitor => true;
        public string PlatformRestrictionNotice => "Teste WPF em memória";
#pragma warning disable CS0067
        public event EventHandler<string>? SystemClipboardTextCaptured;
#pragma warning restore CS0067
        public Task<Result<string?>> ReadTextAsync(CancellationToken ct = default) => Task.FromResult(Result<string?>.Success(_text));
        public Task<Result> WriteTextAsync(string text, CancellationToken ct = default)
        {
            _text = text;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class TestCredentialProvider(ILocalDeviceContext localDevice) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(new RealtimeCredentialBundle(
                localDevice.DeviceId,
                localDevice.DeviceName,
                "test-wpf-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "https://127.0.0.1:7443",
                null)));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken ct = default)
            => GetCredentialsAsync(ct);
    }

    private sealed class OfflineRouteProber : IRouteProber
    {
        public Task<RouteProbeResult> ProbeAsync(DeviceEndpointRecord endpoint, NetworkEnvironmentSnapshot env, CancellationToken ct = default)
            => Task.FromResult(new RouteProbeResult
            {
                EndpointId = endpoint.EndpointId,
                RouteKind = endpoint.RouteKind,
                EndpointUrl = endpoint.EndpointUrl,
                IsReachable = false,
                FailureReason = RouteFailureReason.HostUnreachable,
                DiagnosticMessage = "Offline deterministic WPF probe"
            });
    }

    private sealed class OfflineTransferTransport : IFileTransferTransport
    {
        public Task<Result<InitiateFileTransferResponse>> InitiateTransferAsync(InitiateFileTransferRequest request, CancellationToken ct = default)
            => Task.FromResult(Result<InitiateFileTransferResponse>.Failure(new NexusError("OFFLINE", "Teste offline")));
        public Task<Result<ChunkUploadResponse>> UploadChunkAsync(Guid transferId, long offset, Stream chunkStream, long chunkLength, CancellationToken ct = default)
            => Task.FromResult(Result<ChunkUploadResponse>.Failure(new NexusError("OFFLINE", "Teste offline")));
        public Task<Result<FileTransferRecord>> FinalizeUploadAsync(Guid transferId, CancellationToken ct = default)
            => Task.FromResult(Result<FileTransferRecord>.Failure(new NexusError("OFFLINE", "Teste offline")));
        public Task<Result<long>> DownloadToPartFileAsync(FileTransferRecord transfer, string partFilePath, long existingOffsetBytes, Action<long, double>? progressCallback, CancellationToken ct = default)
            => Task.FromResult(Result<long>.Failure(new NexusError("OFFLINE", "Teste offline")));
        public Task<Result<FileTransferRecord>> AcknowledgeTransferAsync(AcknowledgeFileTransferRequest request, CancellationToken ct = default)
            => Task.FromResult(Result<FileTransferRecord>.Failure(new NexusError("OFFLINE", "Teste offline")));
        public Task<Result<FileTransferRecord?>> GetTransferStatusAsync(Guid transferId, CancellationToken ct = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));
        public Task<Result<IReadOnlyList<FileTransferRecord>>> ListIncomingTransfersAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<FileTransferRecord>>.Success(Array.Empty<FileTransferRecord>()));
        public Task<Result<FileTransferRecord?>> NotifyPauseAsync(Guid transferId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));
        public Task<Result<FileTransferRecord?>> NotifyCancelAsync(Guid transferId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));
    }

    private sealed class TestRealtimeChannel : IRealtimeClientChannel
    {
        public TransportKind Transport => TransportKind.SignalR;
        public bool IsConnected => true;
        public ConnectionState State => ConnectionState.Connected;
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
        public event EventHandler<PanelProfile>? PanelProfileUpdatedReceived;
        public event EventHandler<AutomationExecutionRecord>? AutomationExecutionReceived;
#pragma warning restore CS0067

        public void RaiseMessageReceived(RealtimeMessageEnvelope envelope) => MessageReceived?.Invoke(this, envelope);
        public void RaiseAckReceived(RealtimeMessageAck ack) => AckReceived?.Invoke(this, ack);
        public void RaiseMobileNotificationReceived(MobileNotificationRecord record) => MobileNotificationReceived?.Invoke(this, record);

        public Task<Result> OpenAsync(string endpoint, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> CloseAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(NexusRequest<TRequest> request, CancellationToken ct = default)
            => Task.FromResult(Result<NexusResponse<TResponse>>.Failure(new NexusError("TEST", "N/A")));
        public Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<int>> SendHeartbeatAsync(CancellationToken ct = default) => Task.FromResult(Result<int>.Success(2));
        public Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(string? endpointOverride = null, CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeSessionSnapshot>.Failure(new NexusError("TEST", "N/A")));
        public Task<Result<RealtimeMessageAck>> SendEnvelopeAsync(RealtimeMessageEnvelope envelope, bool waitForAck = true, TimeSpan? ackTimeout = null, CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Failure(new NexusError("TEST", "N/A")));
        public Task<Result<RealtimeMessageAck>> SendCommandAsync(DeviceCommand command, bool waitForAck = true, CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Failure(new NexusError("TEST", "N/A")));
        public Task<Result> CancelCommandAsync(Guid commandId, Guid targetDeviceId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
        public Task<Result> SendCommandResultAsync(CommandExecutionResult result, Guid? targetDeviceId = null, Guid? correlationId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
        public Task<Result> AcknowledgeAsync(RealtimeMessageAck ack, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result<RealtimeMessageAck>> PublishIntegrationEventAsync(IntegrationEvent integrationEvent, Guid? targetDeviceId = null, bool ordered = true, CancellationToken ct = default)
            => Task.FromResult(Result<RealtimeMessageAck>.Failure(new NexusError("TEST", "N/A")));
        public Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<DevicePresenceSnapshot>>.Success(Array.Empty<DevicePresenceSnapshot>()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class WpfStaDispatcherFixture : IDisposable
{
    private readonly Thread _staThread;
    private readonly List<Exception> _unhandled = new();
    private readonly Lock _sync = new();

    public Dispatcher StaDispatcher { get; }
    public WpfUiDispatcher UiDispatcher { get; }

    public IReadOnlyList<Exception> UnhandledExceptions
    {
        get
        {
            lock (_sync)
            {
                return _unhandled.ToArray();
            }
        }
    }

    public WpfStaDispatcherFixture()
    {
        Dispatcher? capturedDispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        _staThread = new Thread(() =>
        {
            capturedDispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(capturedDispatcher));
            capturedDispatcher.UnhandledException += (_, args) =>
            {
                lock (_sync)
                {
                    _unhandled.Add(args.Exception);
                }
                args.Handled = true;
            };

            if (Application.Current is null)
            {
                var app = new Nexus.Desktop.App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                app.InitializeComponent();
            }

            ready.Set();
            Dispatcher.Run();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Name = "WpfCollectionViewTestStaThread";
        _staThread.Start();
        ready.Wait();

        StaDispatcher = capturedDispatcher!;
        UiDispatcher = new WpfUiDispatcher(StaDispatcher);
    }

    public void ClearUnhandledExceptions()
    {
        lock (_sync)
        {
            _unhandled.Clear();
        }
    }

    public void Dispose()
    {
        StaDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        _staThread.Join(TimeSpan.FromSeconds(5));
    }
}

