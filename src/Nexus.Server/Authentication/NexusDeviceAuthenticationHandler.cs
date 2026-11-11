using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Storage;
using Nexus.Security.Identity;
using Nexus.Security.Replay;
using Nexus.Security.Tokens;

namespace Nexus.Server.Authentication;

public static class NexusAuthDefaults
{
    public const string AuthenticationScheme = "NexusBearer";
    public const string PermissionsClaimType = "nexus_permissions";
    public const string PolicyViewStatus = "Permission.ViewStatus";
    public const string PolicyManageDevices = "Permission.ManageDevices";
}

public sealed class NexusDeviceAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IDeviceTokenService _tokenService;
    private readonly ITrustedDeviceRepository _trustedDevices;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly INonceReplayProtector _replayProtector;
    private readonly IClock _clock;

    public NexusDeviceAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDeviceTokenService tokenService,
        ITrustedDeviceRepository trustedDevices,
        ICryptographicIdentityProvider identityProvider,
        INonceReplayProtector replayProtector,
        IClock clock)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
        _trustedDevices = trustedDevices;
        _identityProvider = identityProvider;
        _replayProtector = replayProtector;
        _clock = clock;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var now = _clock.UtcNow;
        var claimsResult = await _tokenService.ValidateAccessTokenAsync(token, now, Context.RequestAborted).ConfigureAwait(false);
        if (claimsResult.IsFailure)
        {
            return AuthenticateResult.Fail(claimsResult.Error!.Message);
        }

        var tokenClaims = claimsResult.Value;
        var trustedResult = await _trustedDevices.GetByIdAsync(tokenClaims.DeviceId, Context.RequestAborted).ConfigureAwait(false);
        if (trustedResult.IsFailure || trustedResult.Value is null)
        {
            return AuthenticateResult.Fail("Device is not registered as trusted.");
        }

        var trusted = trustedResult.Value;
        if (trusted.TrustStatus != DeviceTrustStatus.Authorized)
        {
            return AuthenticateResult.Fail("Device trust has been revoked.");
        }

        // If client provides signed anti-replay headers, verify ECDSA signature over request metadata
        if (Request.Headers.TryGetValue("X-Nexus-Signature", out var sigValues) && !string.IsNullOrWhiteSpace(sigValues.FirstOrDefault()))
        {
            if (!Request.Headers.TryGetValue("X-Nexus-Timestamp", out var tsRaw) ||
                !DateTimeOffset.TryParse(tsRaw.FirstOrDefault(), out var tsUtc) ||
                !Request.Headers.TryGetValue("X-Nexus-Nonce", out var nonceRaw) ||
                string.IsNullOrWhiteSpace(nonceRaw.FirstOrDefault()))
            {
                return AuthenticateResult.Fail("Signed request missing timestamp or nonce headers.");
            }

            var nonce = nonceRaw.First()!;
            var replay = _replayProtector.ValidateAndRecord("http.request", trusted.DeviceId, tsUtc, nonce);
            if (replay.IsFailure)
            {
                return AuthenticateResult.Fail(replay.Error!.Message);
            }

            var canonical = $"{Request.Method.ToUpperInvariant()}|{Request.Path.Value}|{tsUtc.ToUnixTimeSeconds()}|{nonce}";
            var sigVerify = _identityProvider.VerifySignature(trusted.PublicKeySpkiBase64, canonical, sigValues.First()!);
            if (sigVerify.IsFailure)
            {
                return AuthenticateResult.Fail(sigVerify.Error!.Message);
            }
        }

        await _trustedDevices.UpdateLastAccessAsync(trusted.DeviceId, now, Context.RequestAborted).ConfigureAwait(false);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, trusted.DeviceId.ToString("D")),
            new Claim(ClaimTypes.Name, trusted.CustomName),
            new Claim(NexusAuthDefaults.PermissionsClaimType, ((int)trusted.Permissions).ToString())
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private string? ExtractToken()
    {
        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var raw = authHeader.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(raw) && raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return raw["Bearer ".Length..].Trim();
            }
        }

        if (Request.Path.StartsWithSegments("/hubs") && Request.Query.TryGetValue("access_token", out var queryToken))
        {
            return queryToken.FirstOrDefault();
        }

        return null;
    }
}

public sealed record NexusPermissionRequirement(NexusPermission RequiredPermission) : IAuthorizationRequirement;

public sealed class NexusPermissionAuthorizationHandler : AuthorizationHandler<NexusPermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NexusPermissionRequirement requirement)
    {
        var claim = context.User.FindFirst(NexusAuthDefaults.PermissionsClaimType);
        if (claim is not null && int.TryParse(claim.Value, out var permInt))
        {
            var granted = (NexusPermission)permInt;
            if ((granted & requirement.RequiredPermission) == requirement.RequiredPermission)
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}

