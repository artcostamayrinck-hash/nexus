using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Services;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Security;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class ConnectivityIntegrationTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-conn-int-" + Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task SqliteEndpointRepository_PersistsDeduplicatesUpdatesTelemetryAndDeletesAcrossRestart()
    {
        var dbRoot = Path.Combine(_storageDir, "sqlite-ep");
        var endpointId = Guid.CreateVersion7();
        var hostId = Guid.CreateVersion7();

        // First startup: insert LAN + Tailscale endpoints
        {
            var services = new ServiceCollection();
            services.AddNexusInfrastructure(
                configureStorage: s => { s.RootDirectory = dbRoot; s.EnablePooling = false; },
                configureLogging: l => l.EnableConsole = false);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IStorageInitializer>().InitializeAsync();
            var repo = provider.GetRequiredService<IEndpointRegistryRepository>();

            var saveLan = await repo.UpsertAsync(new DeviceEndpointRecord
            {
                EndpointId = endpointId,
                DeviceId = hostId,
                DeviceName = "PC Windows",
                Label = "Wi-Fi Casa",
                RouteKind = ConnectionRouteKind.LocalLan,
                Transport = TransportKind.LocalNetwork,
                EndpointUrl = "https://192.168.1.10:5443/",
                Priority = 10,
                Origin = EndpointOrigin.MdnsDiscovered
            });
            Assert.True(saveLan.IsSuccess);
            Assert.Equal("https://192.168.1.10:5443", saveLan.Value.EndpointUrl);

            // Re-inserting same DeviceId + EndpointUrl deduplicates seamlessly
            var dedup = await repo.UpsertAsync(new DeviceEndpointRecord
            {
                EndpointId = Guid.CreateVersion7(),
                DeviceId = hostId,
                DeviceName = "PC Windows",
                Label = "Wi-Fi Casa Atualizado",
                RouteKind = ConnectionRouteKind.LocalLan,
                Transport = TransportKind.LocalNetwork,
                EndpointUrl = "https://192.168.1.10:5443",
                Priority = 10
            });
            Assert.True(dedup.IsSuccess);
            Assert.Equal(saveLan.Value.EndpointId, dedup.Value.EndpointId);

            await repo.UpdateTelemetryAsync(saveLan.Value.EndpointId, 12, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        }

        // Second startup: verify persisted record & telemetry survive restart
        {
            var services = new ServiceCollection();
            services.AddNexusInfrastructure(
                configureStorage: s => { s.RootDirectory = dbRoot; s.EnablePooling = false; },
                configureLogging: l => l.EnableConsole = false);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IStorageInitializer>().InitializeAsync();
            var repo = provider.GetRequiredService<IEndpointRegistryRepository>();

            var list = await repo.ListAsync(hostId);
            Assert.True(list.IsSuccess);
            var item = Assert.Single(list.Value);
            Assert.Equal("Wi-Fi Casa Atualizado", item.Label);
            Assert.Equal(12, item.LastLatencyMs);
            Assert.NotNull(item.LastConnectedAtUtc);

            Assert.True((await repo.DeleteAsync(item.EndpointId)).IsSuccess);
            Assert.Empty((await repo.ListAsync(hostId)).Value);
        }
    }

    [Fact]
    public async Task ConnectivityEndpoints_RequireAuth_RegisterEndpoints_AndSelectRouteViaServer()
    {
        using var factory = new TestServerFactory(Path.Combine(_storageDir, "server"));
        using var client = factory.CreateClient();

        // Unauthenticated requests must be rejected
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/connectivity/addresses")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/connectivity/status")).StatusCode);

        // Pair mobile client to obtain valid NexusBearer access token + server crypto metadata
        var paired = await PairMobileDeviceAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired.Tokens.AccessToken);

        var addrResp = await client.GetAsync("/api/v1/connectivity/addresses");
        addrResp.EnsureSuccessStatusCode();
        var addresses = (await addrResp.Content.ReadFromJsonAsync<NexusResponse<ServerConnectivityAddressesDto>>(JsonOptions))!.Payload!;
        Assert.Equal("_nexus._tcp.local.", addresses.MdnsServiceType);
        Assert.NotNull(addresses.HostKeyFingerprint);
        Assert.NotNull(addresses.TlsThumbprint);

        // Register a Tailscale endpoint on server registry
        var regReq = new RegisterEndpointRequest
        {
            DeviceId = addresses.DeviceId,
            DeviceName = addresses.DeviceName,
            Label = "Tailscale Casa",
            RouteKind = ConnectionRouteKind.RemoteVpnTailscale,
            EndpointUrl = "https://100.77.22.10:5443",
            ExpectedHostKeyFingerprint = addresses.HostKeyFingerprint,
            ExpectedTlsThumbprint = addresses.TlsThumbprint,
            Origin = EndpointOrigin.TailscalePrivate
        };

        var createResp = await client.PostAsJsonAsync("/api/v1/connectivity/endpoints", regReq, JsonOptions);
        createResp.EnsureSuccessStatusCode();
        var created = (await createResp.Content.ReadFromJsonAsync<NexusResponse<DeviceEndpointRecord>>(JsonOptions))!.Payload!;
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, created.RouteKind);
        Assert.Equal("https://100.77.22.10:5443", created.EndpointUrl);

        var listResp = await client.GetAsync("/api/v1/connectivity/endpoints");
        listResp.EnsureSuccessStatusCode();
        var list = (await listResp.Content.ReadFromJsonAsync<NexusResponse<IReadOnlyList<DeviceEndpointRecord>>>(JsonOptions))!.Payload!;
        Assert.Single(list);

        var statusResp = await client.GetAsync("/api/v1/connectivity/status");
        statusResp.EnsureSuccessStatusCode();
        var status = (await statusResp.Content.ReadFromJsonAsync<NexusResponse<ConnectivityStatusReport>>(JsonOptions))!.Payload!;
        Assert.Single(status.KnownEndpoints);

        // Delete endpoint cleanly
        var delResp = await client.DeleteAsync($"/api/v1/connectivity/endpoints/{created.EndpointId:D}");
        delResp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task RouteProber_RejectsSpoofedServerWithMismatchedCryptographicFingerprint()
    {
        using var factory = new TestServerFactory(Path.Combine(_storageDir, "spoof"));
        using var serverHttpClient = factory.CreateClient();

        // Point prober directly at the real TestServer /api/v1/system/info,
        // but require an expected host fingerprint from a different trusted machine.
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s => { s.RootDirectory = Path.Combine(_storageDir, "probe-client"); s.EnablePooling = false; },
            configureLogging: l => l.EnableConsole = false);
        services.AddSingleton(sp => new NexusPairingProtocolClient(
            sp.GetRequiredService<ICryptographicIdentityProvider>(),
            sp.GetRequiredService<ITokenGenerator>(),
            sp.GetRequiredService<IJsonSerializer>(),
            sp.GetRequiredService<IClock>(),
            customHttpClientFactory: (_, _) => factory.CreateClient()));

        await using var provider = services.BuildServiceProvider();
        var prober = provider.GetRequiredService<IRouteProber>();

        var net = new NetworkEnvironmentSnapshot
        {
            IsNetworkAvailable = true,
            HasInternetAccess = true,
            VpnStatus = VpnStatus.Connected
        };

        // Case 1: Spoofed/different HostKeyFingerprint -> MUST fail with IdentityMismatch!
        var mismatchedEndpoint = new DeviceEndpointRecord
        {
            EndpointId = Guid.CreateVersion7(),
            DeviceId = Guid.Empty,
            DeviceName = "Spoofed Tailscale Node",
            Label = "Tailscale",
            RouteKind = ConnectionRouteKind.RemoteVpnTailscale,
            Transport = TransportKind.SignalR,
            EndpointUrl = "https://100.99.99.99:5443",
            ExpectedHostKeyFingerprint = "0000000000000000000000000000000000000000000000000000000000000000"
        };

        var spoofProbe = await prober.ProbeAsync(mismatchedEndpoint, net);
        Assert.False(spoofProbe.IsReachable);
        Assert.Equal(RouteFailureReason.IdentityMismatch, spoofProbe.FailureReason);

        // Case 2: Valid matching fingerprint -> Succeeds and captures latency!
        var sysInfo = (await serverHttpClient.GetFromJsonAsync<NexusResponse<Nexus.Server.Endpoints.SystemInfoDto>>("/api/v1/system/info", JsonOptions))!.Payload!;
        var legitimateEndpoint = mismatchedEndpoint with
        {
            DeviceId = sysInfo.HostDevice.Id,
            ExpectedHostKeyFingerprint = sysInfo.HostKeyFingerprint
        };

        var validProbe = await prober.ProbeAsync(legitimateEndpoint, net);
        Assert.True(validProbe.IsReachable);
        Assert.Equal(RouteFailureReason.None, validProbe.FailureReason);
        Assert.True(validProbe.LatencyMs >= 1);
    }

    private async Task<(DeviceCryptographicIdentity ClientIdentity, DeviceAuthTokenPair Tokens)> PairMobileDeviceAsync(HttpClient client)
    {
        var clock = new SystemClock();
        var hostVault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions { VaultDirectory = Path.Combine(_storageDir, "host-vault") });
        var hostCrypto = new CryptographicIdentityProvider(hostVault, new DefaultLocalDeviceContext(Guid.Empty, "WinHost", DevicePlatform.Windows, DeviceType.Desktop, "Win11", "0.4.0", DeviceCapability.All), new SystemTextJsonSerializer(), clock);

        var mobVault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions { VaultDirectory = Path.Combine(_storageDir, "mob-vault") });
        var mobCrypto = new CryptographicIdentityProvider(mobVault, new DefaultLocalDeviceContext(Guid.Empty, "AndroidTest", DevicePlatform.Android, DeviceType.Phone, "Android 15", "0.4.0", DeviceCapability.All), new SystemTextJsonSerializer(), clock);

        var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), clock);
        var mobProtocol = new NexusPairingProtocolClient(mobCrypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, 120)).Value;
        var createResp = await client.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions);
        createResp.EnsureSuccessStatusCode();
        var snap = (await createResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;

        var qr = mobProtocol.ParseAndVerifyQrPayload(snap.QrPayloadJson!).Value;
        var claim = (await mobProtocol.BuildClaimRequestAsync(qr, NexusPermission.All)).Value;
        (await client.PostAsJsonAsync("/api/v1/pairing/claim", claim.Request, JsonOptions)).EnsureSuccessStatusCode();

        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: true, NexusPermission.All)).Value;
        (await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions)).EnsureSuccessStatusCode();

        var finalizeReq = (await mobProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeReq, JsonOptions);
        finalizeResp.EnsureSuccessStatusCode();
        var tokens = (await finalizeResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;
        return ((await mobCrypto.GetOrCreateIdentityAsync()).Value, tokens);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storageDir))
            {
                Directory.Delete(_storageDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup locks
        }
    }

    private sealed class TestServerFactory(string storageDir) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-conn-test.db",
                    ["Nexus:Storage:DisablePooling"] = "true",
                    ["Nexus:Tls:BindKestrelHttps"] = "false",
                    ["Nexus:Tls:RequireHttps"] = "false",
                    ["Nexus:Security:EnableLocalHostBridge"] = "false",
                    ["Nexus:Discovery:EnableMdns"] = "false"
                });
            });
        }
    }
}

