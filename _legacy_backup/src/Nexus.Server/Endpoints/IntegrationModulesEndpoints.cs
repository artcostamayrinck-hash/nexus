using System.Security.Claims;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;
using Nexus.Server.Services;

namespace Nexus.Server.Endpoints;

public static class IntegrationModulesEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationModulesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/modules")
            .RequireAuthorization();

        group.MapGet("/status", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.ViewStatus, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetModuleStatusesAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPost("/toggle", async (
            UpdateModuleToggleRequest request,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.ManageDevices, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SetModuleEnabledAsync(auth.Device!, request, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapGet("/control/settings", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.ControlInput, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetControlSettingsAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPut("/control/settings", async (
            ControlModuleSettings settings,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.ManageDevices, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SaveControlSettingsAsync(settings, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPost("/control/execute", async (
            RemoteControlCommandRequest request,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var requiredPermission = RemoteControlCommandRequest.IsHighImpactAction(request.Action)
                ? NexusPermission.SystemPower
                : NexusPermission.ControlInput;

            var auth = await AuthorizeAsync(httpContext, coordinator, requiredPermission, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.ExecuteControlAsync(auth.Device!, request, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapGet("/notify/settings", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notifications, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetNotifySettingsAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPut("/notify/settings", async (
            NotifyModuleSettings settings,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notifications, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SaveNotifySettingsAsync(settings, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPost("/notify/push", async (
            PushMobileNotificationRequest request,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notifications, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.PushNotificationAsync(auth.Device!, request, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapGet("/notify/history", async (
            int? limit,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notifications, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetNotificationHistoryAsync(limit ?? 100, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPost("/notify/{notificationId:guid}/dismiss", async (
            Guid notificationId,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notifications, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.DismissNotificationAsync(notificationId, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(new { dismissed = true }) : ToErrorResult(res.Error!);
        });

        group.MapPost("/notify/clear", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Notifications, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.ClearNotificationHistoryAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(new { cleared = res.Value }) : ToErrorResult(res.Error!);
        });

        group.MapGet("/monitor/settings", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.HardwareMonitor, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetMonitorSettingsAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPut("/monitor/settings", async (
            MonitorModuleSettings settings,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.HardwareMonitor, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SaveMonitorSettingsAsync(settings, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapGet("/monitor/snapshot", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.HardwareMonitor, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetMonitorSnapshotAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapGet("/media/settings", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.MediaControl, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetMediaSettingsAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPut("/media/settings", async (
            MediaModuleSettings settings,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.MediaControl, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SaveMediaSettingsAsync(settings, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapGet("/media/state", async (
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.MediaControl, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetMediaStateAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapPost("/media/command", async (
            MediaControlCommandRequest request,
            HttpContext httpContext,
            ServerIntegrationModulesCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.MediaControl, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.ExecuteMediaCommandAsync(auth.Device!, request, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        return endpoints;
    }

    private static async Task<(TrustedDeviceRecord? Device, IResult? ErrorResult)> AuthorizeAsync(
        HttpContext httpContext,
        ServerIntegrationModulesCoordinator coordinator,
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
        return auth.IsFailure ? (null, ToErrorResult(auth.Error!)) : (auth.Value, null);
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

