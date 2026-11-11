using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Messaging;
using Nexus.Server.Authentication;
using Nexus.Server.Hubs;
using Nexus.Server.Middleware;
using Nexus.Server.Services;

namespace Nexus.Server.Endpoints;

public static class RealtimeEndpoints
{
    private const long MaxBulkPayloadBytes = 50 * 1024 * 1024;

    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/realtime");

        group.MapGet("/presence", async (
            ServerRealtimeCoordinator coordinator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var presence = await coordinator.GetMergedPresenceAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(NexusResponse<IReadOnlyList<DevicePresenceSnapshot>>.Success(
                presence,
                correlationId: GetCorrelationId(httpContext)));
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        group.MapGet("/sessions", (
            ServerRealtimeCoordinator coordinator,
            HttpContext httpContext) =>
        {
            var sessions = coordinator.GetActiveSessionSnapshots();
            return Results.Ok(NexusResponse<IReadOnlyList<RealtimeSessionSnapshot>>.Success(
                sessions,
                correlationId: GetCorrelationId(httpContext)));
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        group.MapPost("/sessions/{connectionId}/disconnect", async (
            string connectionId,
            INexusPresenceTracker presenceTracker,
            ServerRealtimeCoordinator coordinator,
            IHubContext<NexusHub, INexusHubClient> hubContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var target = presenceTracker.GetConnectedSessions().FirstOrDefault(s => s.ConnectionId == connectionId);
            if (target is null)
            {
                return Results.NotFound(NexusResponse<bool>.Failure(
                    new ContractError("SESSION_NOT_FOUND", "Sessão em tempo real não encontrada."),
                    correlationId: GetCorrelationId(httpContext)));
            }

            await hubContext.Clients.Client(connectionId)
                .ReceiveSessionNotice(new RealtimeSessionSnapshot
                {
                    SessionId = target.SessionId,
                    ConnectionId = connectionId,
                    DeviceId = target.DeviceId,
                    DeviceName = target.DeviceName,
                    State = ConnectionState.Disconnected,
                    ConnectedAtUtc = target.ConnectedAtUtc,
                    LastActivityAtUtc = DateTimeOffset.UtcNow
                }).ConfigureAwait(false);

            presenceTracker.Unregister(connectionId);
            await coordinator.BroadcastPresenceAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(NexusResponse<bool>.Success(true, correlationId: GetCorrelationId(httpContext)));
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        group.MapPost("/commands", async (
            DeviceCommand command,
            ServerRealtimeCoordinator coordinator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var callerId = ExtractCallerDeviceId(httpContext);
            var ack = await coordinator.RouteCommandAsync(callerId, null, command, cancellationToken).ConfigureAwait(false);
            if (ack.Status == RealtimeAckStatus.Rejected)
            {
                var status = ack.Detail?.Contains("TARGET_DEVICE_OFFLINE", StringComparison.Ordinal) == true
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status400BadRequest;

                return Results.Json(
                    NexusResponse<RealtimeMessageAck>.Failure(
                        new ContractError("COMMAND_REJECTED", ack.Detail ?? "Comando rejeitado."),
                        correlationId: command.CorrelationId),
                    statusCode: status);
            }

            return Results.Ok(NexusResponse<RealtimeMessageAck>.Success(ack, correlationId: command.CorrelationId));
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        // HTTP bulk data channel for larger payloads (> SignalR message frame budget)
        group.MapPost("/transfers/payload", async (
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (httpContext.Request.ContentLength is > MaxBulkPayloadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var callerId = ExtractCallerDeviceId(httpContext);
            Guid? targetId = httpContext.Request.Headers.TryGetValue("X-Nexus-Target-DeviceId", out var targetRaw)
                && Guid.TryParse(targetRaw.FirstOrDefault(), out var parsedTarget)
                ? parsedTarget
                : null;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[16384];
            long totalBytes = 0;

            int read;
            while ((read = await httpContext.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalBytes += read;
                if (totalBytes > MaxBulkPayloadBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                hasher.AppendData(buffer, 0, read);
            }

            var receipt = new BulkPayloadTransferReceipt(
                Guid.CreateVersion7(),
                callerId,
                targetId,
                httpContext.Request.ContentType ?? "application/octet-stream",
                totalBytes,
                Convert.ToHexString(hasher.GetHashAndReset()),
                DateTimeOffset.UtcNow);

            return Results.Ok(NexusResponse<BulkPayloadTransferReceipt>.Success(
                receipt,
                correlationId: GetCorrelationId(httpContext)));
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        return endpoints;
    }

    private static Guid ExtractCallerDeviceId(HttpContext context)
    {
        var raw = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static Guid GetCorrelationId(HttpContext httpContext)
        => httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw) && raw is Guid id
            ? id
            : Guid.CreateVersion7();
}

