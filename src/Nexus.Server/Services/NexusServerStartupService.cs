using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Security;
using Nexus.Modules.Registry;
using Nexus.Security.Identity;
using Nexus.Security.Tls;
using Nexus.Security.Tokens;

namespace Nexus.Server.Services;

public sealed class NexusServerStartupService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IStorageInitializer _storageInitializer;
    private readonly IModuleRegistry _moduleRegistry;
    private readonly ILocalDeviceContext _localDevice;
    private readonly LocalHostTokenBridgeOptions _bridgeOptions;
    private readonly ILocalHostTokenBridge _hostBridge;
    private readonly ILogger<NexusServerStartupService> _logger;

    public NexusServerStartupService(
        IServiceProvider serviceProvider,
        IStorageInitializer storageInitializer,
        IModuleRegistry moduleRegistry,
        ILocalDeviceContext localDevice,
        LocalHostTokenBridgeOptions bridgeOptions,
        ILocalHostTokenBridge hostBridge,
        ILogger<NexusServerStartupService> logger)
    {
        _serviceProvider = serviceProvider;
        _storageInitializer = storageInitializer;
        _moduleRegistry = moduleRegistry;
        _localDevice = localDevice;
        _bridgeOptions = bridgeOptions;
        _hostBridge = hostBridge;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting NEXUS Server for host device {DeviceName} ({DeviceId})", _localDevice.DeviceName, _localDevice.DeviceId);

        var init = await _storageInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (init.IsFailure)
        {
            _logger.LogError("Failed to initialize SQLite storage: {Code} - {Message}", init.Error?.Code, init.Error?.Message);
        }

        await _moduleRegistry.InitializeAllAsync(_serviceProvider, cancellationToken).ConfigureAwait(false);

        if (_bridgeOptions.Enabled)
        {
            await PublishLocalHostBridgeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping NEXUS Server modules");
        await _moduleRegistry.ShutdownAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishLocalHostBridgeAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var identityProvider = scope.ServiceProvider.GetRequiredService<ICryptographicIdentityProvider>();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
        var tlsProvider = scope.ServiceProvider.GetRequiredService<ITlsCertificateProvider>();

        var idResult = await identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idResult.IsFailure)
        {
            _logger.LogWarning("Failed to initialize host cryptographic identity for bridge: {Error}", idResult.Error?.Message);
            return;
        }

        var id = idResult.Value;
        var existing = await trustedRepo.GetByIdAsync(id.DeviceId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (existing.IsSuccess && existing.Value is null)
        {
            var hostTrust = new TrustedDeviceRecord
            {
                DeviceId = id.DeviceId,
                CustomName = id.CustomName,
                Platform = id.Platform,
                DeviceType = id.DeviceType,
                OperatingSystemVersion = id.OperatingSystemVersion,
                AppVersion = id.AppVersion,
                PublicKeySpkiBase64 = id.PublicKeySpkiBase64,
                PublicKeyFingerprint = id.PublicKeyFingerprint,
                Permissions = NexusPermission.All,
                TrustStatus = DeviceTrustStatus.Authorized,
                PairedAtUtc = id.RegisteredAtUtc,
                LastAccessAtUtc = now
            };

            await trustedRepo.UpsertAsync(hostTrust, cancellationToken).ConfigureAwait(false);
        }

        var tokens = await tokenService.IssueTokenPairAsync(id.DeviceId, NexusPermission.All, now, cancellationToken).ConfigureAwait(false);
        if (tokens.IsFailure)
        {
            return;
        }

        var cert = await tlsProvider.GetOrCreateServerCertificateAsync(Environment.MachineName, cancellationToken).ConfigureAwait(false);
        var thumbprint = cert.IsSuccess ? tlsProvider.GetSha256Thumbprint(cert.Value) : null;

        await _hostBridge.WriteAsync(
            new HostBridgeTokenEnvelope(
                id.DeviceId,
                tokens.Value.TokenPair.AccessToken,
                tokens.Value.TokenPair.AccessTokenExpiresAtUtc,
                thumbprint),
            cancellationToken).ConfigureAwait(false);
    }
}
