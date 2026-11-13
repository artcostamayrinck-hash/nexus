using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Messaging;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Infrastructure.Security;

namespace Nexus.Infrastructure.Connectivity;

/// <summary>
/// Uploads larger binary payloads over HTTPS/HTTP directly to <c>/api/v1/realtime/transfers/payload</c>
/// using TLS certificate pinning and device bearer tokens, keeping SignalR control frames lightweight.
/// </summary>
public sealed class HttpBulkTransferClient : IHttpBulkTransferChannel
{
    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly ILogger<HttpBulkTransferClient> _logger;

    public HttpBulkTransferClient(
        NexusPairingProtocolClient protocolClient,
        IRealtimeCredentialProvider credentialProvider,
        ILogger<HttpBulkTransferClient> logger)
    {
        _protocolClient = protocolClient;
        _credentialProvider = credentialProvider;
        _logger = logger;
    }

    public async Task<Result<BulkPayloadTransferReceipt>> UploadLargePayloadAsync(
        ReadOnlyMemory<byte> payload,
        string contentType = "application/octet-stream",
        Guid? targetDeviceId = null,
        string? endpointOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (payload.IsEmpty)
        {
            return Result<BulkPayloadTransferReceipt>.Failure(
                NexusError.Validation("Payload para transferência HTTP não pode ser vazio.", code: "BULK_PAYLOAD_EMPTY"));
        }

        var credsResult = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credsResult.IsFailure)
        {
            return Result<BulkPayloadTransferReceipt>.Failure(credsResult.Error!);
        }

        var creds = credsResult.Value;
        var baseEndpoint = string.IsNullOrWhiteSpace(endpointOverride)
            ? creds.ServerEndpoint
            : endpointOverride.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseEndpoint))
        {
            return Result<BulkPayloadTransferReceipt>.Failure(
                NexusError.Validation("Endpoint do servidor não configurado para transferência HTTP.", code: "ENDPOINT_EMPTY"));
        }

        try
        {
            using var client = _protocolClient.CreateHttpClient(baseEndpoint, creds.TlsThumbprint, TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/realtime/transfers/payload")
            {
                Content = new ByteArrayContent(payload.ToArray())
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim());

            if (targetDeviceId.HasValue && targetDeviceId.Value != Guid.Empty)
            {
                request.Headers.TryAddWithoutValidation("X-Nexus-Target-DeviceId", targetDeviceId.Value.ToString());
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var code = (int)response.StatusCode switch
                {
                    401 => "REALTIME_AUTH_EXPIRED",
                    403 => "DEVICE_REVOKED",
                    413 => "BULK_PAYLOAD_TOO_LARGE",
                    _ => "BULK_TRANSFER_FAILED"
                };

                var category = (int)response.StatusCode is 401 or 403
                    ? ErrorCategory.Unauthorized
                    : ErrorCategory.Network;

                return Result<BulkPayloadTransferReceipt>.Failure(
                    new NexusError(code, $"Transferência HTTP falhou com status {(int)response.StatusCode}: {body}", category));
            }

            var envelope = await response.Content
                .ReadFromJsonAsync<NexusResponse<BulkPayloadTransferReceipt>>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (envelope is null || !envelope.IsSuccess || envelope.Payload is null)
            {
                return Result<BulkPayloadTransferReceipt>.Failure(
                    NexusError.Unexpected(envelope?.Error?.Message ?? "Resposta inválida do servidor na transferência HTTP."));
            }

            return Result<BulkPayloadTransferReceipt>.Success(envelope.Payload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bulk HTTP payload upload failed to {Endpoint}.", baseEndpoint);
            return Result<BulkPayloadTransferReceipt>.Failure(
                NexusError.Network($"Falha na transferência HTTP para '{baseEndpoint}': {ex.Message}", exception: ex));
        }
    }
}
