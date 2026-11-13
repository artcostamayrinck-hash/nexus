using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;

namespace Nexus.Infrastructure.Security;

/// <summary>
/// Shared authenticated HTTP/JSON request executor that handles TLS-pinned pooled client creation,
/// Bearer header attachment, one-shot credential refresh on HTTP 401, and structured error mapping.
/// </summary>
internal static class NexusAuthenticatedJsonHttpExecutor
{
    public static readonly JsonSerializerOptions SharedJsonOptions = new SystemTextJsonSerializer().Options;

    public static async Task<Result<TResponse>> SendWithRetryAsync<TRequest, TResponse>(
        NexusPairingProtocolClient protocolClient,
        IRealtimeCredentialProvider credentialProvider,
        RealtimeClientOptions options,
        ILogger logger,
        HttpMethod method,
        string relativePath,
        TRequest? body,
        TimeSpan? timeout = null,
        bool ignoreResponseBodyOnSuccess = false,
        string missingEndpointCode = "ENDPOINT_UNAVAILABLE",
        string missingEndpointMessage = "Endpoint do servidor indisponível.",
        Func<HttpResponseMessage, string, NexusError>? customErrorMapper = null,
        CancellationToken cancellationToken = default)
    {
        var credsRes = await credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credsRes.IsFailure)
        {
            return Result<TResponse>.Failure(credsRes.Error!);
        }

        var creds = credsRes.Value;
        var baseEndpoint = creds.ServerEndpoint?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseEndpoint))
        {
            return Result<TResponse>.Failure(
                NexusError.Network(missingEndpointMessage, code: missingEndpointCode));
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
        var (result, wasUnauthorized) = await SendOnceAsync<TRequest, TResponse>(
            protocolClient,
            options,
            logger,
            method,
            baseEndpoint,
            relativePath,
            creds,
            body,
            effectiveTimeout,
            ignoreResponseBodyOnSuccess,
            customErrorMapper,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure && wasUnauthorized)
        {
            var refreshed = await credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                var updated = refreshed.Value with
                {
                    ServerEndpoint = string.IsNullOrWhiteSpace(refreshed.Value.ServerEndpoint)
                        ? baseEndpoint
                        : refreshed.Value.ServerEndpoint
                };

                (result, _) = await SendOnceAsync<TRequest, TResponse>(
                    protocolClient,
                    options,
                    logger,
                    method,
                    updated.ServerEndpoint.Trim().TrimEnd('/'),
                    relativePath,
                    updated,
                    body,
                    effectiveTimeout,
                    ignoreResponseBodyOnSuccess,
                    customErrorMapper,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    private static async Task<(Result<TResponse> Result, bool WasUnauthorized)> SendOnceAsync<TRequest, TResponse>(
        NexusPairingProtocolClient protocolClient,
        RealtimeClientOptions options,
        ILogger logger,
        HttpMethod method,
        string baseEndpoint,
        string relativePath,
        RealtimeCredentialBundle creds,
        TRequest? body,
        TimeSpan timeout,
        bool ignoreResponseBodyOnSuccess,
        Func<HttpResponseMessage, string, NexusError>? customErrorMapper,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = protocolClient.CreateHttpClient(
                baseEndpoint,
                creds.TlsThumbprint,
                timeout,
                options.SignalRMessageHandlerFactory);
            using var request = new HttpRequestMessage(method, relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);

            if (body is not null && method != HttpMethod.Get)
            {
                request.Content = JsonContent.Create(body, options: SharedJsonOptions);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var mappedError = customErrorMapper is not null
                    ? customErrorMapper(response, raw)
                    : ParseStandardServerError(response.StatusCode, response.ReasonPhrase, raw);
                return (Result<TResponse>.Failure(mappedError), response.StatusCode == HttpStatusCode.Unauthorized);
            }

            if (ignoreResponseBodyOnSuccess || typeof(TResponse) == typeof(object))
            {
                return (Result<TResponse>.Success(default!), false);
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<TResponse>(SharedJsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return parsed is null
                ? (Result<TResponse>.Failure(NexusError.Unexpected("Resposta vazia do servidor.", code: "EMPTY_RESPONSE")), false)
                : (Result<TResponse>.Success(parsed), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (Result<TResponse>.Failure(
                new NexusError("HTTP_TIMEOUT", "Tempo limite excedido na comunicação com o servidor.", ErrorCategory.Timeout)), false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha de comunicação HTTP ao chamar {Method} {Path}.", method, relativePath);
            return (Result<TResponse>.Failure(
                NexusError.Network($"Erro de conexão HTTP: {ex.Message}", code: "HTTP_NETWORK_ERROR")), false);
        }
    }

    public static NexusError ParseStandardServerError(HttpStatusCode statusCode, string? reasonPhrase, string raw)
    {
        string? code = null;
        string? message = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    code = c.GetString();
                }

                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    message = m.GetString();
                }
            }
        }
        catch
        {
            // Non-JSON error body: fall through to HTTP status fallbacks below.
        }

        if (code is not null || message is not null)
        {
            var resolvedCode = code ?? $"HTTP_{(int)statusCode}";
            var resolvedMsg = message ?? $"Erro HTTP {(int)statusCode} ({reasonPhrase}).";
            return statusCode switch
            {
                HttpStatusCode.Unauthorized => NexusError.Unauthorized(resolvedMsg, resolvedCode),
                HttpStatusCode.Forbidden => NexusError.Forbidden(resolvedMsg, resolvedCode),
                HttpStatusCode.BadRequest => NexusError.Validation(resolvedMsg, code: resolvedCode),
                HttpStatusCode.NotFound => NexusError.NotFound(resolvedMsg, resolvedCode),
                HttpStatusCode.Conflict => NexusError.Conflict(resolvedMsg, resolvedCode),
                _ => NexusError.Network(resolvedMsg, resolvedCode)
            };
        }

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => NexusError.Unauthorized($"Erro HTTP {(int)statusCode}.", "HTTP_401"),
            HttpStatusCode.Forbidden => NexusError.Forbidden("Acesso negado ao recurso (HTTP 403).", "PERMISSION_DENIED"),
            _ => NexusError.Network($"Falha HTTP {(int)statusCode} ({reasonPhrase}).", $"HTTP_{(int)statusCode}")
        };
    }
}
