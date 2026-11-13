using Microsoft.AspNetCore.Diagnostics;
using Nexus.Contracts.Messaging;
using Nexus.Core.Errors;

namespace Nexus.Server.Middleware;

/// <summary>
/// Translates unhandled domain and infrastructure exceptions into standardized NexusResponse payloads.
/// </summary>
public sealed class NexusExceptionHandler : IExceptionHandler
{
    private readonly ILogger<NexusExceptionHandler> _logger;

    public NexusExceptionHandler(ILogger<NexusExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw) && raw is Guid gid
            ? gid
            : Guid.CreateVersion7();

        var (statusCode, error) = exception switch
        {
            NexusValidationException validationEx => (StatusCodes.Status400BadRequest, validationEx.Error),
            NexusStorageException storageEx => (StatusCodes.Status503ServiceUnavailable, storageEx.Error),
            NexusException nexusEx => (MapStatusCode(nexusEx.Error.Category), nexusEx.Error),
            OperationCanceledException => (499, NexusError.Cancelled()),
            _ => (StatusCodes.Status500InternalServerError,
                NexusError.Unexpected("An unexpected server error occurred.", detail: exception.Message))
        };

        _logger.LogError(exception, "Unhandled request exception ({Code}) mapped to HTTP {StatusCode}", error.Code, statusCode);

        httpContext.Response.StatusCode = statusCode;
        var payload = NexusResponse<object>.Failure(error.ToContractError(), correlationId: correlationId);
        await httpContext.Response.WriteAsJsonAsync(payload, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static int MapStatusCode(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation => StatusCodes.Status400BadRequest,
        ErrorCategory.NotFound => StatusCodes.Status404NotFound,
        ErrorCategory.Conflict => StatusCodes.Status409Conflict,
        ErrorCategory.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCategory.Storage => StatusCodes.Status503ServiceUnavailable,
        ErrorCategory.Timeout => StatusCodes.Status504GatewayTimeout,
        ErrorCategory.Cancelled => 499,
        _ => StatusCodes.Status500InternalServerError
    };
}

