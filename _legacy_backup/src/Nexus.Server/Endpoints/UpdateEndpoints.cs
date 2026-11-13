using System.Security.Claims;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;
using Nexus.Server.Services;

namespace Nexus.Server.Endpoints;

public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/updates")
            .RequireAuthorization();

        group.MapGet("/status", (ServerUpdateRelayService relay) =>
        {
            return Results.Ok(relay.GetRelayStatus());
        });

        group.MapGet("/latest", async (
            string? owner,
            string? repo,
            bool? includePrereleases,
            HttpContext httpContext,
            ServerUpdateRelayService relay,
            ServerPanelAndAutomationCoordinator trustCoordinator,
            CancellationToken ct) =>
        {
            var device = await ResolveTrustedDeviceAsync(httpContext, trustCoordinator, ct).ConfigureAwait(false);
            if (device.ErrorResult is not null) return device.ErrorResult;

            var res = await relay.GetLatestReleaseAsync(device.Device!, owner, repo, includePrereleases, ct).ConfigureAwait(false);
            return res.IsSuccess ? Results.Ok(res.Value) : ToErrorResult(res.Error!);
        });

        group.MapGet("/releases/{tag}/download", async (
            string tag,
            HttpContext httpContext,
            ServerUpdateRelayService relay,
            ServerPanelAndAutomationCoordinator trustCoordinator,
            CancellationToken ct) =>
        {
            var device = await ResolveTrustedDeviceAsync(httpContext, trustCoordinator, ct).ConfigureAwait(false);
            if (device.ErrorResult is not null) return device.ErrorResult;

            var res = await relay.OpenAssetDownloadStreamAsync(device.Device!, tag, ct).ConfigureAwait(false);
            if (res.IsFailure)
            {
                return ToErrorResult(res.Error!);
            }

            var download = res.Value;
            httpContext.Response.RegisterForDispose(download.ScopeToDispose);
            if (download.ContentLength is > 0)
            {
                httpContext.Response.ContentLength = download.ContentLength.Value;
            }

            httpContext.Response.Headers["X-Nexus-Apk-Sha256"] = download.ExpectedSha256;
            return Results.Stream(
                download.ContentStream,
                contentType: "application/vnd.android.package-archive",
                fileDownloadName: download.FileName,
                enableRangeProcessing: false);
        });

        return endpoints;
    }

    private static async Task<(Contracts.Security.TrustedDeviceRecord? Device, IResult? ErrorResult)> ResolveTrustedDeviceAsync(
        HttpContext httpContext,
        ServerPanelAndAutomationCoordinator trustCoordinator,
        CancellationToken ct)
    {
        var claimVal = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claimVal, out var deviceId))
        {
            return (null, Results.Json(new { code = "UNAUTHORIZED_DEVICE", message = "Dispositivo não autenticado." }, statusCode: 401));
        }

        var trustedRes = await trustCoordinator.AuthorizeCallerAsync(deviceId, NexusPermission.ViewStatus, ct).ConfigureAwait(false);
        if (trustedRes.IsFailure)
        {
            return (null, ToErrorResult(trustedRes.Error!));
        }

        return (trustedRes.Value, null);
    }

    private static IResult ToErrorResult(NexusError error) => error.Category switch
    {
        ErrorCategory.Unauthorized => Results.Json(new { code = error.Code, message = error.Message }, statusCode: 401),
        ErrorCategory.Forbidden => Results.Json(new { code = error.Code, message = error.Message }, statusCode: 403),
        ErrorCategory.NotFound => Results.Json(new { code = error.Code, message = error.Message }, statusCode: 404),
        ErrorCategory.Validation => Results.Json(new { code = error.Code, message = error.Message }, statusCode: 400),
        ErrorCategory.Network or ErrorCategory.Timeout => Results.Json(new { code = error.Code, message = error.Message }, statusCode: 503),
        _ => Results.Json(new { code = error.Code, message = error.Message }, statusCode: 500)
    };
}
