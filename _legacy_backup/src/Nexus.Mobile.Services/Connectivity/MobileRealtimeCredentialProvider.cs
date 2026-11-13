using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Mobile.Services.Security;
using Nexus.Security.Identity;

namespace Nexus.Mobile.Services.Connectivity;

/// <summary>
/// Resolves paired bearer credentials, endpoint selection (preferring active route orchestrator),
/// and automatic token refresh for the Android SignalR client.
/// </summary>
public sealed class MobileRealtimeCredentialProvider : IRealtimeCredentialProvider
{
    private readonly IMobilePairingClient _pairingClient;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly INexusRouteOrchestrator _routeOrchestrator;
    private readonly IClock _clock;

    public MobileRealtimeCredentialProvider(
        IMobilePairingClient pairingClient,
        ICryptographicIdentityProvider identityProvider,
        INexusRouteOrchestrator routeOrchestrator,
        IClock clock)
    {
        _pairingClient = pairingClient;
        _identityProvider = identityProvider;
        _routeOrchestrator = routeOrchestrator;
        _clock = clock;
    }

    public async Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var sessionRes = await _pairingClient.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        if (sessionRes.IsFailure)
        {
            return Result<RealtimeCredentialBundle>.Failure(sessionRes.Error!);
        }

        var session = sessionRes.Value;
        if (session is null || string.IsNullOrWhiteSpace(session.Tokens.AccessToken))
        {
            return Result<RealtimeCredentialBundle>.Failure(
                NexusError.Unauthorized(
                    "Dispositivo Android ainda não está pareado ao servidor NEXUS.",
                    code: "REALTIME_AUTH_MISSING"));
        }

        var idRes = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idRes.IsFailure)
        {
            return Result<RealtimeCredentialBundle>.Failure(idRes.Error!);
        }

        var endpoint = ResolvePreferredEndpoint(session);
        var deviceId = session.Tokens.DeviceId != Guid.Empty ? session.Tokens.DeviceId : idRes.Value.DeviceId;

        return Result<RealtimeCredentialBundle>.Success(new RealtimeCredentialBundle(
            deviceId,
            idRes.Value.CustomName,
            session.Tokens.AccessToken,
            session.Tokens.AccessTokenExpiresAtUtc,
            endpoint,
            session.TlsThumbprint));
    }

    public async Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var refreshRes = await _pairingClient.RefreshStoredTokensAsync(cancellationToken).ConfigureAwait(false);
        if (refreshRes.IsFailure)
        {
            return Result<RealtimeCredentialBundle>.Failure(refreshRes.Error!);
        }

        var updatedSession = await _pairingClient.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        var idRes = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idRes.IsFailure)
        {
            return Result<RealtimeCredentialBundle>.Failure(idRes.Error!);
        }

        var tokens = refreshRes.Value;
        var session = updatedSession.IsSuccess ? updatedSession.Value : null;
        if (tokens.AccessTokenExpiresAtUtc <= _clock.UtcNow)
        {
            return Result<RealtimeCredentialBundle>.Failure(
                NexusError.Unauthorized("Token renovado já está expirado.", code: "REALTIME_AUTH_EXPIRED"));
        }

        var endpoint = session is not null
            ? ResolvePreferredEndpoint(session)
            : _routeOrchestrator.CurrentStatus.ActiveEndpoint ?? string.Empty;

        return Result<RealtimeCredentialBundle>.Success(new RealtimeCredentialBundle(
            tokens.DeviceId != Guid.Empty ? tokens.DeviceId : idRes.Value.DeviceId,
            idRes.Value.CustomName,
            tokens.AccessToken,
            tokens.AccessTokenExpiresAtUtc,
            endpoint,
            session?.TlsThumbprint));
    }

    private string ResolvePreferredEndpoint(MobilePairedSessionEnvelope session)
    {
        var activeRouteUrl = _routeOrchestrator.CurrentStatus.ActiveEndpoint;
        if (!string.IsNullOrWhiteSpace(activeRouteUrl))
        {
            return activeRouteUrl.Trim().TrimEnd('/');
        }

        return session.ServerEndpoint.Trim().TrimEnd('/');
    }
}
