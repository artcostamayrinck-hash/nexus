using System.Security.Claims;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Sync;
using Nexus.Core.Errors;
using Nexus.Server.Services;

namespace Nexus.Server.Endpoints;

public static class BasicSyncEndpoints
{
    public static IEndpointRouteBuilder MapBasicSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/sync")
            .RequireAuthorization();

        group.MapGet("/clipboard", async (
            int? limit,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Clipboard, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.ListClipboardHistoryAsync(limit ?? 50, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapPost("/clipboard", async (
            ClipboardEntryContract entry,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Clipboard, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.IngestClipboardAsync(auth.Device!, entry, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapGet("/notes", async (
            string? query,
            bool? includeDeleted,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notes, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.ListNotesAsync(query, includeDeleted ?? false, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapPost("/notes/sync", async (
            NoteSyncRequest request,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notes, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.SynchronizeNotesAsync(auth.Device!, request, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapGet("/notes/{noteId:guid}/revisions", async (
            Guid noteId,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notes, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.GetRevisionsAsync(noteId, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapPost("/notes/{noteId:guid}/restore/{revisionId:guid}", async (
            Guid noteId,
            Guid revisionId,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notes, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.RestoreRevisionAsync(auth.Device!, noteId, revisionId, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapGet("/conflicts", async (
            bool? unresolvedOnly,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notes, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.ListConflictsAsync(unresolvedOnly ?? true, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        group.MapPost("/conflicts/{conflictId:guid}/resolve", async (
            Guid conflictId,
            ResolveConflictRequest request,
            HttpContext httpContext,
            ServerBasicSyncCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notes, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null)
            {
                return auth.ErrorResult;
            }

            var result = await coordinator.ResolveConflictAsync(auth.Device!, conflictId, request, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : ToErrorResult(result.Error!);
        });

        return endpoints;
    }

    private static async Task<(TrustedDeviceRecord? Device, IResult? ErrorResult)> AuthorizeAsync(
        HttpContext httpContext,
        ServerBasicSyncCoordinator coordinator,
        NexusPermission requiredPermission,
        CancellationToken ct)
    {
        var idRaw = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idRaw, out var callerId))
        {
            return (null, Results.Json(
                new { code = "DEVICE_IDENTITY_MISSING", message = "Dispositivo não identificado." },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var auth = await coordinator.AuthorizeCallerAsync(callerId, requiredPermission, ct).ConfigureAwait(false);
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
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(new { code = error.Code, message = error.Message }, statusCode: statusCode);
    }
}
