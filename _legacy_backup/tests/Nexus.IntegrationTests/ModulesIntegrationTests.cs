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
using Nexus.Contracts.Modules;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Modules;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Modules;
using Nexus.Infrastructure.Security;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class ModulesIntegrationTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-mod-int-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero));
    private readonly ModulesWebFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public ModulesIntegrationTests()
    {
        _factory = new ModulesWebFactory(_storageDir, _clock);
    }

    [Fact]
    public async Task ControlModule_RemoteActionsAndPowerConfirmation_RespectPermissionsAndSettings()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-ctl", "NEXUS Win", DevicePlatform.Windows);

        var inputOnlyMobile = await CreateNodeAsync(
            http,
            host,
            "mob-ctl-input",
            "Android Control",
            DevicePlatform.Android,
            NexusPermission.ViewStatus | NexusPermission.ControlInput);

        var clickRes = await inputOnlyMobile.ModulesClient.SendControlCommandAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = inputOnlyMobile.Identity.DeviceId,
            Action = RemoteControlActionType.MouseLeftClick
        });
        Assert.True(clickRes.IsSuccess, clickRes.Error?.Message);
        Assert.True(clickRes.Value.Executed);

        var powerDenied = await inputOnlyMobile.ModulesClient.SendControlCommandAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = inputOnlyMobile.Identity.DeviceId,
            Action = RemoteControlActionType.PowerLock,
            ConfirmedByUser = true
        });
        Assert.True(powerDenied.IsFailure);
        Assert.Equal("PERMISSION_DENIED_SYSTEMPOWER", powerDenied.Error!.Code);

        var fullMobile = await CreateNodeAsync(
            http,
            host,
            "mob-ctl-full",
            "Android Full",
            DevicePlatform.Android,
            NexusPermission.All);

        // Unconfirmed power action is blocked with CONFIRMATION_REQUIRED
        var unconfirmed = await fullMobile.ModulesClient.SendControlCommandAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = fullMobile.Identity.DeviceId,
            Action = RemoteControlActionType.PowerSleep,
            ConfirmedByUser = false
        });
        Assert.True(unconfirmed.IsFailure);
        Assert.Equal("CONFIRMATION_REQUIRED", unconfirmed.Error!.Code);

        // Confirmed power action runs safely under DryRunPowerActions in test environment
        var confirmed = await fullMobile.ModulesClient.SendControlCommandAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = fullMobile.Identity.DeviceId,
            Action = RemoteControlActionType.PowerLock,
            ConfirmedByUser = true
        });
        Assert.True(confirmed.IsSuccess, confirmed.Error?.Message);
        Assert.True(confirmed.Value.Executed);

        await inputOnlyMobile.DisposeAsync();
        await fullMobile.DisposeAsync();
    }

    [Fact]
    public async Task NotifyModule_AndroidPushFilteredAndRelayedInRealTimeToWindows()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-ntf", "NEXUS Win", DevicePlatform.Windows);

        await using var winNode = await CreateNodeAsync(http, host, "win-ntf", "Windows Host", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateNodeAsync(http, host, "and-ntf", "Android Phone", DevicePlatform.Android, NexusPermission.All);

        var receivedTcs = new TaskCompletionSource<MobileNotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.MobileNotificationReceived += (_, n) => receivedTcs.TrySetResult(n);

        // Configure server allowlist via HTTP
        var settingsService = _factory.Services.GetRequiredService<IModuleSettingsService>();
        await settingsService.SaveNotifySettingsAsync(new NotifyModuleSettings
        {
            IsEnabled = true,
            FilterMode = NotificationFilterMode.AllowList,
            AllowedApps = ["WhatsApp"]
        });

        var rejectedReceipt = await androidNode.ModulesClient.PushMobileNotificationAsync(new PushMobileNotificationRequest
        {
            SourceDeviceId = androidNode.Identity.DeviceId,
            AppName = "GameAds",
            Title = "Bonus",
            Body = "Ignore"
        });
        Assert.True(rejectedReceipt.IsSuccess);
        Assert.False(rejectedReceipt.Value.Forwarded);

        var acceptedReceipt = await androidNode.ModulesClient.PushMobileNotificationAsync(new PushMobileNotificationRequest
        {
            SourceDeviceId = androidNode.Identity.DeviceId,
            AppName = "WhatsApp",
            PackageName = "com.whatsapp",
            Title = "Ana",
            Body = "Projeto NEXUS Fase 08!"
        });
        Assert.True(acceptedReceipt.IsSuccess);
        Assert.True(acceptedReceipt.Value.Forwarded);

        var relayed = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("WhatsApp", relayed.AppName);
        Assert.Equal("Ana", relayed.Title);

        var history = await winNode.ModulesClient.GetRemoteNotificationHistoryAsync();
        Assert.True(history.IsSuccess);
        Assert.Single(history.Value);
        Assert.True((await winNode.ModulesClient.DismissRemoteNotificationAsync(relayed.NotificationId)).IsSuccess);
    }

    [Fact]
    public async Task MonitorAndMediaModules_EnforcePermissionsBroadcastMediaStateAndAllowRemoteEndpoint()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-mm", "NEXUS Win", DevicePlatform.Windows);

        await using var monitorOnly = await CreateNodeAsync(
            http,
            host,
            "mob-mon",
            "Android Monitor",
            DevicePlatform.Android,
            NexusPermission.ViewStatus | NexusPermission.HardwareMonitor,
            endpointOverride: "https://nexus-pc.tailnet-abc.ts.net:5443");

        await using var winNode = await CreateNodeAsync(
            http,
            host,
            "win-mm",
            "Windows Hub",
            DevicePlatform.Windows,
            NexusPermission.All);

        await using var fullAndroid = await CreateNodeAsync(
            http,
            host,
            "and-mm",
            "Android Remote",
            DevicePlatform.Android,
            NexusPermission.All);

        // Tailscale-style remote endpoint succeeds over test server handler and returns genuine host telemetry
        var monRes = await monitorOnly.ModulesClient.GetRemoteMonitorSnapshotAsync();
        Assert.True(monRes.IsSuccess, monRes.Error?.Message);
        Assert.True(monRes.Value.Cpu.LogicalCores > 0);
        Assert.False(monRes.Value.Temperatures.IsAvailable);

        // Device without MediaControl is denied independently
        var mediaDenied = await monitorOnly.ModulesClient.SendMediaCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = monitorOnly.Identity.DeviceId,
            Action = MediaControlAction.PlayPause
        });
        Assert.True(mediaDenied.IsFailure);
        Assert.Equal("PERMISSION_DENIED_MEDIACONTROL", mediaDenied.Error!.Code);

        // Full Android sends media command and broadcasts real-time state + module toggle notices
        var mediaTcs = new TaskCompletionSource<MediaPlaybackStateContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toggleTcs = new TaskCompletionSource<IntegrationModuleStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.MediaStateReceived += (_, s) => mediaTcs.TrySetResult(s);
        winNode.Realtime.ModuleStatusChangedReceived += (_, m) => toggleTcs.TrySetResult(m);

        var mediaExec = await fullAndroid.ModulesClient.SendMediaCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = fullAndroid.Identity.DeviceId,
            Action = MediaControlAction.PlayPause
        });
        Assert.True(mediaExec.IsSuccess, mediaExec.Error?.Message);
        var broadcastMedia = await mediaTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MediaPlaybackStatus.Playing, broadcastMedia.PlaybackStatus);

        // Toggle Monitor off independently
        var toggled = await fullAndroid.ModulesClient.SetRemoteModuleEnabledAsync(
            new UpdateModuleToggleRequest(IntegrationModuleKind.Monitor, IsEnabled: false));
        Assert.True(toggled.IsSuccess);
        Assert.False(toggled.Value.IsEnabled);

        var broadcastToggle = await toggleTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IntegrationModuleKind.Monitor, broadcastToggle.ModuleKind);
        Assert.False(broadcastToggle.IsEnabled);

        var monitorAfterDisable = await monitorOnly.ModulesClient.GetRemoteMonitorSnapshotAsync();
        Assert.True(monitorAfterDisable.IsFailure);
        Assert.Equal("MODULE_DISABLED", monitorAfterDisable.Error!.Code);
    }

    [Fact]
    public async Task DisconnectedOrMissingServer_ReturnsGracefulNetworkErrorWithoutCrashing()
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "vault-offline")
        });
        var localCtx = new DefaultLocalDeviceContext(
            Guid.NewGuid(), "OfflineAndroid", DevicePlatform.Android, DeviceType.Phone, "Android 15", "0.8.0", DeviceCapability.All);
        var crypto = new CryptographicIdentityProvider(vault, localCtx, new SystemTextJsonSerializer(), _clock);
        var protocol = new NexusPairingProtocolClient(crypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var offlineCreds = new InMemoryCredentialProvider(new RealtimeCredentialBundle(
            Guid.NewGuid(), "Offline", "fake-token", _clock.UtcNow.AddHours(1), "http://127.0.0.1:59991", null));

        var offlineClient = new HttpIntegrationModulesClient(
            protocol,
            offlineCreds,
            new RealtimeClientOptions(),
            NullLogger<HttpIntegrationModulesClient>.Instance);

        var res = await offlineClient.GetRemoteMonitorSnapshotAsync();
        Assert.True(res.IsFailure);
        Assert.Equal(ErrorCategory.Network, res.Error!.Category);
    }

    private async Task<ConnectedModuleNode> CreateNodeAsync(
        HttpClient http,
        CryptographicIdentityProvider hostCrypto,
        string folderName,
        string deviceName,
        DevicePlatform platform,
        NexusPermission permissions,
        string endpointOverride = "http://localhost")
    {
        var dir = Path.Combine(_storageDir, "nodes", folderName);
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
            "0.8.0",
            DeviceCapability.All);

        var serializer = new SystemTextJsonSerializer();
        var clientCrypto = new CryptographicIdentityProvider(vault, localCtx, serializer, _clock);
        var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), serializer, _clock);
        var clientProtocol = new NexusPairingProtocolClient(clientCrypto, new SecureTokenGenerator(), serializer, _clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync(endpointOverride, null, 120)).Value;
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
            endpointOverride,
            null));

        var rtOptions = new RealtimeClientOptions
        {
            AutoReconnect = false,
            DefaultAckTimeout = TimeSpan.FromSeconds(5),
            SignalRMessageHandlerFactory = (_, _) => _factory.Server.CreateHandler()
        };
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState { LocalDevice = localCtx.ToDeviceInfo() });
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

        Assert.True((await realtime.OpenAuthenticatedAsync()).IsSuccess);

        var modulesClient = new HttpIntegrationModulesClient(
            clientProtocol,
            credProvider,
            rtOptions,
            NullLogger<HttpIntegrationModulesClient>.Instance);

        return new ConnectedModuleNode(identity, realtime, modulesClient);
    }

    private CryptographicIdentityProvider CreateCryptoProvider(string folderName, string deviceName, DevicePlatform platform)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(
            Guid.Empty, deviceName, platform, DeviceType.Desktop, "Windows 11", "0.8.0", DeviceCapability.All);
        return new CryptographicIdentityProvider(vault, ctx, new SystemTextJsonSerializer(), _clock);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private sealed class ConnectedModuleNode(
        DeviceCryptographicIdentity identity,
        SignalRRealtimeClient realtime,
        HttpIntegrationModulesClient modulesClient) : IAsyncDisposable
    {
        public DeviceCryptographicIdentity Identity { get; } = identity;
        public SignalRRealtimeClient Realtime { get; } = realtime;
        public HttpIntegrationModulesClient ModulesClient { get; } = modulesClient;

        public async ValueTask DisposeAsync()
            => await Realtime.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryCredentialProvider(RealtimeCredentialBundle bundle) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(bundle));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("Refresh disabled")));
    }

    private sealed class ModulesWebFactory(string storageDir, MutableClock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-server-modules.db",
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
                services.RemoveAll<WindowsControlOptions>();
                services.AddSingleton(new WindowsControlOptions
                {
                    DryRunPowerActions = true,
                    SimulateInputInHeadlessTests = true
                });
            });
        }
    }
}
