using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Services;
using Nexus.Modules.Abstractions;
using Nexus.Modules.Registry;
using Nexus.Security.Identity;
using Nexus.Security.Tls;
using Nexus.Server.Authentication;
using Nexus.Server.Hubs;
using Nexus.Server.Middleware;

namespace Nexus.Server.Endpoints;

public sealed record SystemInfoDto(
    string ServiceName,
    string Version,
    string ContractVersion,
    DeviceInfo HostDevice,
    IReadOnlyList<ModuleDescriptor> Modules,
    int ActiveHubConnections,
    DateTimeOffset ServerTimeUtc,
    string? TlsThumbprint = null,
    string? HostKeyFingerprint = null);

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapNexusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthJsonAsync
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthJsonAsync
        });

        var api = endpoints.MapGroup("/api/v1");

        api.MapGet("/system/info", async (
            ILocalDeviceContext localDevice,
            ICryptographicIdentityProvider identityProvider,
            ITlsCertificateProvider tlsProvider,
            IModuleRegistry moduleRegistry,
            INexusPresenceTracker presence,
            IClock clock,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var correlationId = GetCorrelationId(httpContext);
            var idResult = await identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
            var certResult = await tlsProvider.GetOrCreateServerCertificateAsync(Environment.MachineName, cancellationToken).ConfigureAwait(false);
            var thumbprint = certResult.IsSuccess ? tlsProvider.GetSha256Thumbprint(certResult.Value) : null;

            var dto = new SystemInfoDto(
                ServiceName: "NEXUS.Server",
                Version: localDevice.AppVersion,
                ContractVersion: ContractVersions.Current,
                HostDevice: localDevice.ToDeviceInfo(lastSeenAtUtc: clock.UtcNow),
                Modules: moduleRegistry.GetDescriptors(),
                ActiveHubConnections: presence.GetConnectedSessions().Count,
                ServerTimeUtc: clock.UtcNow,
                TlsThumbprint: thumbprint,
                HostKeyFingerprint: idResult.IsSuccess ? idResult.Value.PublicKeyFingerprint : null);

            return Results.Ok(NexusResponse<SystemInfoDto>.Success(dto, correlationId: correlationId));
        });

        api.MapGet("/system/health", async (
            DiagnosticHealthService healthService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var correlationId = GetCorrelationId(httpContext);
            var report = await healthService.CaptureAsync(cancellationToken).ConfigureAwait(false);
            return report.IsSuccess
                ? Results.Ok(NexusResponse<SubsystemHealthReport>.Success(report.Value, correlationId: correlationId))
                : Results.Json(NexusResponse<SubsystemHealthReport>.Failure(report.Error!.ToContractError(), correlationId: correlationId), statusCode: 503);
        });

        api.MapGet("/devices", async (
            IDeviceRegistry deviceRegistry,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var correlationId = GetCorrelationId(httpContext);
            var result = await deviceRegistry.GetAllDevicesAsync(cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.Ok(NexusResponse<IReadOnlyList<DeviceInfo>>.Success(result.Value, correlationId: correlationId))
                : Results.Json(NexusResponse<IReadOnlyList<DeviceInfo>>.Failure(result.Error!.ToContractError(), correlationId: correlationId), statusCode: 503);
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        api.MapPost("/devices", async (
            DeviceRegistrationContract contract,
            IDeviceRegistry deviceRegistry,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var correlationId = GetCorrelationId(httpContext);
            var result = await deviceRegistry.RegisterOrUpdateAsync(contract, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return Results.Ok(NexusResponse<DeviceInfo>.Success(result.Value, correlationId: correlationId));
            }

            var status = result.Error!.Category == ErrorCategory.Validation
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status503ServiceUnavailable;

            return Results.Json(
                NexusResponse<DeviceInfo>.Failure(result.Error.ToContractError(), correlationId: correlationId),
                statusCode: status);
        }).RequireAuthorization(NexusAuthDefaults.PolicyManageDevices);

        api.MapGet("/connections/state", (
            IConnectionManager connectionManager,
            HttpContext httpContext) =>
        {
            var correlationId = GetCorrelationId(httpContext);
            return Results.Ok(NexusResponse<ConnectionStateSnapshot>.Success(
                connectionManager.CurrentSnapshot,
                correlationId: correlationId));
        }).RequireAuthorization(NexusAuthDefaults.PolicyViewStatus);

        return endpoints;
    }

    private static Guid GetCorrelationId(HttpContext httpContext)
        => httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw) && raw is Guid id
            ? id
            : Guid.CreateVersion7();

    private static Task WriteHealthJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = e.Value.Duration.TotalMilliseconds,
                    data = e.Value.Data
                })
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}

