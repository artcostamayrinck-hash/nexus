using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Pairing;
using Nexus.Security.Replay;
using Nexus.Security.Tls;
using Nexus.Security.Tokens;

namespace Nexus.Security.DependencyInjection;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddNexusSecurity(
        this IServiceCollection services,
        Action<SecureCredentialStoreOptions>? configureVault = null,
        Action<DeviceTokenOptions>? configureTokens = null)
    {
        var vaultOptions = new SecureCredentialStoreOptions();
        configureVault?.Invoke(vaultOptions);

        var tokenOptions = new DeviceTokenOptions();
        configureTokens?.Invoke(tokenOptions);

        services.TryAddSingleton(vaultOptions);
        services.TryAddSingleton(tokenOptions);

        services.TryAddSingleton<ISecureCredentialStore, PlatformSecureCredentialStore>();
        services.TryAddSingleton<IDeviceIdentityService, DeviceIdentityService>();
        services.TryAddSingleton<ICryptographicIdentityProvider, CryptographicIdentityProvider>();
        services.TryAddSingleton<ITokenGenerator, SecureTokenGenerator>();
        services.TryAddSingleton<IPayloadSigner, HmacPayloadSigner>();
        services.TryAddSingleton<IDeviceTokenService, DeviceTokenService>();
        services.TryAddSingleton<INonceReplayProtector, InMemoryNonceReplayProtector>();
        services.TryAddSingleton<ITlsCertificateProvider, LocalTlsCertificateManager>();
        services.TryAddSingleton<IQrCodeService, QrCodeGeneratorService>();

        return services;
    }
}
