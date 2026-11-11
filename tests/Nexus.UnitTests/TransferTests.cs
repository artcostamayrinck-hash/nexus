using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Sync;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Transfers;
using Nexus.Mobile.Services.ViewModels;
using Xunit;

namespace Nexus.UnitTests;

public sealed class TransferTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-transfer-unit-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil.exe")]
    [InlineData("..\\evil.exe")]
    [InlineData("sub/nested.txt")]
    [InlineData("sub\\nested.txt")]
    [InlineData("C:\\secret.txt")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("bad\0name.txt")]
    public void SafeTransferStorageProvider_RejectsPathTraversalAndUnsafeFileNames(string unsafeName)
    {
        var storage = CreateStorage();

        var sanitized = storage.SanitizeFileName(unsafeName);
        Assert.True(sanitized.IsFailure, $"Expected rejection for '{unsafeName}'.");

        var dest = storage.ResolveSafeReceivedDestination(unsafeName, TransferDuplicatePolicy.Rename);
        Assert.True(dest.IsFailure);

        var staged = storage.ResolveSafeStagingPath(Guid.NewGuid(), unsafeName, isPartFile: true);
        Assert.True(staged.IsFailure);
    }

    [Fact]
    public void FileTransferRecord_ForbidsAutoExecute()
    {
        var record = new FileTransferRecord
        {
            TransferId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            SourceDeviceId = Guid.NewGuid(),
            TargetDeviceId = Guid.NewGuid(),
            OriginalFileName = "installer.msi",
            SafeFileName = "installer.msi",
            TotalBytes = 1024,
            Sha256Checksum = new string('a', 64),
            AutoExecute = true
        };

        var errors = record.Validate().ToArray();
        Assert.Contains(errors, e => e.Code == "AUTO_EXECUTE_FORBIDDEN");
    }

    [Fact]
    public void SafeTransferStorageProvider_ResolvesDuplicatePolicies_Correctly()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(storage.ReceivedDirectory);

        var firstPath = Path.Combine(storage.ReceivedDirectory, "report.pdf");
        File.WriteAllText(firstPath, "v1");

        // Rename produces report (1).pdf, then report (2).pdf if (1) exists
        var renamed1 = storage.ResolveSafeReceivedDestination("report.pdf", TransferDuplicatePolicy.Rename);
        Assert.True(renamed1.IsSuccess);
        Assert.False(renamed1.Value.ShouldSkip);
        Assert.Equal("report (1).pdf", renamed1.Value.ResolvedFileName);
        File.WriteAllText(renamed1.Value.FullPath, "v2");

        var renamed2 = storage.ResolveSafeReceivedDestination("report.pdf", TransferDuplicatePolicy.Rename);
        Assert.True(renamed2.IsSuccess);
        Assert.Equal("report (2).pdf", renamed2.Value.ResolvedFileName);

        // Overwrite keeps exact name
        var overwrite = storage.ResolveSafeReceivedDestination("report.pdf", TransferDuplicatePolicy.Overwrite);
        Assert.True(overwrite.IsSuccess);
        Assert.False(overwrite.Value.ShouldSkip);
        Assert.Equal(firstPath, overwrite.Value.FullPath);

        // Skip indicates ShouldSkip = true
        var skip = storage.ResolveSafeReceivedDestination("report.pdf", TransferDuplicatePolicy.Skip);
        Assert.True(skip.IsSuccess);
        Assert.True(skip.Value.ShouldSkip);
    }

    [Fact]
    public void SafeTransferStorageProvider_RejectsInsufficientDiskSpace()
    {
        long simulatedFree = 1024;
        var storage = CreateStorage(new TransferStoragePolicyOptions
        {
            AvailableFreeSpaceProvider = _ => simulatedFree
        });

        var denied = storage.CheckAvailableDiskSpace(storage.ReceivedDirectory, requiredBytes: 2048, safetyMarginBytes: 0);
        Assert.True(denied.IsFailure);
        Assert.Equal("INSUFFICIENT_DISK_SPACE", denied.Error!.Code);

        var marginDenied = storage.CheckAvailableDiskSpace(storage.ReceivedDirectory, requiredBytes: 800, safetyMarginBytes: 512);
        Assert.True(marginDenied.IsFailure);
        Assert.Equal("INSUFFICIENT_DISK_SPACE", marginDenied.Error!.Code);

        var allowed = storage.CheckAvailableDiskSpace(storage.ReceivedDirectory, requiredBytes: 512, safetyMarginBytes: 512);
        Assert.True(allowed.IsSuccess);
    }

    [Fact]
    public async Task SafeTransferStorageProvider_DetectsCorruptedSha256AndCleansPartFile()
    {
        var storage = CreateStorage();
        var transferId = Guid.NewGuid();
        var payload = "Integridade NEXUS 100%"u8.ToArray();
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));

        var partPath = storage.ResolveSafeStagingPath(transferId, "payload.bin", isPartFile: true).Value;
        await using (var ms = new MemoryStream("Integridade CORROMPIDA"u8.ToArray()))
        {
            var written = await storage.WriteChunkAtOffsetAsync(partPath, 0, ms);
            Assert.True(written.IsSuccess);
        }

        var dest = storage.ResolveSafeReceivedDestination("payload.bin", TransferDuplicatePolicy.Rename).Value;
        var finalized = await storage.FinalizeVerifiedFileAsync(
            partPath,
            dest.FullPath,
            expectedHash,
            TransferDuplicatePolicy.Rename);

        Assert.True(finalized.IsFailure);
        Assert.Equal("CHECKSUM_MISMATCH", finalized.Error!.Code);
        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(dest.FullPath));
    }

    [Fact]
    public async Task TransferManager_EnforcesConfiguredMaxConcurrencyLimit_AndCompletesQueue()
    {
        await using var ctx = await CreateManagerHarnessAsync("conc-unit");
        var targetId = Guid.CreateVersion7();

        await ctx.Manager.UpdateConfigurationAsync(new TransferQueueOptions
        {
            MaxConcurrentTransfers = 2,
            DefaultChunkSizeBytes = 16 * 1024
        });

        var currentInFlight = 0;
        var peakInFlight = 0;
        var gateLock = new object();

        ctx.Transport.DelayOnChunk = TimeSpan.FromMilliseconds(35);
        ctx.Transport.OnChunkStarted = () =>
        {
            lock (gateLock)
            {
                currentInFlight++;
                if (currentInFlight > peakInFlight)
                {
                    peakInFlight = currentInFlight;
                }
            }
        };
        ctx.Transport.OnChunkFinished = () =>
        {
            lock (gateLock)
            {
                currentInFlight--;
            }
        };

        var files = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(ctx.WorkingDir, $"batch-{i}.dat");
            await File.WriteAllBytesAsync(path, new byte[48 * 1024]);
            files.Add(path);
        }

        var enqueue = await ctx.Manager.EnqueueSendFilesAsync(targetId, "Android Pixel", files);
        Assert.True(enqueue.IsSuccess, enqueue.Error?.Message);
        Assert.Equal(5, enqueue.Value.Count);

        await WaitUntilAsync(() => ctx.Transport.FinalizedTransfers.Count == 5, timeoutMs: 8000);

        Assert.InRange(peakInFlight, 1, 2);
        Assert.Equal(5, ctx.Transport.FinalizedTransfers.Count);
    }

    [Fact]
    public async Task TransferManager_PauseResumeAndCancel_PreserveStateAndCleanPartFiles()
    {
        await using var ctx = await CreateManagerHarnessAsync("pause-unit");
        var targetId = Guid.CreateVersion7();

        await ctx.Manager.UpdateConfigurationAsync(new TransferQueueOptions
        {
            MaxConcurrentTransfers = 1,
            DefaultChunkSizeBytes = 8 * 1024
        });

        var firstChunkUploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Transport.DelayOnChunk = TimeSpan.FromMilliseconds(40);
        ctx.Transport.OnChunkFinished = () => firstChunkUploaded.TrySetResult();

        var sourceFile = Path.Combine(ctx.WorkingDir, "resumable.bin");
        var raw = new byte[64 * 1024];
        Random.Shared.NextBytes(raw);
        await File.WriteAllBytesAsync(sourceFile, raw);

        var enqueued = await ctx.Manager.EnqueueSendFileAsync(targetId, "Android", sourceFile);
        Assert.True(enqueued.IsSuccess);
        var transferId = enqueued.Value.TransferId;

        await firstChunkUploaded.Task.WaitAsync(TimeSpan.FromSeconds(4));

        // Pause after partial upload
        var paused = await ctx.Manager.PauseTransferAsync(transferId);
        Assert.True(paused.IsSuccess);
        Assert.Equal(TransferState.Paused, paused.Value.State);

        await Task.Delay(80);
        var uploadedBeforeResume = ctx.Transport.GetUploadedBytes(transferId);
        Assert.InRange(uploadedBeforeResume, 8 * 1024, 63 * 1024);

        // Resume finishes remaining chunks without restarting from 0
        ctx.Transport.DelayOnChunk = TimeSpan.Zero;
        var resumed = await ctx.Manager.ResumeTransferAsync(transferId);
        Assert.True(resumed.IsSuccess);

        await WaitUntilAsync(() => ctx.Transport.FinalizedTransfers.Contains(transferId));
        var uploadedBytes = ctx.Transport.GetStagedBytes(transferId);
        Assert.Equal(raw, uploadedBytes);

        // Now test cancel on a queued download with existing .part
        var cancelId = Guid.NewGuid();
        var partPath = ctx.Storage.ResolveSafeStagingPath(cancelId, "to-cancel.bin", isPartFile: true).Value;
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        await File.WriteAllBytesAsync(partPath, new byte[1024]);
        Assert.True(File.Exists(partPath));

        await ctx.Manager.EnqueueReceiveTransferAsync(new FileTransferRecord
        {
            TransferId = cancelId,
            SourceDeviceId = targetId,
            TargetDeviceId = ctx.DeviceId,
            OriginalFileName = "to-cancel.bin",
            SafeFileName = "to-cancel.bin",
            TotalBytes = 4096,
            Sha256Checksum = new string('b', 64)
        }, startImmediately: false);

        var cancelled = await ctx.Manager.CancelTransferAsync(cancelId, "Teste cancelamento");
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(TransferState.Cancelled, cancelled.Value.State);
        Assert.False(File.Exists(partPath));
    }

    [Fact]
    public async Task DesktopAndMobileViewModels_ManageMultiFileSelectionSettingsAndHistory()
    {
        await using var ctx = await CreateManagerHarnessAsync("vm-unit");
        var peerId = Guid.CreateVersion7();

        await ctx.TrustedRepo.UpsertAsync(new TrustedDeviceRecord
        {
            DeviceId = peerId,
            CustomName = "Galaxy S25",
            Platform = DevicePlatform.Android,
            DeviceType = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "0.7.0",
            PublicKeySpkiBase64 = "pk-test-base64",
            PublicKeyFingerprint = new string('a', 64),
            TrustStatus = DeviceTrustStatus.Authorized,
            Permissions = NexusPermission.All,
            PairedAtUtc = _clock.UtcNow,
            LastAccessAtUtc = _clock.UtcNow
        });

        var sample1 = Path.Combine(ctx.WorkingDir, "foto.png");
        var sample2 = Path.Combine(ctx.WorkingDir, "documento.pdf");
        await File.WriteAllBytesAsync(sample1, new byte[4096]);
        await File.WriteAllBytesAsync(sample2, new byte[8192]);

        var desktopVm = new TransferViewModel(ctx.Manager, ctx.Storage, ctx.TrustedRepo, ctx.LocalDevice);
        await desktopVm.InitializeAsync();
        Assert.Single(desktopVm.AuthorizedPeers);

        desktopVm.MaxConcurrentTransfers = 4;
        desktopVm.SelectedDuplicatePolicy = TransferDuplicatePolicy.Overwrite;
        await desktopVm.ApplyQueueConfigCommand.ExecuteAsync();
        Assert.Equal(4, ctx.Manager.Options.MaxConcurrentTransfers);
        Assert.Equal(TransferDuplicatePolicy.Overwrite, ctx.Manager.Options.DefaultDuplicatePolicy);

        desktopVm.AddSelectedFiles([sample1, sample2]);
        Assert.Equal(2, desktopVm.PendingSelectedFiles.Count);

        await desktopVm.SendSelectedFilesCommand.ExecuteAsync();
        Assert.Empty(desktopVm.PendingSelectedFiles);
        await WaitUntilAsync(() => ctx.Transport.FinalizedTransfers.Count == 2);

        var mobileVm = new MobileTransferViewModel(ctx.Manager, ctx.Storage, ctx.TrustedRepo, ctx.LocalDevice);
        await mobileVm.InitializeAsync();
        Assert.Equal(2, mobileVm.HistoryItems.Count);
        Assert.Equal("4.0 KB", TransferDisplayItem.FormatBytes(4096));
    }

    private SafeTransferStorageProvider CreateStorage(TransferStoragePolicyOptions? policy = null)
    {
        var opts = new NexusStorageOptions
        {
            RootDirectory = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")),
            EnablePooling = false
        };
        return new SafeTransferStorageProvider(opts, policy);
    }

    private async Task<TransferHarness> CreateManagerHarnessAsync(string key)
    {
        var root = Path.Combine(_tempDir, key);
        Directory.CreateDirectory(root);
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);

        var storageOptions = new NexusStorageOptions
        {
            RootDirectory = root,
            DatabaseFileName = "transfer-unit.db",
            EnablePooling = false
        };
        var connFactory = new SqliteConnectionFactory(storageOptions);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var localDevice = new DefaultLocalDeviceContext(
            Guid.NewGuid(),
            "Unit Test Host",
            DevicePlatform.Windows,
            DeviceType.Desktop,
            "Windows 11",
            "0.7.0",
            DeviceCapability.All);

        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState
        {
            LocalDevice = localDevice.ToDeviceInfo(),
            UpdatedAtUtc = _clock.UtcNow
        });

        var initializer = new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance);
        Assert.True((await initializer.InitializeAsync()).IsSuccess);

        var repo = new SqliteTransferRepository(connFactory, failureHandler);
        var trustedRepo = new SqliteTrustedDeviceRepository(connFactory, failureHandler);
        var storage = new SafeTransferStorageProvider(storageOptions);
        var transport = new FakeFileTransferTransport();
        var realtime = new FakeRealtimeChannel();
        var credProvider = new FakeCredentialProvider(localDevice.DeviceId, localDevice.DeviceName);

        var manager = new TransferManagerService(
            repo,
            storage,
            transport,
            realtime,
            credProvider,
            new InProcessEventBus(),
            _clock,
            NullLogger<TransferManagerService>.Instance);

        await manager.InitializeAsync();
        return new TransferHarness(root, work, localDevice, repo, trustedRepo, storage, transport, manager);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.True(predicate(), $"Timed out after {timeoutMs}ms waiting for transfer condition.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class TransferHarness(
        string rootDir,
        string workingDir,
        ILocalDeviceContext localDevice,
        SqliteTransferRepository repository,
        SqliteTrustedDeviceRepository trustedRepo,
        SafeTransferStorageProvider storage,
        FakeFileTransferTransport transport,
        TransferManagerService manager) : IAsyncDisposable
    {
        public string RootDir { get; } = rootDir;
        public string WorkingDir { get; } = workingDir;
        public ILocalDeviceContext LocalDevice { get; } = localDevice;
        public Guid DeviceId => LocalDevice.DeviceId;
        public SqliteTransferRepository Repository { get; } = repository;
        public SqliteTrustedDeviceRepository TrustedRepo { get; } = trustedRepo;
        public SafeTransferStorageProvider Storage { get; } = storage;
        public FakeFileTransferTransport Transport { get; } = transport;
        public TransferManagerService Manager { get; } = manager;

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeCredentialProvider(Guid deviceId, string deviceName) : IRealtimeCredentialProvider
    {
        private readonly RealtimeCredentialBundle _bundle = new(deviceId, deviceName, "token", DateTimeOffset.UtcNow.AddHours(1), "https://localhost", null);
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result<RealtimeCredentialBundle>.Success(_bundle));
        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result<RealtimeCredentialBundle>.Success(_bundle));
    }

    private sealed class FakeFileTransferTransport : IFileTransferTransport
    {
        private readonly ConcurrentDictionary<Guid, FileTransferRecord> _records = new();
        private readonly ConcurrentDictionary<Guid, MemoryStream> _streams = new();
        public ConcurrentBag<Guid> FinalizedTransfers { get; } = [];
        public TimeSpan DelayOnChunk { get; set; } = TimeSpan.Zero;
        public Action? OnChunkStarted { get; set; }
        public Action? OnChunkFinished { get; set; }

        public long GetUploadedBytes(Guid transferId)
            => _streams.TryGetValue(transferId, out var ms) ? ms.Length : 0;

        public byte[] GetStagedBytes(Guid transferId)
            => _streams.TryGetValue(transferId, out var ms) ? ms.ToArray() : [];

        public Task<Result<InitiateFileTransferResponse>> InitiateTransferAsync(InitiateFileTransferRequest request, CancellationToken cancellationToken = default)
        {
            var ms = _streams.GetOrAdd(request.TransferId, _ => new MemoryStream());
            var existingOffset = ms.Length;
            _records.TryAdd(request.TransferId, new FileTransferRecord
            {
                TransferId = request.TransferId,
                SourceDeviceId = request.SourceDeviceId,
                TargetDeviceId = request.TargetDeviceId,
                OriginalFileName = request.FileName,
                SafeFileName = request.FileName,
                TotalBytes = request.TotalBytes,
                Sha256Checksum = request.Sha256Checksum,
                ChunkSizeBytes = request.ChunkSizeBytes
            });

            return Task.FromResult(Result<InitiateFileTransferResponse>.Success(new InitiateFileTransferResponse
            {
                TransferId = request.TransferId,
                SafeFileName = request.FileName,
                AcceptedOffsetBytes = existingOffset,
                ChunkSizeBytes = request.ChunkSizeBytes,
                State = TransferState.InProgress,
                Stage = TransferStage.Uploading,
                AlreadyUploadedOnServer = false,
                ServerAvailableFreeBytes = 1024L * 1024 * 1024
            }));
        }

        public async Task<Result<ChunkUploadResponse>> UploadChunkAsync(Guid transferId, long offset, Stream chunkStream, long chunkLength, CancellationToken cancellationToken = default)
        {
            OnChunkStarted?.Invoke();
            try
            {
                if (DelayOnChunk > TimeSpan.Zero)
                {
                    await Task.Delay(DelayOnChunk, cancellationToken).ConfigureAwait(false);
                }

                var ms = _streams.GetOrAdd(transferId, _ => new MemoryStream());
                lock (ms)
                {
                    ms.Seek(offset, SeekOrigin.Begin);
                    chunkStream.CopyTo(ms);
                }

                var total = _records[transferId].TotalBytes;
                return Result<ChunkUploadResponse>.Success(new ChunkUploadResponse
                {
                    TransferId = transferId,
                    UploadedBytes = ms.Length,
                    TotalBytes = total,
                    PercentComplete = total == 0 ? 100 : (double)ms.Length / total * 100d,
                    Stage = TransferStage.Uploading
                });
            }
            finally
            {
                OnChunkFinished?.Invoke();
            }
        }

        public Task<Result<FileTransferRecord>> FinalizeUploadAsync(Guid transferId, CancellationToken cancellationToken = default)
        {
            FinalizedTransfers.Add(transferId);
            var rec = _records[transferId] with
            {
                State = TransferState.Completed,
                Stage = TransferStage.Completed,
                TransferredBytes = _records[transferId].TotalBytes,
                ServerUploadedBytes = _records[transferId].TotalBytes,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            return Task.FromResult(Result<FileTransferRecord>.Success(rec));
        }

        public Task<Result<long>> DownloadToPartFileAsync(FileTransferRecord transfer, string partFilePath, long existingOffsetBytes, Action<long, double>? progressCallback, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<long>.Success(transfer.TotalBytes));

        public Task<Result<FileTransferRecord>> AcknowledgeTransferAsync(AcknowledgeFileTransferRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord>.Success(new FileTransferRecord
            {
                TransferId = request.TransferId,
                SourceDeviceId = Guid.NewGuid(),
                TargetDeviceId = Guid.NewGuid(),
                OriginalFileName = "ack.bin",
                SafeFileName = "ack.bin",
                Sha256Checksum = new string('a', 64),
                State = TransferState.Completed,
                Stage = TransferStage.Completed
            }));

        public Task<Result<FileTransferRecord?>> GetTransferStatusAsync(Guid transferId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(_records.GetValueOrDefault(transferId)));

        public Task<Result<IReadOnlyList<FileTransferRecord>>> ListIncomingTransfersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<FileTransferRecord>>.Success(Array.Empty<FileTransferRecord>()));

        public Task<Result<FileTransferRecord?>> NotifyPauseAsync(Guid transferId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));

        public Task<Result<FileTransferRecord?>> NotifyCancelAsync(Guid transferId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<FileTransferRecord?>.Success(null));
    }

    private sealed class FakeRealtimeChannel : IRealtimeClientChannel
    {
        public TransportKind Transport => TransportKind.SignalR;
        public bool IsConnected => true;
        public ConnectionState State => ConnectionState.Connected;
        public RealtimeSessionSnapshot? CurrentSession => null;
        public IReadOnlyList<DevicePresenceSnapshot> LivePresence => [];
        public IReadOnlyList<RealtimeMessageEnvelope> RecentMessages => [];
        public IReadOnlyList<RealtimeMessageAck> RecentAcknowledgements => [];
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
        public event EventHandler<Nexus.Contracts.Modules.MobileNotificationRecord>? MobileNotificationReceived;
        public event EventHandler<Nexus.Contracts.Modules.MediaPlaybackStateContract>? MediaStateReceived;
        public event EventHandler<Nexus.Contracts.Modules.IntegrationModuleStatusSnapshot>? ModuleStatusChangedReceived;
        public event EventHandler<Nexus.Contracts.Automation.PanelProfile>? PanelProfileUpdatedReceived;
        public event EventHandler<Nexus.Contracts.Automation.AutomationExecutionRecord>? AutomationExecutionReceived;
#pragma warning restore CS0067

        public Task<Result> OpenAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> CloseAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(string? endpointOverride = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(NexusRequest<TRequest> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<int>> SendHeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result<int>.Success(1));
        public Task<Result<RealtimeMessageAck>> SendEnvelopeAsync(RealtimeMessageEnvelope envelope, bool waitForAck = true, TimeSpan? ackTimeout = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<RealtimeMessageAck>> SendCommandAsync(DeviceCommand command, bool waitForAck = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> CancelCommandAsync(Guid commandId, Guid targetDeviceId, string? reason = null, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> SendCommandResultAsync(CommandExecutionResult result, Guid? targetDeviceId = null, Guid? correlationId = null, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> AcknowledgeAsync(RealtimeMessageAck ack, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<RealtimeMessageAck>> PublishIntegrationEventAsync(IntegrationEvent integrationEvent, Guid? targetDeviceId = null, bool ordered = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result<IReadOnlyList<DevicePresenceSnapshot>>.Success(Array.Empty<DevicePresenceSnapshot>()));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

