using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Common;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;
using Nexus.Core.Pairing;
using Nexus.Server.Authentication;
using Nexus.Server.Hubs;
using Nexus.Server.Middleware;

namespace Nexus.Server.Endpoints;

public static class PairingAndSecurityEndpoints
{
    public static IEndpointRouteBuilder MapPairingAndSecurityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");

        // Pairing lifecycle (protected by ECDSA P-256 signatures, single-use secrets, and anti-replay nonces)
        var pairing = api.MapGroup("/pairing");

        pairing.MapPost("/sessions", async (
            CreatePairingRequest request,
            IPairingService pairingService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await pairingService.CreateSessionAsync(request, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        });

        pairing.MapGet("/sessions/{pairingId:guid}", async (
            Guid pairingId,
            IPairingService pairingService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await pairingService.GetSessionAsync(pairingId, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        });

        pairing.MapPost("/claim", async (
            SubmitPairingClaimRequest claim,
            IPairingService pairingService,
            IHubContext<NexusHub, INexusHubClient> hubContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await pairingService.SubmitClaimAsync(claim, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                await hubContext.Clients.All.ReceivePairingSnapshot(result.Value).ConfigureAwait(false);
            }

            return MapResult(result, httpContext);
        });

        pairing.MapPost("/sessions/{pairingId:guid}/confirm", async (
            Guid pairingId,
            ConfirmPairingRequest request,
            IPairingService pairingService,
            IHubContext<NexusHub, INexusHubClient> hubContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var normalized = request with { PairingId = pairingId };
            var result = await pairingService.ConfirmAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                await hubContext.Clients.All.ReceivePairingSnapshot(result.Value).ConfigureAwait(false);
            }

            return MapResult(result, httpContext);
        });

        pairing.MapPost("/sessions/{pairingId:guid}/finalize", async (
            Guid pairingId,
            FinalizePairingTokenRequest request,
            IPairingService pairingService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var normalized = request with { PairingId = pairingId };
            var result = await pairingService.FinalizeTokensAsync(normalized, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        });

        api.MapPost("/auth/refresh", async (
            RefreshTokenRequest request,
            ITrustedDeviceService trustedService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await trustedService.RefreshTokensAsync(request, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        });

        // Trusted device management (strictly authenticated + permission-checked)
        var trusted = api.MapGroup("/trusted-devices").RequireAuthorization();

        trusted.MapGet("/", async (
            ITrustedDeviceService trustedService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await trustedService.ListAllAsync(cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        trusted.MapGet("/{deviceId:guid}", async (
            Guid deviceId,
            ITrustedDeviceService trustedService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await trustedService.GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        trusted.MapPatch("/{deviceId:guid}/name", async (
            Guid deviceId,
            RenameDeviceRequest request,
            ITrustedDeviceService trustedService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var validation = ContractValidator.Validate(request);
            if (!validation.IsValid)
            {
                return MapResult(Result<TrustedDeviceRecord>.Failure(NexusError.Validation(validation)), httpContext);
            }

            var result = await trustedService.RenameAsync(deviceId, request.CustomName, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        trusted.MapPatch("/{deviceId:guid}/permissions", async (
            Guid deviceId,
            UpdateDevicePermissionsRequest request,
            ITrustedDeviceService trustedService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await trustedService.UpdatePermissionsAsync(deviceId, request.Permissions, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        trusted.MapPost("/{deviceId:guid}/revoke", async (
            Guid deviceId,
            RevokeDeviceRequest? request,
            ITrustedDeviceService trustedService,
            INexusPresenceTracker presenceTracker,
            IHubContext<NexusHub, INexusHubClient> hubContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await trustedService.RevokeAsync(deviceId, request?.Reason, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                foreach (var session in presenceTracker.GetConnectedSessions().Where(s => s.DeviceId == deviceId))
                {
                    await hubContext.Clients.Client(session.ConnectionId).ReceiveRevocationNotice(deviceId, request?.Reason ?? "Dispositivo revogado").ConfigureAwait(false);
                    presenceTracker.Unregister(session.ConnectionId);
                }

                return Results.Ok(NexusResponse<bool>.Success(true, correlationId: GetCorrelationId(httpContext)));
            }

            return MapResult(Result<bool>.Failure(result.Error!), httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        trusted.MapDelete("/{deviceId:guid}", async (
            Guid deviceId,
            ITrustedDeviceService trustedService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await trustedService.RemoveAsync(deviceId, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        return endpoints;
    }

    private static IResult MapResult<T>(Result<T> result, HttpContext httpContext)
    {
        var correlationId = GetCorrelationId(httpContext);
        if (result.IsSuccess)
        {
            return Results.Ok(NexusResponse<T>.Success(result.Value, correlationId: correlationId));
        }

        var status = result.Error!.Category switch
        {
            ErrorCategory.Validation => StatusCodes.Status400BadRequest,
            ErrorCategory.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCategory.NotFound => StatusCodes.Status404NotFound,
            ErrorCategory.Conflict => StatusCodes.Status409Conflict,
            ErrorCategory.Storage => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            NexusResponse<T>.Failure(result.Error.ToContractError(), correlationId: correlationId),
            statusCode: status);
    }

    private static Guid GetCorrelationId(HttpContext httpContext)
        => httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw) && raw is Guid id
            ? id
            : Guid.CreateVersion7();
}

