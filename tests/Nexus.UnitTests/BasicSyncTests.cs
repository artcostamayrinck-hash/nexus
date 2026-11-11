using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Sync;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Sync;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Settings;
using Nexus.Infrastructure.Sync;
using Nexus.Mobile.Services.ViewModels;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Xunit;

namespace Nexus.UnitTests;

public sealed class BasicSyncTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-sync-unit-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Clipboard_CyclePrevention_DoesNotEchoRemoteContentBackToTransport()
    {
        await using var ctx = await CreateClientHarnessAsync("cycle-win", "NEXUS Windows");
        var remoteDeviceId = Guid.CreateVersion7();

        var remoteEntry = new ClipboardEntryContract
        {
            EntryId = Guid.CreateVersion7(),
            OriginDeviceId = remoteDeviceId,
            OriginDeviceName = "Pixel 9 Android",
            ContentText = "Texto vindo do Android",
            ContentHash = SyncContentHashHelper.ComputeUtf8Sha256("Texto vindo do Android"),
            SyncOriginToken = Guid.CreateVersion7(),
            CapturedAtUtc = _clock.UtcNow
        };

        var applied = await ctx.ClipboardService.ApplyRemoteEntryAsync(remoteEntry);
        Assert.True(applied.IsSuccess);
        Assert.True(applied.Value);
        Assert.True(ctx.ClipboardService.IsCycleGuardActive);

        // Adapter now holds remote text
        var osText = await ctx.ClipboardAdapter.ReadTextAsync();
        Assert.Equal("Texto vindo do Android", osText.Value);

        // Simulating OS watcher re-capturing the exact text we just wrote must be suppressed!
        ctx.ClipboardAdapter.SimulateUserCopy("Texto vindo do Android");
        await Task.Delay(50);

        Assert.Empty(ctx.FakeTransport.PushedClipboardEntries);

        // Same-origin echo coming back must also be rejected cleanly
        var selfEcho = remoteEntry with { OriginDeviceId = ctx.LocalIdentity.DeviceId };
        var echoApplied = await ctx.ClipboardService.ApplyRemoteEntryAsync(selfEcho);
        Assert.True(echoApplied.IsSuccess);
        Assert.False(echoApplied.Value);
    }

    [Fact]
    public async Task Clipboard_RespectsConfiguredHistoryLimit_AndPrivacyToggle()
    {
        await using var ctx = await CreateClientHarnessAsync("privacy-clip", "NEXUS Host");

        var savePrefs = await ctx.PrivacyService.SavePreferencesAsync(new SyncPrivacyPreferences
        {
            ClipboardSyncEnabled = true,
            ClipboardAutoSync = true,
            ClipboardMaxHistoryItems = 3,
            NotesSyncEnabled = true,
            NotesAutoSync = true
        });
        Assert.True(savePrefs.IsSuccess);

        for (var i = 1; i <= 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            var pushed = await ctx.ClipboardService.CaptureAndSendTextAsync($"Item #{i}", manualTrigger: true);
            Assert.True(pushed.IsSuccess);
        }

        var history = await ctx.ClipboardService.GetHistoryAsync();
        Assert.True(history.IsSuccess);
        Assert.Equal(3, history.Value.Count);
        Assert.Equal("Item #5", history.Value[0].ContentText);
        Assert.Equal("Item #3", history.Value[2].ContentText);
        Assert.Equal(5, ctx.FakeTransport.PushedClipboardEntries.Count);

        // Disable clipboard sync -> local send and remote apply are blocked while Notes stay enabled
        await ctx.PrivacyService.SavePreferencesAsync(savePrefs.Value with { ClipboardSyncEnabled = false });

        var blockedSend = await ctx.ClipboardService.CaptureAndSendTextAsync("Não deve sair", manualTrigger: true);
        Assert.False(blockedSend.IsSuccess);
        Assert.Equal("CLIPBOARD_SYNC_DISABLED", blockedSend.Error?.Code);
        Assert.Equal(5, ctx.FakeTransport.PushedClipboardEntries.Count);

        var blockedRemote = await ctx.ClipboardService.ApplyRemoteEntryAsync(new ClipboardEntryContract
        {
            EntryId = Guid.CreateVersion7(),
            OriginDeviceId = Guid.CreateVersion7(),
            OriginDeviceName = "Outro Dispositivo",
            ContentText = "Não deve sobrescrever",
            ContentHash = SyncContentHashHelper.ComputeUtf8Sha256("Não deve sobrescrever"),
            CapturedAtUtc = _clock.UtcNow
        });
        Assert.True(blockedRemote.IsSuccess);
        Assert.False(blockedRemote.Value);
        Assert.Equal("Item #5", (await ctx.ClipboardAdapter.ReadTextAsync()).Value ?? "Item #5");
    }

    [Fact]
    public async Task Notes_CrudSearchRevisionRestoreAndTombstoneDelete_WorkCorrectly()
    {
        await using var ctx = await CreateClientHarnessAsync("notes-local", "NEXUS Desktop");
        // Keep auto-sync off so offline revision tracking and BaseVersion anchoring can be verified cleanly
        await ctx.PrivacyService.SavePreferencesAsync(new SyncPrivacyPreferences
        {
            ClipboardSyncEnabled = true,
            NotesSyncEnabled = true,
            NotesAutoSync = false
        });

        var createdTime = _clock.UtcNow;
        var create = await ctx.NotesService.CreateNoteAsync("Arquitetura NEXUS", "Versão inicial dos requisitos.");
        Assert.True(create.IsSuccess);
        var note = create.Value;
        Assert.Equal(1, note.Version);
        Assert.Equal(0, note.BaseVersion);
        Assert.Equal(createdTime, note.CreatedAtUtc);

        _clock.Advance(TimeSpan.FromMinutes(3));
        var edit1 = await ctx.NotesService.UpdateNoteAsync(note.NoteId, "Arquitetura NEXUS v2", "Com sincronização offline.");
        Assert.True(edit1.IsSuccess);
        Assert.Equal(2, edit1.Value.Version);
        Assert.Equal(0, edit1.Value.BaseVersion); // Preserved unsynced BaseVersion across sequential offline edits
        Assert.Equal(createdTime, edit1.Value.CreatedAtUtc);
        Assert.Equal(_clock.UtcNow, edit1.Value.ModifiedAtUtc);

        var other = await ctx.NotesService.CreateNoteAsync("Lista de mercado", "Café e filtro");
        Assert.True(other.IsSuccess);

        var search = await ctx.NotesService.ListNotesAsync("SINCRONIZAÇÃO");
        Assert.True(search.IsSuccess);
        Assert.Single(search.Value);
        Assert.Equal(note.NoteId, search.Value[0].NoteId);

        var revisions = await ctx.NotesService.GetRevisionsAsync(note.NoteId);
        Assert.True(revisions.IsSuccess);
        Assert.Equal(2, revisions.Value.Count);
        var v1Rev = revisions.Value.Single(r => r.Version == 1);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var restored = await ctx.NotesService.RestoreRevisionAsync(note.NoteId, v1Rev.RevisionId);
        Assert.True(restored.IsSuccess);
        Assert.Equal("Arquitetura NEXUS", restored.Value.Title);
        Assert.Equal("Versão inicial dos requisitos.", restored.Value.Body);
        Assert.Equal(3, restored.Value.Version);

        var deleted = await ctx.NotesService.DeleteNoteAsync(other.Value.NoteId);
        Assert.True(deleted.IsSuccess);
        Assert.True(deleted.Value.IsDeleted);
        Assert.NotNull(deleted.Value.DeletedAtUtc);

        var activeList = await ctx.NotesService.ListNotesAsync();
        Assert.Single(activeList.Value);
        Assert.DoesNotContain(activeList.Value, n => n.NoteId == other.Value.NoteId);
    }

    [Fact]
    public void NoteConflictMergeEngine_DetectsConcurrentEditAndDeleteVsEdit_WithoutSilentOverwrite()
    {
        var t0 = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var winId = Guid.CreateVersion7();
        var androidId = Guid.CreateVersion7();
        var noteId = Guid.CreateVersion7();

        var v1Server = new NexusNoteContract
        {
            NoteId = noteId,
            Title = "Plano de Viagem",
            Body = "Dia 1: Chegada",
            Version = 1,
            BaseVersion = 1,
            ContentHash = SyncContentHashHelper.ComputeNoteHash("Plano de Viagem", "Dia 1: Chegada", false),
            LastModifiedByDeviceId = winId,
            LastModifiedByDeviceName = "Windows",
            CreatedAtUtc = t0,
            ModifiedAtUtc = t0,
            NeedsSync = false
        };

        // Sequential edit based on v1 advances forward cleanly
        var v2Win = v1Server with
        {
            Title = "Plano de Viagem",
            Body = "Dia 1: Chegada + Hotel",
            Version = 2,
            BaseVersion = 1,
            ContentHash = SyncContentHashHelper.ComputeNoteHash("Plano de Viagem", "Dia 1: Chegada + Hotel", false),
            ModifiedAtUtc = t0.AddMinutes(5)
        };
        var seqEval = NoteConflictMergeEngine.EvaluateServerPush(v1Server, v2Win, ConflictResolutionPolicy.Manual, t0.AddMinutes(6));
        Assert.Equal(NoteMergeOutcomeKind.AcceptedForward, seqEval.Outcome);
        Assert.Null(seqEval.ConflictRecord);
        Assert.Equal(2, seqEval.ResultingNote.Version);

        // Android offline edit based on stale BaseVersion = 1 arrives AFTER v2Win -> CONCURRENT CONFLICT
        var v2AndroidStale = v1Server with
        {
            Title = "Plano de Viagem",
            Body = "Dia 1: Chegada + Museu",
            Version = 2,
            BaseVersion = 1,
            ContentHash = SyncContentHashHelper.ComputeNoteHash("Plano de Viagem", "Dia 1: Chegada + Museu", false),
            LastModifiedByDeviceId = androidId,
            LastModifiedByDeviceName = "Android",
            ModifiedAtUtc = t0.AddMinutes(7)
        };

        var conflictEval = NoteConflictMergeEngine.EvaluateServerPush(
            seqEval.ResultingNote,
            v2AndroidStale,
            ConflictResolutionPolicy.Manual,
            t0.AddMinutes(8));

        Assert.Equal(NoteMergeOutcomeKind.ConflictDetected, conflictEval.Outcome);
        Assert.NotNull(conflictEval.ConflictRecord);
        Assert.False(conflictEval.ConflictRecord!.IsResolved);
        Assert.True(conflictEval.ResultingNote.HasConflict);
        // Never overwrote Server canonical text silently
        Assert.Equal("Dia 1: Chegada + Hotel", conflictEval.ResultingNote.Body);
        // Both versions preserved in revision history
        Assert.Equal(2, conflictEval.RevisionsToArchive.Count);

        // Delete-vs-Edit conflict also triggers explicit conflict and preserves both states
        var deleteAndroidStale = v1Server with
        {
            Version = 2,
            BaseVersion = 1,
            IsDeleted = true,
            DeletedAtUtc = t0.AddMinutes(9),
            ContentHash = SyncContentHashHelper.ComputeNoteHash("Plano de Viagem", "Dia 1: Chegada", true),
            LastModifiedByDeviceId = androidId,
            LastModifiedByDeviceName = "Android"
        };

        var deleteConflict = NoteConflictMergeEngine.EvaluateServerPush(
            seqEval.ResultingNote,
            deleteAndroidStale,
            ConflictResolutionPolicy.Manual,
            t0.AddMinutes(10));

        Assert.Equal(NoteMergeOutcomeKind.ConflictDetected, deleteConflict.Outcome);
        Assert.NotNull(deleteConflict.ConflictRecord);
        Assert.False(deleteConflict.ConflictRecord!.LocalIsDeleted);
        Assert.True(deleteConflict.ConflictRecord.RemoteIsDeleted);

        // Even LastWriteWins archives both versions before resolving
        var lwwEval = NoteConflictMergeEngine.EvaluateServerPush(
            seqEval.ResultingNote,
            v2AndroidStale,
            ConflictResolutionPolicy.LastWriteWins,
            t0.AddMinutes(11));
        Assert.Equal(NoteMergeOutcomeKind.ConflictDetected, lwwEval.Outcome);
        Assert.True(lwwEval.ConflictRecord!.IsResolved);
        Assert.Equal(3, lwwEval.RevisionsToArchive.Count);
        Assert.Equal("Dia 1: Chegada + Museu", lwwEval.ResultingNote.Body);
    }

    [Fact]
    public async Task ViewModels_DesktopAndMobile_RespectAndroidClipboardRestrictions_AndExposeSyncWorkflows()
    {
        await using var desktopCtx = await CreateClientHarnessAsync("vm-desktop", "Windows VM");
        var desktopVm = new SyncViewModel(
            desktopCtx.ClipboardService,
            desktopCtx.NotesService,
            desktopCtx.Orchestrator,
            desktopCtx.PrivacyService,
            desktopCtx.ClipboardAdapter);

        await desktopVm.InitializeAsync();
        desktopVm.ClipboardInputText = "Snippet Desktop";
        await desktopVm.SendClipboardTextCommand.ExecuteAsync();
        Assert.Single(desktopVm.ClipboardHistory);

        desktopVm.EditorTitle = "Nota via ViewModel";
        desktopVm.EditorBody = "Conteúdo estruturado";
        await desktopVm.SaveNoteCommand.ExecuteAsync();
        Assert.Single(desktopVm.Notes);

        var androidAdapter = new SimulatedAndroidRestrictedClipboardAdapter();
        Assert.False(androidAdapter.SupportsContinuousBackgroundMonitor);
        Assert.Contains("Android", androidAdapter.PlatformRestrictionNotice, StringComparison.OrdinalIgnoreCase);

        await using var mobileCtx = await CreateClientHarnessAsync("vm-mobile", "Android VM", androidAdapter);
        var mobileVm = new MobileSyncViewModel(
            mobileCtx.ClipboardService,
            mobileCtx.NotesService,
            mobileCtx.Orchestrator,
            mobileCtx.PrivacyService,
            androidAdapter);

        await mobileVm.InitializeAsync();
        Assert.Contains("segundo plano", mobileVm.PlatformRestrictionText, StringComparison.OrdinalIgnoreCase);

        androidAdapter.SetForegroundClipboard("Link copiado manualmente");
        await mobileVm.CaptureForegroundClipboardCommand.ExecuteAsync();
        Assert.Single(mobileVm.ClipboardHistory);
        Assert.Equal("Link copiado manualmente", mobileVm.ClipboardHistory[0].ContentText);
    }

    private async Task<TestClientHarness> CreateClientHarnessAsync(
        string folderName,
        string deviceName,
        ISystemClipboardAdapter? customAdapter = null)
    {
        var dir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(dir);

        var options = new NexusStorageOptions
        {
            RootDirectory = dir,
            DatabaseFileName = "nexus-unit-sync.db",
            EnablePooling = false
        };
        var connFactory = new SqliteConnectionFactory(options);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);

        var localCtx = new DefaultLocalDeviceContext(
            Guid.Empty,
            deviceName,
            DevicePlatform.Windows,
            DeviceType.Desktop,
            "Windows 11",
            "0.6.0",
            DeviceCapability.All);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState
        {
            LocalDevice = localCtx.ToDeviceInfo(),
            UpdatedAtUtc = _clock.UtcNow
        });

        var initializer = new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance);
        var initResult = await initializer.InitializeAsync();
        Assert.True(initResult.IsSuccess, initResult.Error?.Message);

        var serializer = new SystemTextJsonSerializer();
        var settingsStore = new SqliteSettingsStore(connFactory, failureHandler, serializer, _clock);
        var clipboardRepo = new SqliteClipboardRepository(connFactory, failureHandler);
        var notesRepo = new SqliteNotesRepository(connFactory, failureHandler);
        var queueRepo = new SqliteSyncQueueRepository(connFactory, failureHandler);
        var privacyService = new SyncPrivacySettingsService(settingsStore, _clock, NullLogger<SyncPrivacySettingsService>.Instance);

        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(dir, "vault")
        });
        var identityProvider = new CryptographicIdentityProvider(vault, localCtx, new SystemTextJsonSerializer(), _clock);
        var identity = (await identityProvider.GetOrCreateIdentityAsync()).Value;

        var adapter = customAdapter ?? new DefaultSystemClipboardAdapter();
        var transport = new RecordingSyncTransport();
        var realtime = new StubRealtimeClient();
        var eventBus = new InProcessEventBus();

        var clipboardService = new ClipboardSyncCoordinator(
            adapter,
            clipboardRepo,
            queueRepo,
            privacyService,
            transport,
            identityProvider,
            _clock,
            NullLogger<ClipboardSyncCoordinator>.Instance);

        var notesService = new NotesSyncCoordinator(
            notesRepo,
            queueRepo,
            privacyService,
            transport,
            identityProvider,
            _clock,
            NullLogger<NotesSyncCoordinator>.Instance);

        var orchestrator = new BasicSyncOrchestrator(
            clipboardService,
            notesService,
            clipboardRepo,
            notesRepo,
            queueRepo,
            privacyService,
            transport,
            realtime,
            eventBus,
            _clock,
            NullLogger<BasicSyncOrchestrator>.Instance);

        return new TestClientHarness(
            identity,
            adapter as DefaultSystemClipboardAdapter,
            privacyService,
            transport,
            clipboardService,
            notesService,
            orchestrator);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class TestClientHarness : IAsyncDisposable
    {
        public TestClientHarness(
            Nexus.Contracts.Security.DeviceCryptographicIdentity localIdentity,
            DefaultSystemClipboardAdapter? clipboardAdapter,
            SyncPrivacySettingsService privacyService,
            RecordingSyncTransport fakeTransport,
            ClipboardSyncCoordinator clipboardService,
            NotesSyncCoordinator notesService,
            BasicSyncOrchestrator orchestrator)
        {
            LocalIdentity = localIdentity;
            ClipboardAdapter = clipboardAdapter!;
            PrivacyService = privacyService;
            FakeTransport = fakeTransport;
            ClipboardService = clipboardService;
            NotesService = notesService;
            Orchestrator = orchestrator;
        }

        public Nexus.Contracts.Security.DeviceCryptographicIdentity LocalIdentity { get; }
        public DefaultSystemClipboardAdapter ClipboardAdapter { get; }
        public SyncPrivacySettingsService PrivacyService { get; }
        public RecordingSyncTransport FakeTransport { get; }
        public ClipboardSyncCoordinator ClipboardService { get; }
        public NotesSyncCoordinator NotesService { get; }
        public BasicSyncOrchestrator Orchestrator { get; }

        public ValueTask DisposeAsync()
        {
            Orchestrator.Dispose();
            ClipboardService.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }

    private sealed class RecordingSyncTransport : INexusSyncTransport
    {
        public bool IsRealtimeAvailable { get; set; } = true;
        public List<ClipboardEntryContract> PushedClipboardEntries { get; } = new();

        public event EventHandler<ClipboardEntryContract>? RemoteClipboardReceived;
        public event EventHandler<NexusNoteContract>? RemoteNoteChangeReceived;

        public Task<Result> PushClipboardEntryAsync(ClipboardEntryContract entry, CancellationToken cancellationToken = default)
        {
            PushedClipboardEntries.Add(entry);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<ClipboardEntryContract>>> PullClipboardHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<ClipboardEntryContract>>.Success(PushedClipboardEntries.TakeLast(limit).ToArray()));

        public Task<Result<NoteSyncResponse>> SyncNotesAsync(NoteSyncRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<NoteSyncResponse>.Success(new NoteSyncResponse
            {
                CanonicalNotes = request.LocalChanges,
                AcceptedNoteIds = request.LocalChanges.Select(n => n.NoteId).ToArray()
            }));

        public void SuppressWarnings()
        {
            RemoteClipboardReceived?.Invoke(this, null!);
            RemoteNoteChangeReceived?.Invoke(this, null!);
        }
    }

    private sealed class SimulatedAndroidRestrictedClipboardAdapter : ISystemClipboardAdapter
    {
        private string? _text;
        public bool SupportsContinuousBackgroundMonitor => false;
        public string PlatformRestrictionNotice =>
            "Android 10+ restringe monitoramento contínuo de clipboard em segundo plano; use captura em primeiro plano.";

#pragma warning disable CS0067
        public event EventHandler<string>? SystemClipboardTextCaptured;
#pragma warning restore CS0067

        public void SetForegroundClipboard(string text) => _text = text;
        public Task<Result<string?>> ReadTextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<string?>.Success(_text));
        public Task<Result> WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            _text = text;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubRealtimeClient : IRealtimeClientChannel
    {
        public TransportKind Transport => TransportKind.WebSocket;
        public bool IsConnected => false;
        public ConnectionState State => ConnectionState.Disconnected;
        public RealtimeSessionSnapshot? CurrentSession => null;
        public IReadOnlyList<DevicePresenceSnapshot> LivePresence => Array.Empty<DevicePresenceSnapshot>();
        public IReadOnlyList<Nexus.Contracts.Messaging.RealtimeMessageEnvelope> RecentMessages => Array.Empty<Nexus.Contracts.Messaging.RealtimeMessageEnvelope>();
        public IReadOnlyList<Nexus.Contracts.Messaging.RealtimeMessageAck> RecentAcknowledgements => Array.Empty<Nexus.Contracts.Messaging.RealtimeMessageAck>();
        public string? LastError => null;
        public DateTimeOffset? LastHeartbeatUtc => null;

#pragma warning disable CS0067
        public event EventHandler<ConnectionStateSnapshot>? StateChanged;
        public event EventHandler<Nexus.Contracts.Messaging.RealtimeMessageEnvelope>? MessageReceived;
        public event EventHandler<Nexus.Contracts.Messaging.RealtimeMessageAck>? AckReceived;
        public event EventHandler<Nexus.Contracts.Commands.DeviceCommand>? CommandReceived;
        public event EventHandler<CommandCancellationRequest>? CommandCancelled;
        public event EventHandler<Nexus.Contracts.Commands.CommandExecutionResult>? CommandResultReceived;
        public event EventHandler<IReadOnlyList<DevicePresenceSnapshot>>? PresenceUpdated;
        public event EventHandler<ClipboardEntryContract>? ClipboardReceived;
        public event EventHandler<NexusNoteContract>? NoteChangeReceived;
        public event EventHandler<Nexus.Contracts.Transfers.FileTransferProgressEvent>? TransferProgressReceived;
        public event EventHandler<Nexus.Contracts.Transfers.FileTransferRecord>? TransferAvailableReceived;
        public event EventHandler<Nexus.Contracts.Modules.MobileNotificationRecord>? MobileNotificationReceived;
        public event EventHandler<Nexus.Contracts.Modules.MediaPlaybackStateContract>? MediaStateReceived;
        public event EventHandler<Nexus.Contracts.Modules.IntegrationModuleStatusSnapshot>? ModuleStatusChangedReceived;
        public event EventHandler<Nexus.Contracts.Automation.PanelProfile>? PanelProfileUpdatedReceived;
        public event EventHandler<Nexus.Contracts.Automation.AutomationExecutionRecord>? AutomationExecutionReceived;
#pragma warning restore CS0067

        public Task<Result> OpenAsync(string endpoint, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> CloseAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<RealtimeSessionSnapshot>> OpenAuthenticatedAsync(string? endpointOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeSessionSnapshot>.Failure(NexusError.Network("Stub offline")));
        public Task<Result<Nexus.Contracts.Messaging.RealtimeMessageAck>> SendEnvelopeAsync(Nexus.Contracts.Messaging.RealtimeMessageEnvelope envelope, bool waitForAck = true, TimeSpan? ackTimeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<Nexus.Contracts.Messaging.RealtimeMessageAck>.Failure(NexusError.Network("Stub offline")));
        public Task<Result<Nexus.Contracts.Messaging.RealtimeMessageAck>> SendCommandAsync(Nexus.Contracts.Commands.DeviceCommand command, bool waitForAck = true, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<Nexus.Contracts.Messaging.RealtimeMessageAck>.Failure(NexusError.Network("Stub offline")));
        public Task<Result> CancelCommandAsync(Guid commandId, Guid targetDeviceId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result> SendCommandResultAsync(Nexus.Contracts.Commands.CommandExecutionResult result, Guid? targetDeviceId = null, Guid? correlationId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result> AcknowledgeAsync(Nexus.Contracts.Messaging.RealtimeMessageAck ack, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<NexusResponse<TResponse>>> SendAsync<TRequest, TResponse>(NexusRequest<TRequest> request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<NexusResponse<TResponse>>.Failure(NexusError.Network("Stub offline")));
        public Task<Result> PublishEventAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<Nexus.Contracts.Messaging.RealtimeMessageAck>> PublishIntegrationEventAsync(Nexus.Contracts.Events.IntegrationEvent integrationEvent, Guid? targetDeviceId = null, bool ordered = true, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<Nexus.Contracts.Messaging.RealtimeMessageAck>.Failure(NexusError.Network("Stub offline")));
        public Task<Result<IReadOnlyList<DevicePresenceSnapshot>>> RefreshPresenceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<DevicePresenceSnapshot>>.Success(Array.Empty<DevicePresenceSnapshot>()));
        public Task<Result<int>> SendHeartbeatAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<int>.Failure(NexusError.Network("Stub offline")));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
