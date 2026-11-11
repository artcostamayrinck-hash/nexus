using Nexus.Contracts.Connections;
using Nexus.Core.Connections;
using Nexus.Security.Identity;
using Nexus.Security.Tls;

namespace Nexus.Server.Services;

/// <summary>
/// Advertises the running NEXUS Server on the local network via mDNS (_nexus._tcp.local.)
/// so Android devices on the same Wi-Fi/LAN can auto-discover the host endpoint.
/// </summary>
public sealed class NexusMdnsAdvertisementService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMdnsServiceDiscovery _mdnsDiscovery;
    private readonly INetworkEnvironmentInspector _networkInspector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NexusMdnsAdvertisementService> _logger;

    public NexusMdnsAdvertisementService(
        IServiceProvider serviceProvider,
        IMdnsServiceDiscovery mdnsDiscovery,
        INetworkEnvironmentInspector networkInspector,
        IConfiguration configuration,
        ILogger<NexusMdnsAdvertisementService> logger)
    {
        _serviceProvider = serviceProvider;
        _mdnsDiscovery = mdnsDiscovery;
        _networkInspector = networkInspector;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Nexus:Discovery:EnableMdns", true))
        {
            _logger.LogInformation("mDNS local service advertisement is disabled by configuration.");
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var identityProvider = scope.ServiceProvider.GetRequiredService<ICryptographicIdentityProvider>();
            var tlsProvider = scope.ServiceProvider.GetRequiredService<ITlsCertificateProvider>();

            var identityResult = await identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (identityResult.IsFailure)
            {
                _logger.LogWarning("Skipping mDNS advertisement because host identity is unavailable.");
                return;
            }

            var net = await _networkInspector.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var bindHttps = _configuration.GetValue("Nexus:Tls:BindKestrelHttps", false);
            var httpsPort = _configuration.GetValue("Nexus:Tls:HttpsPort", 5443);
            var httpPort = _configuration.GetValue("Nexus:Tls:HttpPort", 5280);
            var scheme = bindHttps ? "https" : "http";
            var port = bindHttps ? httpsPort : httpPort;

            var lanHost = net.LocalIpv4Addresses.FirstOrDefault() ?? $"{Environment.MachineName.ToLowerInvariant()}.local";
            var certResult = await tlsProvider.GetOrCreateServerCertificateAsync(Environment.MachineName, cancellationToken).ConfigureAwait(false);
            var thumbprint = certResult.IsSuccess && certResult.Value is not null
                ? tlsProvider.GetSha256Thumbprint(certResult.Value)
                : null;

            var announcement = new MdnsDiscoveredEndpoint
            {
                ServiceDeviceId = identityResult.Value.DeviceId,
                ServiceName = identityResult.Value.CustomName,
                HostName = Environment.MachineName,
                EndpointUrl = $"{scheme}://{lanHost}:{port}",
                Port = port,
                TlsThumbprint = thumbprint,
                HostKeyFingerprint = identityResult.Value.PublicKeyFingerprint,
                DiscoveredAtUtc = DateTimeOffset.UtcNow
            };

            var startResult = await _mdnsDiscovery.StartAdvertisingAsync(announcement, cancellationToken).ConfigureAwait(false);
            if (startResult.IsFailure)
            {
                _logger.LogWarning("mDNS advertisement unavailable ({Code}): {Message}", startResult.Error?.Code, startResult.Error?.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS advertisement startup failed gracefully; manual/Tailscale endpoints remain unaffected.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => _mdnsDiscovery.StopAdvertisingAsync(cancellationToken);
}

