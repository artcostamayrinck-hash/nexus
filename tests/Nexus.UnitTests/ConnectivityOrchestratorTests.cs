using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Messaging;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Modules.BuiltIn;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Desktop.Services.DependencyInjection;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Mobile.Services.DependencyInjection;
using Nexus.Mobile.Services.ViewModels;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.UnitTests;

public sealed class ConnectivityOrchestratorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-conn-unit-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new SystemTextJsonSerializer().Options;

    [Fact]
    public void ContractValidator_ValidatesDeviceEndpointAndMdnsRecords()
    {
        var validEndpoint = new DeviceEndpointRecord
        {
            EndpointId = Guid.CreateVersion7(),
            DeviceId = Guid.CreateVersion7(),
            DeviceName = "Windows Desktop",
            Label = "Tailscale",
            RouteKind = ConnectionRouteKind.RemoteVpnTailscale,
            EndpointUrl = "https://100.70.10.20:5443"
        };
        Assert.True(ContractValidator.Validate(validEndpoint).IsValid);

        var invalidEndpoint = validEndpoint with { RouteKind = ConnectionRouteKind.Unknown, EndpointUrl = "ftp://invalid" };
        Assert.False(ContractValidator.Validate(invalidEndpoint).IsValid);

        var validMdns = new MdnsDiscoveredEndpoint
        {
            ServiceDeviceId = Guid.CreateVersion7(),
            ServiceName = "PC",
            HostName = "pc.local",
            EndpointUrl = "https://192.168.1.10:5443",
            Port = 5443
        };
        Assert.True(ContractValidator.Validate(validMdns).IsValid);
    }

    [Fact]
    public void NetworkAddressClassifier_DistinguishesLanAndTailscaleAddresses()
    {
        Assert.Equal(ConnectionRouteKind.LocalLan, NetworkAddressClassifier.ClassifyEndpoint("https://192.168.1.42:5443"));
        Assert.Equal(ConnectionRouteKind.LocalLan, NetworkAddressClassifier.ClassifyEndpoint("https://10.0.0.12:5443"));
        Assert.Equal(ConnectionRouteKind.LocalLan, NetworkAddressClassifier.ClassifyEndpoint("https://meu-pc.local:5443"));
        Assert.Equal(ConnectionRouteKind.LocalLan, NetworkAddressClassifier.ClassifyEndpoint("http://127.0.0.1:5280"));

        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, NetworkAddressClassifier.ClassifyEndpoint("https://100.64.0.15:5443"));
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, NetworkAddressClassifier.ClassifyEndpoint("https://100.101.45.88:5443"));
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, NetworkAddressClassifier.ClassifyEndpoint("https://100.127.255.10:5443"));
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, NetworkAddressClassifier.ClassifyEndpoint("https://[fd7a:115c:a1e0::1]:5443"));
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, NetworkAddressClassifier.ClassifyEndpoint("https://workstation.tailnet-abc.ts.net:5443"));

        Assert.Equal(ConnectionRouteKind.RemotePrivate, NetworkAddressClassifier.ClassifyEndpoint("https://203.0.113.20:5443"));
        Assert.Equal(ConnectionRouteKind.Unknown, NetworkAddressClassifier.ClassifyEndpoint("not-a-valid-uri"));
    }

    [Fact]
    public async Task MdnsDiscovery_EncodesQueryAndDecodesAnnouncementPacket()
    {
        var hostId = Guid.CreateVersion7();
        var serverTransport = new InMemoryMdnsBusTransport();
        var clientTransport = new InMemoryMdnsBusTransport(serverTransport);

        var clock = new SystemClock();
        var eventBus = new InProcessEventBus();

        await using var advertiser = new MdnsServiceDiscovery(serverTransport, clock, eventBus, NullLogger<MdnsServiceDiscovery>.Instance);
        await using var browser = new MdnsServiceDiscovery(clientTransport, clock, eventBus, NullLogger<MdnsServiceDiscovery>.Instance);

        var advertised = new MdnsDiscoveredEndpoint
        {
            ServiceDeviceId = hostId,
            ServiceName = "Estação Windows",
            HostName = "win-desktop",
            EndpointUrl = "https://192.168.1.50:5443",
            Port = 5443,
            TlsThumbprint = "ABCDEF0123456789",
            HostKeyFingerprint = "FEEDFACE1234"
        };

        var start = await advertiser.StartAdvertisingAsync(advertised);
        Assert.True(start.IsSuccess);
        Assert.True(advertiser.IsAdvertising);

        var discovered = await browser.DiscoverAsync(TimeSpan.FromMilliseconds(350));
        Assert.True(discovered.IsSuccess);
        var match = Assert.Single(discovered.Value);
        Assert.Equal(hostId, match.ServiceDeviceId);
        Assert.Equal("Estação Windows", match.ServiceName);
        Assert.Equal("https://192.168.1.50:5443", match.EndpointUrl);
        Assert.Equal("ABCDEF0123456789", match.TlsThumbprint);
        Assert.Equal("FEEDFACE1234", match.HostKeyFingerprint);
    }

    [Fact]
    public async Task Orchestrator_SameWifi_PrioritizesLocalLanOverTailscale()
    {
        var hostId = Guid.CreateVersion7();
        var adapter = new SimulatedNetworkAdapter
        {
            Snapshot = new NetworkEnvironmentSnapshot
            {
                IsNetworkAvailable = true,
                HasInternetAccess = true,
                HasLocalLanInterface = true,
                VpnStatus = VpnStatus.Connected,
                TailscaleAddresses = ["100.88.1.10"],
                LocalIpv4Addresses = ["192.168.1.25"]
            }
        };

        var reachability = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://192.168.1.50:5443"] = true,
            ["https://100.88.1.50:5443"] = true
        };

        await using var harness = await CreateHarnessAsync(hostId, "PC Casa", adapter, reachability);
        await RegisterLanAndTailscaleAsync(harness.Orchestrator, hostId);

        var selected = await harness.Orchestrator.SelectAndConnectBestRouteAsync();
        Assert.True(selected.IsSuccess);
        Assert.Equal(ConnectionState.Connected, selected.Value.State);
        Assert.Equal(ConnectionRouteKind.LocalLan, selected.Value.RouteKind);
        Assert.Equal("https://192.168.1.50:5443", selected.Value.ActiveEndpoint);
        Assert.Equal(VpnStatus.Connected, selected.Value.VpnStatus);
        Assert.NotNull(selected.Value.LatencyMs);
    }

    [Fact]
    public async Task Orchestrator_CellularWithTailscaleConnected_SelectsRemoteVpnRoute()
    {
        var hostId = Guid.CreateVersion7();
        var adapter = new SimulatedNetworkAdapter
        {
            Snapshot = new NetworkEnvironmentSnapshot
            {
                IsNetworkAvailable = true,
                HasInternetAccess = true,
                HasLocalLanInterface = false,
                IsCellularActive = true,
                VpnStatus = VpnStatus.Connected,
                VpnProvider = "Tailscale",
                TailscaleAddresses = ["100.88.2.99"]
            }
        };

        var reachability = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://192.168.1.50:5443"] = false,
            ["https://100.88.1.50:5443"] = true
        };

        await using var harness = await CreateHarnessAsync(hostId, "PC Escritório", adapter, reachability);
        await RegisterLanAndTailscaleAsync(harness.Orchestrator, hostId);

        var selected = await harness.Orchestrator.SelectAndConnectBestRouteAsync();
        Assert.True(selected.IsSuccess);
        Assert.Equal(ConnectionState.Connected, selected.Value.State);
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, selected.Value.RouteKind);
        Assert.Equal("https://100.88.1.50:5443", selected.Value.ActiveEndpoint);
        Assert.Equal("PC Escritório", selected.Value.DeviceName);
    }

    [Fact]
    public async Task Orchestrator_TailscaleDisconnected_ReportsClearVpnUnavailableReason()
    {
        var hostId = Guid.CreateVersion7();
        var adapter = new SimulatedNetworkAdapter
        {
            Snapshot = new NetworkEnvironmentSnapshot
            {
                IsNetworkAvailable = true,
                HasInternetAccess = true,
                HasLocalLanInterface = false,
                IsCellularActive = true,
                VpnStatus = VpnStatus.Disconnected,
                VpnProvider = "Tailscale"
            }
        };

        var reachability = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://192.168.1.50:5443"] = false,
            ["https://100.88.1.50:5443"] = false
        };

        await using var harness = await CreateHarnessAsync(hostId, "PC Escritório", adapter, reachability);
        await RegisterLanAndTailscaleAsync(harness.Orchestrator, hostId);

        var selected = await harness.Orchestrator.SelectAndConnectBestRouteAsync();
        Assert.True(selected.IsFailure);
        Assert.Equal(RouteFailureReason.VpnUnavailable, harness.Orchestrator.CurrentStatus.LastFailureReason);
        Assert.Contains("Tailscale", harness.Orchestrator.CurrentStatus.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Orchestrator_WifiToCellularSwitch_FailsOverWithoutBreakingAndDefersPromotionDuringActiveOperation()
    {
        var hostId = Guid.CreateVersion7();
        var adapter = new SimulatedNetworkAdapter
        {
            Snapshot = new NetworkEnvironmentSnapshot
            {
                IsNetworkAvailable = true,
                HasInternetAccess = true,
                HasLocalLanInterface = true,
                VpnStatus = VpnStatus.Connected,
                TailscaleAddresses = ["100.88.2.99"],
                LocalIpv4Addresses = ["192.168.1.25"]
            }
        };

        var reachability = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://192.168.1.50:5443"] = true,
            ["https://100.88.1.50:5443"] = true
        };

        await using var harness = await CreateHarnessAsync(hostId, "PC Casa", adapter, reachability);
        await RegisterLanAndTailscaleAsync(harness.Orchestrator, hostId);

        // Step 1: On Wi-Fi -> selects Local LAN
        var initial = await harness.Orchestrator.SelectAndConnectBestRouteAsync();
        Assert.True(initial.IsSuccess);
        Assert.Equal(ConnectionRouteKind.LocalLan, initial.Value.RouteKind);

        // Step 2: Walk out onto 4G/5G -> LAN becomes unavailable -> failover to Tailscale
        reachability["https://192.168.1.50:5443"] = false;
        adapter.Snapshot = new NetworkEnvironmentSnapshot
        {
            IsNetworkAvailable = true,
            HasInternetAccess = true,
            HasLocalLanInterface = false,
            IsCellularActive = true,
            VpnStatus = VpnStatus.Connected,
            TailscaleAddresses = ["100.88.2.99"]
        };

        var cellularFailover = await harness.Orchestrator.HandleNetworkChangedAsync();
        Assert.True(cellularFailover.IsSuccess);
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, cellularFailover.Value.RouteKind);

        // Step 3: Start an active transfer operation -> return home (LAN recovers), route switch must defer!
        var leaseResult = await harness.Orchestrator.BeginOperationScopeAsync("file_transfer");
        Assert.True(leaseResult.IsSuccess);

        reachability["https://192.168.1.50:5443"] = true;
        adapter.Snapshot = new NetworkEnvironmentSnapshot
        {
            IsNetworkAvailable = true,
            HasInternetAccess = true,
            HasLocalLanInterface = true,
            VpnStatus = VpnStatus.Connected,
            TailscaleAddresses = ["100.88.2.99"],
            LocalIpv4Addresses = ["192.168.1.25"]
        };

        var deferred = await harness.Orchestrator.HandleNetworkChangedAsync();
        Assert.True(deferred.IsSuccess);
        Assert.True(deferred.Value.HandoverDeferred);
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, deferred.Value.RouteKind);
        Assert.Equal(1, deferred.Value.ActiveOperationsCount);

        // Step 4: Finish operation -> deferred promotion to Local LAN completes automatically!
        await leaseResult.Value.DisposeAsync();
        for (var i = 0; i < 20 && harness.Orchestrator.CurrentStatus.RouteKind != ConnectionRouteKind.LocalLan; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(ConnectionRouteKind.LocalLan, harness.Orchestrator.CurrentStatus.RouteKind);
        Assert.False(harness.Orchestrator.CurrentStatus.HandoverDeferred);
    }

    [Fact]
    public async Task Orchestrator_InternetLossAndRecovery_ReconnectsAutomatically()
    {
        var hostId = Guid.CreateVersion7();
        var adapter = new SimulatedNetworkAdapter
        {
            Snapshot = new NetworkEnvironmentSnapshot
            {
                IsNetworkAvailable = true,
                HasInternetAccess = true,
                HasLocalLanInterface = false,
                IsCellularActive = true,
                VpnStatus = VpnStatus.Connected,
                TailscaleAddresses = ["100.88.2.99"]
            }
        };

        var reachability = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://192.168.1.50:5443"] = false,
            ["https://100.88.1.50:5443"] = true
        };

        var fastOptions = new ConnectivityOptions
        {
            MonitorInterval = TimeSpan.FromMilliseconds(75),
            EnableBackgroundMonitor = true
        };

        await using var harness = await CreateHarnessAsync(hostId, "PC Casa", adapter, reachability, fastOptions);
        await RegisterLanAndTailscaleAsync(harness.Orchestrator, hostId);

        var initial = await harness.Orchestrator.SelectAndConnectBestRouteAsync();
        Assert.True(initial.IsSuccess);
        Assert.Equal(ConnectionState.Connected, initial.Value.State);

        // Drop internet completely
        adapter.Snapshot = new NetworkEnvironmentSnapshot
        {
            IsNetworkAvailable = false,
            HasInternetAccess = false,
            HasLocalLanInterface = false,
            VpnStatus = VpnStatus.Unavailable
        };

        for (var i = 0; i < 30 && harness.Orchestrator.CurrentStatus.State == ConnectionState.Connected; i++)
        {
            await Task.Delay(40);
        }

        Assert.Equal(ConnectionState.Reconnecting, harness.Orchestrator.CurrentStatus.State);
        Assert.Equal(RouteFailureReason.InternetUnavailable, harness.Orchestrator.CurrentStatus.LastFailureReason);

        // Restore network & Tailscale -> monitor automatically restores Connected state
        adapter.Snapshot = new NetworkEnvironmentSnapshot
        {
            IsNetworkAvailable = true,
            HasInternetAccess = true,
            HasLocalLanInterface = false,
            IsCellularActive = true,
            VpnStatus = VpnStatus.Connected,
            TailscaleAddresses = ["100.88.2.99"]
        };

        for (var i = 0; i < 40 && harness.Orchestrator.CurrentStatus.State != ConnectionState.Connected; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(ConnectionState.Connected, harness.Orchestrator.CurrentStatus.State);
        Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, harness.Orchestrator.CurrentStatus.RouteKind);
    }

    [Fact]
    public async Task Orchestrator_RejectsInvalidEndpoint_And_FutureAbstractionsResolveCleanly()
    {
        var hostId = Guid.CreateVersion7();
        var adapter = new SimulatedNetworkAdapter();
        var reachability = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        await using var harness = await CreateHarnessAsync(hostId, "PC", adapter, reachability);
        var invalid = await harness.Orchestrator.RegisterEndpointAsync(new RegisterEndpointRequest
        {
            DeviceId = hostId,
            DeviceName = "PC",
            Label = "Inválido",
            RouteKind = ConnectionRouteKind.RemoteVpnTailscale,
            EndpointUrl = "ftp://endpoint-invalido"
        });

        Assert.True(invalid.IsFailure);
        Assert.Equal(ErrorCategory.Validation, invalid.Error!.Category);

        // Verify Desktop & Mobile DI resolve future transport abstractions with clean placeholder behavior
        var spServices = new ServiceCollection();
        spServices.AddNexusInfrastructure(
            configureStorage: s => { s.RootDirectory = Path.Combine(_tempDir, "future-di"); s.EnablePooling = false; },
            configureLogging: l => l.EnableConsole = false);
        spServices.AddNexusModules();
        spServices.AddNexusDesktopServices();
        spServices.AddNexusMobileServices();

        await using var sp = spServices.BuildServiceProvider();
        Assert.Equal("SIGNALING_NOT_CONFIGURED", (await sp.GetRequiredService<ISignalingChannel>().ConnectAsync(new Uri("wss://signal.example.com"))).Error?.Code);
        Assert.Equal("RELAY_NOT_CONFIGURED", (await sp.GetRequiredService<IRelayTransport>().AllocateRelaySessionAsync(Guid.CreateVersion7())).Error?.Code);
        Assert.Equal("VPS_GATEWAY_NOT_CONFIGURED", (await sp.GetRequiredService<IVpsGatewayProvider>().ResolveGatewayAsync()).Error?.Code);
        Assert.Equal("NAT_PROBE_NOT_CONFIGURED", (await sp.GetRequiredService<INatTraversalProbe>().ProbeNatAsync()).Error?.Code);
        Assert.Equal("STUN_TURN_NOT_CONFIGURED", (await sp.GetRequiredService<IStunTurnClient>().GatherCandidatesAsync([])).Error?.Code);

        var desktopVm = sp.GetRequiredService<ConnectivityViewModel>();
        var mobileVm = sp.GetRequiredService<MobileConnectivityViewModel>();
        await desktopVm.InitializeAsync();
        await mobileVm.InitializeAsync();
        Assert.NotNull(desktopVm.ConnectionStateText);
        Assert.NotNull(mobileVm.ConnectionStateText);
    }

    private static async Task RegisterLanAndTailscaleAsync(INexusRouteOrchestrator orchestrator, Guid hostId)
    {
        Assert.True((await orchestrator.RegisterEndpointAsync(new RegisterEndpointRequest
        {
            DeviceId = hostId,
            DeviceName = "PC Casa",
            Label = "Wi-Fi Local",
            RouteKind = ConnectionRouteKind.LocalLan,
            EndpointUrl = "https://192.168.1.50:5443",
            ExpectedHostKeyFingerprint = "FINGERPRINT_HOST_123",
            Origin = EndpointOrigin.MdnsDiscovered
        })).IsSuccess);

        Assert.True((await orchestrator.RegisterEndpointAsync(new RegisterEndpointRequest
        {
            DeviceId = hostId,
            DeviceName = "PC Casa",
            Label = "Tailscale VPN",
            RouteKind = ConnectionRouteKind.RemoteVpnTailscale,
            EndpointUrl = "https://100.88.1.50:5443",
            ExpectedHostKeyFingerprint = "FINGERPRINT_HOST_123",
            Origin = EndpointOrigin.TailscalePrivate
        })).IsSuccess);
    }

    private async Task<OrchestratorHarness> CreateHarnessAsync(
        Guid hostId,
        string hostName,
        SimulatedNetworkAdapter adapter,
        ConcurrentDictionary<string, bool> reachability,
        ConnectivityOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_tempDir, "orch-" + Guid.NewGuid().ToString("N"));
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false);

        services.AddSingleton<INetworkConnectivityAdapter>(adapter);
        services.AddSingleton(options ?? new ConnectivityOptions { EnableBackgroundMonitor = false });

        services.AddSingleton(sp => new NexusPairingProtocolClient(
            sp.GetRequiredService<ICryptographicIdentityProvider>(),
            sp.GetRequiredService<ITokenGenerator>(),
            sp.GetRequiredService<IJsonSerializer>(),
            sp.GetRequiredService<IClock>(),
            customHttpClientFactory: (baseEndpoint, _) =>
            {
                var handler = new SimulatedHostHttpHandler(baseEndpoint, hostId, hostName, "FINGERPRINT_HOST_123", reachability);
                return new HttpClient(handler, disposeHandler: true)
                {
                    BaseAddress = new Uri(baseEndpoint.TrimEnd('/') + "/", UriKind.Absolute)
                };
            }));

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<Nexus.Core.Storage.IStorageInitializer>().InitializeAsync();
        var orchestrator = provider.GetRequiredService<INexusRouteOrchestrator>();
        await orchestrator.InitializeAsync();
        return new OrchestratorHarness(provider, orchestrator);
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
            // Ignore transient test directory cleanup locks
        }
    }

    private sealed class OrchestratorHarness(ServiceProvider provider, INexusRouteOrchestrator orchestrator) : IAsyncDisposable
    {
        public INexusRouteOrchestrator Orchestrator { get; } = orchestrator;
        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class SimulatedNetworkAdapter : INetworkConnectivityAdapter
    {
        public NetworkEnvironmentSnapshot Snapshot { get; set; } = new()
        {
            IsNetworkAvailable = true,
            HasInternetAccess = true,
            HasLocalLanInterface = true,
            VpnStatus = VpnStatus.Connected
        };

        public NetworkEnvironmentSnapshot CaptureSnapshot() => Snapshot;
    }

    private sealed class SimulatedHostHttpHandler(
        string baseEndpoint,
        Guid hostId,
        string hostName,
        string fingerprint,
        ConcurrentDictionary<string, bool> reachability) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var normalizedBase = baseEndpoint.Trim().TrimEnd('/');
            if (!reachability.TryGetValue(normalizedBase, out var up) || !up)
            {
                throw new HttpRequestException($"Simulated unreachable host: {normalizedBase}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("api/v1/system/info", StringComparison.OrdinalIgnoreCase) == true)
            {
                var payload = NexusResponse<object>.Success(new
                {
                    DeviceId = hostId,
                    DeviceName = hostName,
                    TlsThumbprint = "SIMULATED_THUMBPRINT",
                    HostKeyFingerprint = fingerprint
                });
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class InMemoryMdnsBusTransport : IMdnsPacketTransport
    {
        private readonly ConcurrentQueue<ReadOnlyMemory<byte>> _inbox = new();
        private readonly InMemoryMdnsBusTransport? _peer;
        private InMemoryMdnsBusTransport? _linkedPeer;

        public InMemoryMdnsBusTransport(InMemoryMdnsBusTransport? peer = null)
        {
            _peer = peer;
            if (peer is not null)
            {
                peer._linkedPeer = this;
            }
        }

        public ValueTask StartListeningAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
        {
            (_peer ?? _linkedPeer)?._inbox.Enqueue(datagram.ToArray());
            return Task.CompletedTask;
        }

        public async Task<ReadOnlyMemory<byte>?> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (_inbox.TryDequeue(out var packet))
                {
                    return packet;
                }

                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
