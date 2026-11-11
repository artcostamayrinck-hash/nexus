using System.Security.Claims;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;
using Nexus.Server.Services;

namespace Nexus.Server.Endpoints;

public static class PanelAndAutomationEndpoints
{
    public static IEndpointRouteBuilder MapPanelAndAutomationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var panelGroup = endpoints.MapGroup("/api/v1/panel")
            .RequireAuthorization();

        panelGroup.MapGet("/profiles", async (
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.ViewStatus, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetProfilesAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        panelGroup.MapPut("/profiles", async (
            PanelProfile profile,
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SaveProfileAsync(auth.Device!, profile, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        panelGroup.MapPost("/profiles/{profileId:guid}/activate", async (
            Guid profileId,
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.ActivateProfileAsync(auth.Device!, profileId, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        var autoGroup = endpoints.MapGroup("/api/v1/automations")
            .RequireAuthorization();

        autoGroup.MapGet("/settings", async (
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetSettingsAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        autoGroup.MapPut("/settings", async (
            AutomationEngineSettings settings,
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SaveSettingsAsync(settings, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        autoGroup.MapGet("/rules", async (
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetRulesAsync(ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        autoGroup.MapPut("/rules", async (
            AutomationRule rule,
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.SaveRuleAsync(rule, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        autoGroup.MapDelete("/rules/{ruleId:guid}", async (
            Guid ruleId,
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.DeleteRuleAsync(ruleId, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.NoContent() : ToErrorResult(res.Error!);
        });

        autoGroup.MapGet("/history", async (
            int? limit,
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.GetHistoryAsync(limit ?? 100, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        autoGroup.MapPost("/trigger", async (
            AutomationTriggerDispatchRequest request,
            HttpContext httpContext,
            ServerPanelAndAutomationCoordinator coordinator,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, coordinator, NexusPermission.Automation, ct).ConfigureAwait(false);
            if (auth.ErrorResult is not null) return auth.ErrorResult;

            var res = await coordinator.DispatchTriggerAsync(auth.Device!, request, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        return endpoints;
    }

    private static async Task<(TrustedDeviceRecord? Device, IResult? ErrorResult)> AuthorizeAsync(
        HttpContext httpContext,
        ServerPanelAndAutomationCoordinator coordinator,
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

