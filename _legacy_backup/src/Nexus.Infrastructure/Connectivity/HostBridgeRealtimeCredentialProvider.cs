using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Security;
using Nexus.Security.Identity;

namespace Nexus.Infrastructure.Connectivity;

/// <summary>
/// Resolves real-time authentication credentials for the local Windows host using
/// the protected <see cref="ILocalHostTokenBridge"/> and persisted endpoint configuration.
/// </summary>
public sealed class HostBridgeRealtimeCredentialProvider : IRealtimeCredentialProvider
{
    private const string DesktopSettingsKey = "desktop.preferences.v1";

    private sealed record DesktopEndpointSettingsProbe(string? ServerEndpoint);

    private readonly ILocalHostTokenBridge _hostBridge;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly IEndpointRegistryRepository _endpointRepository;
    private readonly ISettingsStore _settingsStore;
    private readonly IClock _clock;

    public HostBridgeRealtimeCredentialProvider(
        ILocalHostTokenBridge hostBridge,
        ICryptographicIdentityProvider identityProvider,
        IEndpointRegistryRepository endpointRepository,
        ISettingsStore settingsStore,
        IClock clock)
    {
        _hostBridge = hostBridge;
        _identityProvider = identityProvider;
        _endpointRepository = endpointRepository;
        _settingsStore = settingsStore;
        _clock = clock;
    }

    public async Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var bridgeResult = await _hostBridge.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bridgeResult.IsFailure)
        {
            return Result<RealtimeCredentialBundle>.Failure(bridgeResult.Error!);
        }

        var bridge = bridgeResult.Value;
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.AccessToken))
        {
            return Result<RealtimeCredentialBundle>.Failure(
                NexusError.Unauthorized(
                    "Credenciais do servidor NEXUS local indisponíveis ou expiradas.",
                    code: "REALTIME_AUTH_MISSING"));
        }

        if (bridge.ExpiresAtUtc <= _clock.UtcNow)
        {
            return Result<RealtimeCredentialBundle>.Failure(
                NexusError.Unauthorized(
                    "Token do servidor local expirado.",
                    code: "REALTIME_AUTH_EXPIRED"));
        }

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<RealtimeCredentialBundle>.Failure(identityResult.Error!);
        }

        var endpoint = await ResolveServerEndpointAsync(bridge.TlsThumbprint, cancellationToken).ConfigureAwait(false);

        return Result<RealtimeCredentialBundle>.Success(new RealtimeCredentialBundle(
            bridge.ServerDeviceId,
            identityResult.Value.CustomName,
            bridge.AccessToken,
            bridge.ExpiresAtUtc,
            endpoint,
            bridge.TlsThumbprint));
    }

    public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
        => GetCredentialsAsync(cancellationToken);

    private async Task<string> ResolveServerEndpointAsync(string? tlsThumbprint, CancellationToken cancellationToken)
    {
        var settingsResult = await _settingsStore.GetAsync<DesktopEndpointSettingsProbe>(DesktopSettingsKey, cancellationToken)
            .ConfigureAwait(false);
        if (settingsResult.IsSuccess && !string.IsNullOrWhiteSpace(settingsResult.Value?.ServerEndpoint))
        {
            return settingsResult.Value.ServerEndpoint.Trim().TrimEnd('/');
        }

        var endpointsResult = await _endpointRepository.ListAsync(null, cancellationToken).ConfigureAwait(false);
        if (endpointsResult.IsSuccess && endpointsResult.Value.Count > 0)
        {
            var preferred = endpointsResult.Value
                .Where(e => e.IsEnabled)
                .OrderBy(e => e.Priority)
                .FirstOrDefault();
            if (preferred is not null && !string.IsNullOrWhiteSpace(preferred.EndpointUrl))
            {
                return preferred.EndpointUrl.Trim().TrimEnd('/');
            }
        }

        return string.IsNullOrWhiteSpace(tlsThumbprint)
            ? "http://127.0.0.1:5280"
            : "https://127.0.0.1:5280";
    }
}
