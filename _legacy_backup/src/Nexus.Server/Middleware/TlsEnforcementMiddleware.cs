using Nexus.Contracts.Messaging;

namespace Nexus.Server.Middleware;

/// <summary>
/// Enforces TLS (HTTPS) transport when configured, rejecting plaintext HTTP calls cleanly.
/// </summary>
public sealed class TlsEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public TlsEnforcementMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requireHttps = _configuration.GetValue<bool>("Nexus:Tls:RequireHttps");
        if (requireHttps && !context.Request.IsHttps)
        {
            var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw) && raw is Guid gid
                ? gid
                : Guid.CreateVersion7();

            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            var error = new ContractError("TLS_REQUIRED", "Encrypted HTTPS/TLS connection is required for this endpoint.");
            await context.Response.WriteAsJsonAsync(
                NexusResponse<object>.Failure(error, correlationId: correlationId),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}

