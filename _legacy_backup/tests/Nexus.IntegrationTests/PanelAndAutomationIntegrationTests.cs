using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Infrastructure.Automation;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Modules;
using Nexus.Infrastructure.Security;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class PanelAndAutomationIntegrationTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-p9-int-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 19, 0, 0, TimeSpan.Zero));
    private readonly PanelWebFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public PanelAndAutomationIntegrationTests()
    {
        _factory = new PanelWebFactory(_storageDir, _clock);
    }

    [Fact]
    public async Task PanelProfiles_SyncAcrossHttpAndBroadcastOverSignalR()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-pan", "NEXUS Win", DevicePlatform.Windows);

        await using var winNode = await CreateNodeAsync(http, host, "win-pan", "Windows Host", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateNodeAsync(http, host, "and-pan", "Android Phone", DevicePlatform.Android, NexusPermission.All);

        var receivedProfileTcs = new TaskCompletionSource<PanelProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.PanelProfileUpdatedReceived += (_, profile) => receivedProfileTcs.TrySetResult(profile);

        var listRes = await androidNode.PanelClient.GetRemoteProfilesAsync();
        Assert.True(listRes.IsSuccess, listRes.Error?.Message);
        Assert.True(listRes.Value.Count >= 2);

        var targetProfile = listRes.Value[1];
        var customized = targetProfile with
        {
            Theme = PanelThemePreset.SlateGlass,
            Accent = PanelAccentColor.Violet,
            GridColumns = 3
        };

        var saved = await androidNode.PanelClient.SaveRemoteProfileAsync(customized);
        Assert.True(saved.IsSuccess, saved.Error?.Message);

        var relayed = await receivedProfileTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(customized.ProfileId, relayed.ProfileId);
        Assert.Equal(PanelThemePreset.SlateGlass, relayed.Theme);
        Assert.Equal(PanelAccentColor.Violet, relayed.Accent);
        Assert.Equal(3, relayed.GridColumns);
    }

    [Fact]
    public async Task AutomationEndpoints_EnforcePermissionsAndBroadcastTriggerExecutions()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-auto", "NEXUS Win", DevicePlatform.Windows);

        // Node without Automation permission is blocked (403 Forbidden)
        await using var viewOnlyMobile = await CreateNodeAsync(
            http,
            host,
            "mob-viewonly",
            "Android ReadOnly",
            DevicePlatform.Android,
            NexusPermission.ViewStatus);

        var forbiddenRules = await viewOnlyMobile.PanelClient.GetRemoteRulesAsync();
        Assert.True(forbiddenRules.IsFailure);
        Assert.Equal(ErrorCategory.Forbidden, forbiddenRules.Error!.Category);
        Assert.Equal("PERMISSION_DENIED_AUTOMATION", forbiddenRules.Error.Code);

        // Node with Automation + ViewStatus (no SystemPower) vs Full node
        await using var autoNoPowerMobile = await CreateNodeAsync(
            http,
            host,
            "mob-autopower",
            "Android AutoNoPower",
            DevicePlatform.Android,
            NexusPermission.ViewStatus | NexusPermission.Automation | NexusPermission.ControlInput);

        await using var winNode = await CreateNodeAsync(
            http,
            host,
            "win-auto",
            "Windows Host",
            DevicePlatform.Windows,
            NexusPermission.All);

        await using var fullMobile = await CreateNodeAsync(
            http,
            host,
            "mob-fullauto",
            "Android Full",
            DevicePlatform.Android,
            NexusPermission.All);

        var execTcs = new TaskCompletionSource<AutomationExecutionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.AutomationExecutionReceived += (_, rec) => execTcs.TrySetResult(rec);

        var profiles = (await fullMobile.PanelClient.GetRemoteProfilesAsync()).Value;
        var profileRule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Conectar -> Trocar Perfil Remoto",
            Trigger = AutomationTriggerType.ConnectedToComputer,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.ActivatePanelProfile,
                    TargetProfileId = profiles[1].ProfileId,
                    Label = "Ativar perfil 2"
                }
            ]
        };

        var savedRule = await fullMobile.PanelClient.SaveRemoteRuleAsync(profileRule);
        Assert.True(savedRule.IsSuccess, savedRule.Error?.Message);

        var dispatch = await fullMobile.PanelClient.DispatchRemoteTriggerAsync(new AutomationTriggerDispatchRequest
        {
            RuleId = profileRule.RuleId,
            Trigger = AutomationTriggerType.ConnectedToComputer
        });
        Assert.True(dispatch.IsSuccess, dispatch.Error?.Message);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, Assert.Single(dispatch.Value).Outcome);

        var relayedExec = await execTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(profileRule.RuleId, relayedExec.RuleId);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, relayedExec.Outcome);

        // High-impact rule tested by node without SystemPower returns BlockedPermission cleanly
        var powerRule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "Desligamento remoto",
            Trigger = AutomationTriggerType.Manual,
            IsSensitiveAuthorized = true,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.ExecuteControlCommand,
                    ControlAction = RemoteControlActionType.PowerShutdown,
                    IsSensitiveAction = true
                }
            ]
        };
        Assert.True((await fullMobile.PanelClient.SaveRemoteRuleAsync(powerRule)).IsSuccess);

        var deniedExec = await autoNoPowerMobile.PanelClient.DispatchRemoteTriggerAsync(new AutomationTriggerDispatchRequest
        {
            RuleId = powerRule.RuleId,
            ApprovedSensitiveExecution = true
        });
        Assert.True(deniedExec.IsSuccess);
        Assert.Equal(AutomationExecutionOutcome.BlockedPermission, Assert.Single(deniedExec.Value).Outcome);

        var remoteHistory = await fullMobile.PanelClient.GetRemoteHistoryAsync(50);
        Assert.True(remoteHistory.IsSuccess);
        Assert.Contains(remoteHistory.Value, h => h.RuleId == profileRule.RuleId && h.Outcome == AutomationExecutionOutcome.Succeeded);
        Assert.Contains(remoteHistory.Value, h => h.RuleId == powerRule.RuleId && h.Outcome == AutomationExecutionOutcome.BlockedPermission);
    }

    [Fact]
    public async Task OfflinePanelClient_FailsGracefullyWithoutCrashing()
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "offline-vault")
        });
        var localCtx = new DefaultLocalDeviceContext(
            Guid.NewGuid(), "OfflineAndroid", DevicePlatform.Android, DeviceType.Phone, "Android 15", "0.9.0", DeviceCapability.All);
        var crypto = new CryptographicIdentityProvider(vault, localCtx, new SystemTextJsonSerializer(), _clock);
        var protocol = new NexusPairingProtocolClient(crypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var offlineCreds = new InMemoryCredentialProvider(new RealtimeCredentialBundle(
            Guid.NewGuid(), "Offline", "fake-token", _clock.UtcNow.AddHours(1), "http://127.0.0.1:59992", null));

        var offlineClient = new HttpPanelAndAutomationClient(
            protocol,
            offlineCreds,
            new RealtimeClientOptions(),
            NullLogger<HttpPanelAndAutomationClient>.Instance);

        var res = await offlineClient.GetRemoteRulesAsync();
        Assert.True(res.IsFailure);
        Assert.Equal(ErrorCategory.Network, res.Error!.Category);
    }

    private async Task<ConnectedPanelNode> CreateNodeAsync(
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
            "0.9.0",
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

        var panelClient = new HttpPanelAndAutomationClient(
            clientProtocol,
            credProvider,
            rtOptions,
            NullLogger<HttpPanelAndAutomationClient>.Instance);

        return new ConnectedPanelNode(identity, realtime, panelClient);
    }

    private CryptographicIdentityProvider CreateCryptoProvider(string folderName, string deviceName, DevicePlatform platform)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(
            Guid.Empty, deviceName, platform, DeviceType.Desktop, "Windows 11", "0.9.0", DeviceCapability.All);
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

    private sealed class ConnectedPanelNode(
        DeviceCryptographicIdentity identity,
        SignalRRealtimeClient realtime,
        HttpPanelAndAutomationClient panelClient) : IAsyncDisposable
    {
        public DeviceCryptographicIdentity Identity { get; } = identity;
        public SignalRRealtimeClient Realtime { get; } = realtime;
        public HttpPanelAndAutomationClient PanelClient { get; } = panelClient;

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

    private sealed class PanelWebFactory(string storageDir, MutableClock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-server-p9.db",
                    ["Nexus:Storage:EnablePooling"] = "false",
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
                    SimulateInputInHeadlessTests = true,
                    DryRunApprovedAppLaunches = true
                });
            });
        }
    }
}
