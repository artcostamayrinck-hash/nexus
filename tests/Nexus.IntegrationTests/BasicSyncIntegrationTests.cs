using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Sync;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Settings;
using Nexus.Infrastructure.Sync;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class BasicSyncIntegrationTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-sync-int-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 16, 0, 0, TimeSpan.Zero));
    private readonly SyncWebFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public BasicSyncIntegrationTests()
    {
        _factory = new SyncWebFactory(_storageDir, _clock);
    }

    [Fact]
    public async Task RealtimeClipboardSync_WindowsToAndroid_PropagatesWithoutLoopOrEcho()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-host", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);

        await using var winNode = await CreateConnectedSyncNodeAsync(http, host, "win1", "Windows Desktop", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedSyncNodeAsync(http, host, "and1", "Pixel Android", DevicePlatform.Android, NexusPermission.All);

        var androidReceivedTcs = new TaskCompletionSource<ClipboardEntryContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        androidNode.ClipboardService.RemoteClipboardApplied += (_, e) => androidReceivedTcs.TrySetResult(e);

        var sendResult = await winNode.ClipboardService.CaptureAndSendTextAsync("Código OTP 849201", manualTrigger: true);
        Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);

        var received = await androidReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Código OTP 849201", received.ContentText);
        Assert.True(received.IsRemoteReceived);
        Assert.Equal("Código OTP 849201", (await androidNode.ClipboardAdapter.ReadTextAsync()).Value);

        // Simulated OS clipboard capture on Android for the newly arrived text must NOT bounce back to Windows
        var winCountBefore = (await winNode.ClipboardService.GetHistoryAsync()).Value.Count;
        androidNode.ClipboardAdapter.SimulateUserCopy("Código OTP 849201");
        await Task.Delay(250);
        var winCountAfter = (await winNode.ClipboardService.GetHistoryAsync()).Value.Count;
        Assert.Equal(winCountBefore, winCountAfter);
    }

    [Fact]
    public async Task Notes_CrudSearchAndTombstoneDelete_ConvergeRealTimeAcrossDevices()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-notes", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);

        await using var winNode = await CreateConnectedSyncNodeAsync(http, host, "win-n", "Windows", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedSyncNodeAsync(http, host, "and-n", "Android", DevicePlatform.Android, NexusPermission.All);

        var created = await winNode.NotesService.CreateNoteAsync("Reunião NEXUS", "Discutir arquitetura da Fase 06.");
        Assert.True(created.IsSuccess, created.Error?.Message);

        await WaitUntilAsync(async () =>
        {
            var found = await androidNode.NotesService.ListNotesAsync("fase 06");
            return found.IsSuccess && found.Value.Count == 1;
        });

        _clock.Advance(TimeSpan.FromMinutes(1));
        var updated = await androidNode.NotesService.UpdateNoteAsync(created.Value.NoteId, "Reunião NEXUS", "Discutir arquitetura Fase 06 + Conflitos.");
        Assert.True(updated.IsSuccess, updated.Error?.Message);

        await WaitUntilAsync(async () =>
        {
            var winNotes = await winNode.NotesService.ListNotesAsync("Conflitos");
            return winNotes.IsSuccess && winNotes.Value.Count == 1;
        });

        _clock.Advance(TimeSpan.FromMinutes(1));
        var deleted = await androidNode.NotesService.DeleteNoteAsync(created.Value.NoteId);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);

        await WaitUntilAsync(async () =>
        {
            var winActive = await winNode.NotesService.ListNotesAsync();
            return winActive.IsSuccess && winActive.Value.Count == 0;
        });
    }

    [Fact]
    public async Task ConcurrentOfflineNoteEdits_DetectConflict_PreserveBothVersions_ResolveAndRestoreRevision()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-conf", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);

        await using var winNode = await CreateConnectedSyncNodeAsync(http, host, "win-c", "Windows PC", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedSyncNodeAsync(http, host, "and-c", "Android Phone", DevicePlatform.Android, NexusPermission.All);

        // Create and sync baseline v1
        var created = await winNode.NotesService.CreateNoteAsync("Credenciais Rede", "Base v1 inicial");
        Assert.True(created.IsSuccess);
        var noteId = created.Value.NoteId;

        await WaitUntilAsync(async () => (await androidNode.NotesService.ListNotesAsync()).Value.Count == 1);

        // Simulate both devices disconnecting and editing concurrently offline
        await winNode.Realtime.CloseAsync();
        await androidNode.Realtime.CloseAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        var winOfflineEdit = await winNode.NotesService.UpdateNoteAsync(noteId, "Credenciais Rede", "Edição offline do Windows");
        Assert.True(winOfflineEdit.IsSuccess);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var androidOfflineEdit = await androidNode.NotesService.UpdateNoteAsync(noteId, "Credenciais Rede", "Edição offline do Android");
        Assert.True(androidOfflineEdit.IsSuccess);

        // Windows reconnects first -> accepted on server as v2
        Assert.True((await winNode.Realtime.OpenAuthenticatedAsync()).IsSuccess);
        await WaitUntilAsync(async () => winNode.Orchestrator.CurrentOverview.PendingOperationsCount == 0);

        // Android reconnects -> triggers ResumeAfterReconnectAsync -> server detects divergence (BaseVersion 1 < Server v2)!
        Assert.True((await androidNode.Realtime.OpenAuthenticatedAsync()).IsSuccess);
        await WaitUntilAsync(async () => (await androidNode.NotesService.ListConflictsAsync(unresolvedOnly: true)).Value.Count == 1);

        var conflicts = (await androidNode.NotesService.ListConflictsAsync(unresolvedOnly: true)).Value;
        Assert.Single(conflicts);
        var conflict = conflicts[0];
        Assert.Equal("Edição offline do Android", conflict.LocalBody);
        Assert.Equal("Edição offline do Windows", conflict.RemoteBody);

        // Both versions were archived cleanly in NoteRevisions (zero silent overwrite)
        var revisions = (await androidNode.NotesService.GetRevisionsAsync(noteId)).Value;
        Assert.Contains(revisions, r => r.Body.Contains("Windows", StringComparison.Ordinal));
        Assert.Contains(revisions, r => r.Body.Contains("Android", StringComparison.Ordinal));

        // Resolve via ManualMerge -> propagates resolution to both Server and Windows
        var resolved = await androidNode.NotesService.ResolveConflictAsync(new ResolveConflictRequest
        {
            ConflictId = conflict.ConflictId,
            Choice = ConflictResolutionChoice.ManualMerge,
            MergedTitle = "Credenciais Rede (Mesclada)",
            MergedBody = "Edição Windows + Edição Android mescladas"
        });
        Assert.True(resolved.IsSuccess, resolved.Error?.Message);
        Assert.Empty((await androidNode.NotesService.ListConflictsAsync(unresolvedOnly: true)).Value);

        await WaitUntilAsync(async () =>
        {
            var winList = await winNode.NotesService.ListNotesAsync();
            return winList.Value.Count == 1 && winList.Value[0].Body == "Edição Windows + Edição Android mescladas";
        });

        // Recover initial baseline revision v1 and verify convergence
        var winRevisions = (await winNode.NotesService.GetRevisionsAsync(noteId)).Value;
        var initialRev = winRevisions.OrderBy(r => r.Version).First();
        var restored = await winNode.NotesService.RestoreRevisionAsync(noteId, initialRev.RevisionId);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal("Base v1 inicial", restored.Value.Body);

        await WaitUntilAsync(async () =>
        {
            var andList = await androidNode.NotesService.ListNotesAsync();
            return andList.Value.Count == 1 && andList.Value[0].Body == "Base v1 inicial";
        });
    }

    [Fact]
    public async Task OfflineQueue_FlushesPendingClipboardAndNotes_OnReconnect_AndUpdatesLastSyncTimestamp()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-off", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);

        await using var winNode = await CreateConnectedSyncNodeAsync(http, host, "win-off", "Windows Offline", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedSyncNodeAsync(http, host, "and-off", "Android Peer", DevicePlatform.Android, NexusPermission.All);

        Assert.Null(winNode.Orchestrator.CurrentOverview.LastSyncAtUtc);

        // Disconnect Windows
        await winNode.Realtime.CloseAsync();

        var clipQueued = await winNode.ClipboardService.CaptureAndSendTextAsync("Texto copiado offline", manualTrigger: true);
        Assert.True(clipQueued.IsSuccess);

        var noteQueued = await winNode.NotesService.CreateNoteAsync("Nota Offline", "Criada sem internet");
        Assert.True(noteQueued.IsSuccess);
        await winNode.NotesService.UpdateNoteAsync(noteQueued.Value.NoteId, "Nota Offline", "Criada e editada offline");

        var pending = await winNode.QueueRepo.ListPendingAsync();
        Assert.True(pending.Value.Count >= 2);
        Assert.All(pending.Value, p => Assert.Equal(SyncQueueStatus.DeferredOffline, p.Status));

        // Reconnect triggers ResumeAfterReconnectAsync automatically via StateChanged
        Assert.True((await winNode.Realtime.OpenAuthenticatedAsync()).IsSuccess);

        await WaitUntilAsync(async () =>
        {
            var count = (await winNode.QueueRepo.GetPendingCountAsync()).Value;
            return count == 0 && winNode.Orchestrator.CurrentOverview.LastSyncAtUtc.HasValue;
        });

        Assert.Equal(SyncStatus.Completed, winNode.Orchestrator.CurrentOverview.Status);

        // Android can pull/receive both clipboard and note created offline
        await androidNode.Orchestrator.SyncNowAsync();
        var androidClips = (await androidNode.ClipboardService.GetHistoryAsync()).Value;
        Assert.Contains(androidClips, c => c.ContentText == "Texto copiado offline");

        var androidNotes = (await androidNode.NotesService.ListNotesAsync()).Value;
        Assert.Contains(androidNotes, n => n.Body == "Criada e editada offline");
    }

    [Fact]
    public async Task PrivacyAndPermissions_EnforceMissingPermissions_FeatureToggles_AndRevocation()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-perm", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);

        await using var winNode = await CreateConnectedSyncNodeAsync(http, host, "win-perm", "Windows Admin", DevicePlatform.Windows, NexusPermission.All);

        // Pair Android peer with ONLY Notes + ViewStatus (NO Clipboard permission!)
        await using var limitedAndroid = await CreateConnectedSyncNodeAsync(
            http,
            host,
            "and-lim",
            "Android Restricted",
            DevicePlatform.Android,
            NexusPermission.ViewStatus | NexusPermission.Notes);

        var unauthorizedClipAttempt = false;
        limitedAndroid.ClipboardService.RemoteClipboardApplied += (_, _) => unauthorizedClipAttempt = true;

        // Server MUST reject clipboard push from device lacking NexusPermission.Clipboard
        var deniedPush = await limitedAndroid.Transport.PushClipboardEntryAsync(new ClipboardEntryContract
        {
            OriginDeviceId = limitedAndroid.Identity.DeviceId,
            OriginDeviceName = limitedAndroid.Identity.CustomName,
            ContentText = "Bloqueado por permissão",
            ContentHash = SyncContentHashHelper.ComputeUtf8Sha256("Bloqueado por permissão"),
            CapturedAtUtc = _clock.UtcNow
        });
        Assert.False(deniedPush.IsSuccess);
        Assert.Equal("PERMISSION_DENIED_CLIPBOARD", deniedPush.Error?.Code);

        // When Windows broadcasts clipboard, restricted Android peer MUST NOT receive it over SignalR
        await winNode.ClipboardService.CaptureAndSendTextAsync("Dado confidencial do Windows", manualTrigger: true);
        await Task.Delay(300);
        Assert.False(unauthorizedClipAttempt);
        Assert.Empty((await limitedAndroid.ClipboardService.GetHistoryAsync()).Value);

        // Android limited STILL has Notes permission and can sync notes when NotesSyncEnabled = true
        var okNote = await limitedAndroid.NotesService.CreateNoteAsync("Nota Permitida", "Apenas Notas habilitado");
        Assert.True(okNote.IsSuccess);
        await WaitUntilAsync(async () => (await winNode.NotesService.ListNotesAsync("Permitida")).Value.Count == 1);

        // Revoking device on Server immediately blocks Notes sync and disconnects access
        using var adminHttp = _factory.CreateClient();
        adminHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", winNode.AccessToken);
        var revokeResp = await adminHttp.PostAsJsonAsync(
            $"/api/v1/trusted-devices/{limitedAndroid.Identity.DeviceId:D}/revoke",
            new RevokeDeviceRequest { Reason = "Teste revogação Fase 06" },
            JsonOptions);
        Assert.True(revokeResp.IsSuccessStatusCode, await revokeResp.Content.ReadAsStringAsync());

        var blockedAfterRevoke = await limitedAndroid.Transport.SyncNotesAsync(new NoteSyncRequest
        {
            SourceDeviceId = limitedAndroid.Identity.DeviceId,
            SourceDeviceName = limitedAndroid.Identity.CustomName
        });
        Assert.False(blockedAfterRevoke.IsSuccess);
        Assert.Equal(ErrorCategory.Unauthorized, blockedAfterRevoke.Error?.Category);

        // Also confirm toggling off ClipboardSyncEnabled on an authorized peer keeps Notes working while isolating Clipboard
        await using var androidToggle = await CreateConnectedSyncNodeAsync(http, host, "and-tog", "Android Privacy", DevicePlatform.Android, NexusPermission.All);
        await androidToggle.PrivacyService.SavePreferencesAsync(new SyncPrivacyPreferences
        {
            ClipboardSyncEnabled = false,
            NotesSyncEnabled = true,
            NotesAutoSync = true
        });
        await winNode.ClipboardService.CaptureAndSendTextAsync("Não entregar quando desligado", manualTrigger: true);
        await Task.Delay(250);
        Assert.Empty((await androidToggle.ClipboardService.GetHistoryAsync()).Value);
    }

    private async Task<ConnectedSyncNode> CreateConnectedSyncNodeAsync(
        HttpClient http,
        CryptographicIdentityProvider hostCrypto,
        string key,
        string deviceName,
        DevicePlatform platform,
        NexusPermission permissions)
    {
        var dir = Path.Combine(_storageDir, "nodes", key);
        Directory.CreateDirectory(dir);

        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(dir, "vault")
        });
        var localCtx = new DefaultLocalDeviceContext(
            Guid.Empty,
            deviceName,
            platform,
            platform == DevicePlatform.Windows ? DeviceType.Desktop : DeviceType.Phone,
            platform == DevicePlatform.Windows ? "Windows 11" : "Android 15",
            "0.6.0",
            DeviceCapability.All);

        var serializer = new SystemTextJsonSerializer();
        var clientCrypto = new CryptographicIdentityProvider(vault, localCtx, serializer, _clock);
        var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), serializer, _clock);
        var clientProtocol = new NexusPairingProtocolClient(clientCrypto, new SecureTokenGenerator(), serializer, _clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, 120)).Value;
        var createResp = await http.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions);
        createResp.EnsureSuccessStatusCode();
        var createSnap = (await createResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;

        var qr = clientProtocol.ParseAndVerifyQrPayload(createSnap.QrPayloadJson!).Value;
        var claim = (await clientProtocol.BuildClaimRequestAsync(qr, permissions)).Value;
        (await http.PostAsJsonAsync("/api/v1/pairing/claim", claim.Request, JsonOptions)).EnsureSuccessStatusCode();

        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: true, permissions)).Value;
        (await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions)).EnsureSuccessStatusCode();

        var finalizeReq = (await clientProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeReq, JsonOptions);
        finalizeResp.EnsureSuccessStatusCode();
        var tokens = (await finalizeResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;

        var identity = (await clientCrypto.GetOrCreateIdentityAsync()).Value;
        var credProvider = new InMemoryCredentialProvider(new RealtimeCredentialBundle(
            identity.DeviceId,
            identity.CustomName,
            tokens.AccessToken,
            tokens.AccessTokenExpiresAtUtc,
            "http://localhost",
            null));

        var storageOptions = new NexusStorageOptions
        {
            RootDirectory = dir,
            DatabaseFileName = "nexus-node.db",
            EnablePooling = false
        };
        var connFactory = new SqliteConnectionFactory(storageOptions);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState
        {
            LocalDevice = localCtx.ToDeviceInfo(),
            UpdatedAtUtc = _clock.UtcNow
        });

        var initializer = new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance);
        Assert.True((await initializer.InitializeAsync()).IsSuccess);

        var settingsStore = new SqliteSettingsStore(connFactory, failureHandler, serializer, _clock);
        var clipboardRepo = new SqliteClipboardRepository(connFactory, failureHandler);
        var notesRepo = new SqliteNotesRepository(connFactory, failureHandler);
        var queueRepo = new SqliteSyncQueueRepository(connFactory, failureHandler);
        var privacyService = new SyncPrivacySettingsService(settingsStore, _clock, NullLogger<SyncPrivacySettingsService>.Instance);

        var rtOptions = new RealtimeClientOptions
        {
            AutoReconnect = false,
            DefaultAckTimeout = TimeSpan.FromSeconds(5),
            SignalRMessageHandlerFactory = (_, _) => _factory.Server.CreateHandler()
        };
        var eventBus = new InProcessEventBus();
        var connMgr = new ConnectionStateService(localCtx, _clock, eventBus, stateMgr);
        var realtime = new SignalRRealtimeClient(
            credProvider,
            connMgr,
            new RealtimeMessagePipeline(_clock),
            eventBus,
            serializer,
            _clock,
            rtOptions,
            NullLogger<SignalRRealtimeClient>.Instance);

        var adapter = new DefaultSystemClipboardAdapter();
        var transport = new ServerBasicSyncTransport(
            clientProtocol,
            credProvider,
            realtime,
            rtOptions,
            NullLogger<ServerBasicSyncTransport>.Instance);

        var clipboardService = new ClipboardSyncCoordinator(
            adapter,
            clipboardRepo,
            queueRepo,
            privacyService,
            transport,
            clientCrypto,
            _clock,
            NullLogger<ClipboardSyncCoordinator>.Instance);

        var notesService = new NotesSyncCoordinator(
            notesRepo,
            queueRepo,
            privacyService,
            transport,
            clientCrypto,
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

        var openRes = await realtime.OpenAuthenticatedAsync();
        Assert.True(openRes.IsSuccess, openRes.Error?.Message);
        await orchestrator.InitializeAsync();

        return new ConnectedSyncNode(
            identity,
            tokens.AccessToken,
            realtime,
            transport,
            adapter,
            queueRepo,
            privacyService,
            clipboardService,
            notesService,
            orchestrator);
    }

    private CryptographicIdentityProvider CreateCryptoProvider(
        string folderName,
        string deviceName,
        DevicePlatform platform,
        DeviceType deviceType)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(
            Guid.Empty,
            deviceName,
            platform,
            deviceType,
            "Windows 11",
            "0.6.0",
            DeviceCapability.All);
        return new CryptographicIdentityProvider(vault, ctx, new SystemTextJsonSerializer(), _clock);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }
            await Task.Delay(60).ConfigureAwait(false);
        }

        Assert.True(await condition().ConfigureAwait(false), $"Condition timed out after {timeoutMs}ms.");
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private sealed class ConnectedSyncNode(
        DeviceCryptographicIdentity identity,
        string accessToken,
        SignalRRealtimeClient realtime,
        ServerBasicSyncTransport transport,
        DefaultSystemClipboardAdapter clipboardAdapter,
        SqliteSyncQueueRepository queueRepo,
        SyncPrivacySettingsService privacyService,
        ClipboardSyncCoordinator clipboardService,
        NotesSyncCoordinator notesService,
        BasicSyncOrchestrator orchestrator) : IAsyncDisposable
    {
        public DeviceCryptographicIdentity Identity { get; } = identity;
        public string AccessToken { get; } = accessToken;
        public SignalRRealtimeClient Realtime { get; } = realtime;
        public ServerBasicSyncTransport Transport { get; } = transport;
        public DefaultSystemClipboardAdapter ClipboardAdapter { get; } = clipboardAdapter;
        public SqliteSyncQueueRepository QueueRepo { get; } = queueRepo;
        public SyncPrivacySettingsService PrivacyService { get; } = privacyService;
        public ClipboardSyncCoordinator ClipboardService { get; } = clipboardService;
        public NotesSyncCoordinator NotesService { get; } = notesService;
        public BasicSyncOrchestrator Orchestrator { get; } = orchestrator;

        public async ValueTask DisposeAsync()
        {
            Orchestrator.Dispose();
            ClipboardService.Dispose();
            Transport.Dispose();
            await Realtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }

    private sealed class InMemoryCredentialProvider(RealtimeCredentialBundle bundle) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(bundle));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("Refresh disabled", code: "REALTIME_AUTH_EXPIRED")));
    }

    private sealed class SyncWebFactory(string storageDir, MutableClock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-server-sync.db",
                    ["Nexus:Storage:DisablePooling"] = "true",
                    ["Nexus:Tls:BindKestrelHttps"] = "false",
                    ["Nexus:Tls:RequireHttps"] = "false",
                    ["Nexus:Security:EnableLocalHostBridge"] = "false",
                    ["Nexus:Discovery:EnableMdns"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
            });
        }
    }
}
