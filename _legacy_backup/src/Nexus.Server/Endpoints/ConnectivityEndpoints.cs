using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Messaging;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Infrastructure.Connectivity;
using Nexus.Security.Identity;
using Nexus.Security.Tls;
using Nexus.Server.Authentication;
using Nexus.Server.Hubs;
using Nexus.Server.Middleware;

namespace Nexus.Server.Endpoints;

public static class ConnectivityEndpoints
{
    public static IEndpointRouteBuilder MapConnectivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/connectivity");

        group.MapGet("/addresses", async (
            INetworkEnvironmentInspector networkInspector,
            IMdnsServiceDiscovery mdnsDiscovery,
            ICryptographicIdentityProvider identityProvider,
            ITlsCertificateProvider tlsProvider,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var net = await networkInspector.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var idResult = await identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
            var certResult = await tlsProvider.GetOrCreateServerCertificateAsync(Environment.MachineName, cancellationToken).ConfigureAwait(false);
            var tlsThumbprint = certResult.IsSuccess && certResult.Value is not null
                ? tlsProvider.GetSha256Thumbprint(certResult.Value)
                : null;

            var bindHttps = configuration.GetValue("Nexus:Tls:BindKestrelHttps", false);
            var httpsPort = configuration.GetValue("Nexus:Tls:HttpsPort", 5443);
            var httpPort = configuration.GetValue("Nexus:Tls:HttpPort", 5280);
            var scheme = bindHttps ? "https" : "http";
            var effectivePort = bindHttps ? httpsPort : httpPort;

            var localUrls = net.LocalIpv4Addresses
                .Select(ip => $"{scheme}://{ip}:{effectivePort}")
                .ToList();

            if (!bindHttps)
            {
                localUrls.Add($"http://127.0.0.1:{httpPort}");
            }

            var tailscaleUrls = net.TailscaleAddresses
                .Select(ip =>
                {
                    var formattedHost = ip.Contains(':', StringComparison.Ordinal) ? $"[{ip}]" : ip;
                    return $"{scheme}://{formattedHost}:{effectivePort}";
                })
                .ToList();

            var dto = new ServerConnectivityAddressesDto
            {
                DeviceId = idResult.IsSuccess ? idResult.Value.DeviceId : Guid.Empty,
                DeviceName = idResult.IsSuccess ? idResult.Value.CustomName : Environment.MachineName,
                Port = effectivePort,
                UseTls = bindHttps,
                LocalEndpoints = localUrls.AsReadOnly(),
                TailscaleEndpoints = tailscaleUrls.AsReadOnly(),
                TailscaleHostHint = $"{Environment.MachineName.ToLowerInvariant()}.<sua-tailnet>.ts.net:{effectivePort}",
                VpnStatus = net.VpnStatus,
                MdnsServiceType = MdnsPacketCodec.NexusServiceType,
                MdnsActive = mdnsDiscovery.IsAdvertising,
                TlsThumbprint = tlsThumbprint,
                HostKeyFingerprint = idResult.IsSuccess ? idResult.Value.PublicKeyFingerprint : null,
                GeneratedAtUtc = DateTimeOffset.UtcNow
            };

            return Results.Ok(NexusResponse<ServerConnectivityAddressesDto>.Success(dto, correlationId: GetCorrelationId(httpContext)));
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        group.MapGet("/status", (
            INexusRouteOrchestrator orchestrator,
            HttpContext httpContext) =>
        {
            return Results.Ok(NexusResponse<ConnectivityStatusReport>.Success(
                orchestrator.CurrentStatus,
                correlationId: GetCorrelationId(httpContext)));
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        group.MapGet("/endpoints", async (
            IEndpointRegistryRepository repository,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await repository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        group.MapPost("/endpoints", async (
            RegisterEndpointRequest request,
            INexusRouteOrchestrator orchestrator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await orchestrator.RegisterEndpointAsync(request, cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        group.MapDelete("/endpoints/{endpointId:guid}", async (
            Guid endpointId,
            INexusRouteOrchestrator orchestrator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await orchestrator.RemoveEndpointAsync(endpointId, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.Ok(NexusResponse<bool>.Success(true, correlationId: GetCorrelationId(httpContext)))
                : MapResult(Result<bool>.Failure(result.Error!), httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        group.MapPost("/probe", async (
            INexusRouteOrchestrator orchestrator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await orchestrator.ProbeAllEndpointsAsync(cancellationToken).ConfigureAwait(false);
            return MapResult(result, httpContext);
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        group.MapPost("/select-route", async (
            SelectRouteRequest? request,
            INexusRouteOrchestrator orchestrator,
            IConnectionManager connectionManager,
            IHubContext<NexusHub, INexusHubClient> hubContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await orchestrator.SelectAndConnectBestRouteAsync(request, cancellationToken).ConfigureAwait(false);
            await hubContext.Clients.All.ReceiveConnectionSnapshot(connectionManager.CurrentSnapshot).ConfigureAwait(false);
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

