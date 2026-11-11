using System.Security.Claims;
using Nexus.Contracts.Security;
using Nexus.Contracts.Transfers;
using Nexus.Core.Errors;
using Nexus.Server.Services;

namespace Nexus.Server.Endpoints;

public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/transfers")
            .RequireAuthorization();

        group.MapPost("/initiate", async (
            InitiateFileTransferRequest request,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.InitiateTransferAsync(auth.Device!, request, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapPut("/{transferId:guid}/chunks", async (
            Guid transferId,
            long offset,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator
                .ReceiveChunkAsync(auth.Device!, transferId, offset, httpContext.Request.Body, ct)
                .ConfigureAwait(false);

            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        }).DisableAntiforgery();

        group.MapPost("/{transferId:guid}/finalize-upload", async (
            Guid transferId,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.FinalizeUploadAsync(auth.Device!, transferId, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapGet("/{transferId:guid}/download", async (
            Guid transferId,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var resolved = await coordinator.ResolveDownloadAsync(auth.Device!, transferId, ct).ConfigureAwait(false);
            if (resolved.IsFailure)
            {
                return ToErrorResult(resolved.Error!);
            }

            var (record, stagedPath) = resolved.Value;
            return Results.File(
                stagedPath,
                contentType: string.IsNullOrWhiteSpace(record.ContentType) ? "application/octet-stream" : record.ContentType,
                fileDownloadName: record.SafeFileName,
                enableRangeProcessing: true);
        });

        group.MapPost("/{transferId:guid}/acknowledge", async (
            Guid transferId,
            AcknowledgeFileTransferRequest request,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var normalized = request with { TransferId = transferId };
            var result = await coordinator.AcknowledgeAsync(auth.Device!, transferId, normalized, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapPost("/{transferId:guid}/pause", async (
            Guid transferId,
            TransferControlRequest? request,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.PauseAsync(auth.Device!, transferId, request?.Reason, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapPost("/{transferId:guid}/cancel", async (
            Guid transferId,
            TransferControlRequest? request,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.CancelAsync(auth.Device!, transferId, request?.Reason, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapGet("/{transferId:guid}", async (
            Guid transferId,
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.GetStatusAsync(auth.Device!, transferId, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapGet("/incoming", async (
            HttpContext httpContext,
            ServerTransferCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.ListIncomingAsync(auth.Device!, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        return endpoints;
    }

    private static async Task<(TrustedDeviceRecord? Device, IResult? ErrorResult)> AuthorizeAsync(
        HttpContext httpContext,
        ServerTransferCoordinator coordinator,
        CancellationToken ct)
    {
        var idRaw = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idRaw, out var callerId))
        {
            return (null, Results.Json(
                new { code = "DEVICE_IDENTITY_MISSING", message = "Dispositivo não identificado." },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var auth = await coordinator
            .AuthorizeDeviceAsync(callerId, NexusPermission.FileTransfer, ct)
            .ConfigureAwait(false);

        if (auth.IsFailure)
        {
            return (null, ToErrorResult(auth.Error!));
        }

        return (auth.Value, null);
    }

    private static IResult ToErrorResult(NexusError error)
    {
        var statusCode = error.Category switch
        {
            ErrorCategory.Unauthorized => error.Code.StartsWith("PERMISSION_DENIED", StringComparison.Ordinal)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized,
            ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCategory.Validation => StatusCodes.Status400BadRequest,
            ErrorCategory.NotFound => StatusCodes.Status404NotFound,
            ErrorCategory.Conflict => StatusCodes.Status409Conflict,
            ErrorCategory.Storage when string.Equals(error.Code, "INSUFFICIENT_DISK_SPACE", StringComparison.Ordinal) =>
                StatusCodes.Status507InsufficientStorage,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(new { code = error.Code, message = error.Message }, statusCode: statusCode);
    }
}
