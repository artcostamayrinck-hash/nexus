using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Transfers;
using Nexus.Infrastructure.Security;

namespace Nexus.Infrastructure.Transfers;

/// <summary>
/// HTTP streaming transport for bidirectional NEXUS file transfers supporting chunked uploads,
/// HTTP Range resumable downloads, bearer token auto-refresh, and TLS certificate pinning.
/// File payloads are never routed through SignalR.
/// </summary>
public sealed class HttpTransferTransport : IFileTransferTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new SystemTextJsonSerializer().Options;

    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly RealtimeClientOptions _options;
    private readonly ILogger<HttpTransferTransport> _logger;

    public HttpTransferTransport(
        NexusPairingProtocolClient protocolClient,
        IRealtimeCredentialProvider credentialProvider,
        RealtimeClientOptions options,
        ILogger<HttpTransferTransport> logger)
    {
        _protocolClient = protocolClient;
        _credentialProvider = credentialProvider;
        _options = options;
        _logger = logger;
    }

    public Task<Result<InitiateFileTransferResponse>> InitiateTransferAsync(
        InitiateFileTransferRequest request,
        CancellationToken cancellationToken = default)
        => SendJsonAsync<InitiateFileTransferRequest, InitiateFileTransferResponse>(
            HttpMethod.Post,
            "api/v1/transfers/initiate",
            request,
            cancellationToken);

    public async Task<Result<ChunkUploadResponse>> UploadChunkAsync(
        Guid transferId,
        long offset,
        Stream chunkStream,
        long chunkLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkStream);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var credsResult = attempt == 0
                ? await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
                : await _credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);

            if (credsResult.IsFailure)
            {
                return Result<ChunkUploadResponse>.Failure(credsResult.Error!);
            }

            var creds = credsResult.Value;
            if (string.IsNullOrWhiteSpace(creds.ServerEndpoint))
            {
                return Result<ChunkUploadResponse>.Failure(
                    NexusError.Validation("Endpoint do servidor ausente para envio de arquivo.", code: "ENDPOINT_EMPTY"));
            }

            if (chunkStream.CanSeek)
            {
                chunkStream.Seek(0, SeekOrigin.Begin);
            }

            try
            {
                using var client = CreateHttpClient(creds.ServerEndpoint, creds.TlsThumbprint);
                using var boundedContent = new StreamContent(chunkStream, bufferSize: 64 * 1024);
                boundedContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (chunkLength >= 0)
                {
                    boundedContent.Headers.ContentLength = chunkLength;
                }

                using var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"api/v1/transfers/{transferId:D}/chunks?offset={offset}")
                {
                    Content = boundedContent
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && chunkStream.CanSeek)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Result<ChunkUploadResponse>.Failure(await MapFailureAsync(response, cancellationToken).ConfigureAwait(false));
                }

                var parsed = await response.Content
                    .ReadFromJsonAsync<ChunkUploadResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                return parsed is null
                    ? Result<ChunkUploadResponse>.Failure(NexusError.Unexpected("Resposta vazia ao enviar chunk.", code: "TRANSFER_EMPTY_RESPONSE"))
                    : Result<ChunkUploadResponse>.Success(parsed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro de rede ao enviar chunk da transferência {TransferId} (offset {Offset}).", transferId, offset);
                return Result<ChunkUploadResponse>.Failure(
                    NexusError.Network($"Falha de rede durante streaming do chunk: {ex.Message}", code: "TRANSFER_NETWORK_INTERRUPTED"));
            }
        }

        return Result<ChunkUploadResponse>.Failure(
            NexusError.Unauthorized("Falha ao autenticar envio de chunk.", code: "REALTIME_AUTH_EXPIRED"));
    }

    public Task<Result<FileTransferRecord>> FinalizeUploadAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
        => SendJsonAsync<object, FileTransferRecord>(
            HttpMethod.Post,
            $"api/v1/transfers/{transferId:D}/finalize-upload",
            new { },
            cancellationToken);

    public async Task<Result<long>> DownloadToPartFileAsync(
        FileTransferRecord transfer,
        string partFilePath,
        long existingOffsetBytes,
        Action<long, double>? progressCallback,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var credsResult = attempt == 0
                ? await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
                : await _credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);

            if (credsResult.IsFailure)
            {
                return Result<long>.Failure(credsResult.Error!);
            }

            var creds = credsResult.Value;
            if (string.IsNullOrWhiteSpace(creds.ServerEndpoint))
            {
                return Result<long>.Failure(
                    NexusError.Validation("Endpoint do servidor ausente para download.", code: "ENDPOINT_EMPTY"));
            }

            try
            {
                using var client = CreateHttpClient(creds.ServerEndpoint, creds.TlsThumbprint);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/v1/transfers/{transfer.TransferId:D}/download");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);

                if (existingOffsetBytes > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existingOffsetBytes, null);
                }

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                    && existingOffsetBytes > 0
                    && existingOffsetBytes == transfer.TotalBytes)
                {
                    progressCallback?.Invoke(existingOffsetBytes, 0);
                    return Result<long>.Success(existingOffsetBytes);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Result<long>.Failure(await MapFailureAsync(response, cancellationToken).ConfigureAwait(false));
                }

                var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
                var startOffset = isPartial ? existingOffsetBytes : 0L;

                var parentDir = Path.GetDirectoryName(Path.GetFullPath(partFilePath));
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fileStream = new FileStream(
                    partFilePath,
                    isPartial ? FileMode.OpenOrCreate : FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous);

                if (isPartial)
                {
                    fileStream.Seek(startOffset, SeekOrigin.Begin);
                }

                var buffer = new byte[64 * 1024];
                var totalWritten = startOffset;
                long sessionBytes = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReportMs = 0L;

                while (true)
                {
                    var read = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    totalWritten += read;
                    sessionBytes += read;

                    var elapsedMs = stopwatch.ElapsedMilliseconds;
                    if (elapsedMs - lastReportMs >= 80 || totalWritten >= transfer.TotalBytes)
                    {
                        var speed = elapsedMs > 0 ? sessionBytes * 1000d / elapsedMs : sessionBytes;
                        progressCallback?.Invoke(totalWritten, speed);
                        lastReportMs = elapsedMs;
                    }
                }

                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                var finalSpeed = stopwatch.Elapsed.TotalSeconds > 0
                    ? sessionBytes / stopwatch.Elapsed.TotalSeconds
                    : sessionBytes;
                progressCallback?.Invoke(totalWritten, finalSpeed);

                return Result<long>.Success(totalWritten);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro de rede durante download da transferência {TransferId}.", transfer.TransferId);
                return Result<long>.Failure(
                    NexusError.Network($"Conexão interrompida durante download: {ex.Message}", code: "TRANSFER_NETWORK_INTERRUPTED"));
            }
        }

        return Result<long>.Failure(
            NexusError.Unauthorized("Falha ao autenticar download de arquivo.", code: "REALTIME_AUTH_EXPIRED"));
    }

    public Task<Result<FileTransferRecord>> AcknowledgeTransferAsync(
        AcknowledgeFileTransferRequest request,
        CancellationToken cancellationToken = default)
        => SendJsonAsync<AcknowledgeFileTransferRequest, FileTransferRecord>(
            HttpMethod.Post,
            $"api/v1/transfers/{request.TransferId:D}/acknowledge",
            request,
            cancellationToken);

    public async Task<Result<FileTransferRecord?>> GetTransferStatusAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        var result = await SendJsonAsync<object, FileTransferRecord>(
            HttpMethod.Get,
            $"api/v1/transfers/{transferId:D}",
            null,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure && result.Error!.Category == ErrorCategory.NotFound)
        {
            return Result<FileTransferRecord?>.Success(null);
        }

        return result.IsSuccess
            ? Result<FileTransferRecord?>.Success(result.Value)
            : Result<FileTransferRecord?>.Failure(result.Error!);
    }

    public async Task<Result<IReadOnlyList<FileTransferRecord>>> ListIncomingTransfersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendJsonAsync<object, IncomingTransfersResponse>(
            HttpMethod.Get,
            "api/v1/transfers/incoming",
            null,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result<IReadOnlyList<FileTransferRecord>>.Success(result.Value.Transfers)
            : Result<IReadOnlyList<FileTransferRecord>>.Failure(result.Error!);
    }

    public async Task<Result<FileTransferRecord?>> NotifyPauseAsync(
        Guid transferId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SendJsonAsync<TransferControlRequest, FileTransferRecord>(
            HttpMethod.Post,
            $"api/v1/transfers/{transferId:D}/pause",
            new TransferControlRequest { TransferId = transferId, Reason = reason },
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure && result.Error!.Category == ErrorCategory.NotFound)
        {
            return Result<FileTransferRecord?>.Success(null);
        }

        return result.IsSuccess
            ? Result<FileTransferRecord?>.Success(result.Value)
            : Result<FileTransferRecord?>.Failure(result.Error!);
    }

    public async Task<Result<FileTransferRecord?>> NotifyCancelAsync(
        Guid transferId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SendJsonAsync<TransferControlRequest, FileTransferRecord>(
            HttpMethod.Post,
            $"api/v1/transfers/{transferId:D}/cancel",
            new TransferControlRequest { TransferId = transferId, Reason = reason },
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure && result.Error!.Category == ErrorCategory.NotFound)
        {
            return Result<FileTransferRecord?>.Success(null);
        }

        return result.IsSuccess
            ? Result<FileTransferRecord?>.Success(result.Value)
            : Result<FileTransferRecord?>.Failure(result.Error!);
    }

    private async Task<Result<TResponse>> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string relativePath,
        TRequest? body,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var credsResult = attempt == 0
                ? await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
                : await _credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);

            if (credsResult.IsFailure)
            {
                return Result<TResponse>.Failure(credsResult.Error!);
            }

            var creds = credsResult.Value;
            if (string.IsNullOrWhiteSpace(creds.ServerEndpoint))
            {
                return Result<TResponse>.Failure(
                    NexusError.Validation("Endpoint do servidor não configurado.", code: "ENDPOINT_EMPTY"));
            }

            try
            {
                using var client = CreateHttpClient(creds.ServerEndpoint, creds.TlsThumbprint);
                using var request = new HttpRequestMessage(method, relativePath);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);

                if (body is not null && method != HttpMethod.Get)
                {
                    request.Content = JsonContent.Create(body, options: JsonOptions);
                }

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Result<TResponse>.Failure(await MapFailureAsync(response, cancellationToken).ConfigureAwait(false));
                }

                var parsed = await response.Content
                    .ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                return parsed is null
                    ? Result<TResponse>.Failure(NexusError.Unexpected("Resposta vazia do servidor de transferência.", code: "TRANSFER_EMPTY_RESPONSE"))
                    : Result<TResponse>.Success(parsed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro de rede em {Method} {Path}.", method, relativePath);
                return Result<TResponse>.Failure(
                    NexusError.Network($"Erro de conexão durante transferência: {ex.Message}", code: "TRANSFER_NETWORK_INTERRUPTED"));
            }
        }

        return Result<TResponse>.Failure(
            NexusError.Unauthorized("Falha de autenticação no endpoint de transferência.", code: "REALTIME_AUTH_EXPIRED"));
    }

    private HttpClient CreateHttpClient(string baseEndpoint, string? tlsThumbprint)
        => _protocolClient.CreateHttpClient(baseEndpoint, tlsThumbprint, TimeSpan.FromMinutes(10), _options.SignalRMessageHandlerFactory);

    private static async Task<NexusError> MapFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => NexusError.Unauthorized(
                string.IsNullOrWhiteSpace(raw) ? "Sessão expirada ou dispositivo não autorizado." : raw,
                code: "REALTIME_AUTH_EXPIRED"),
            HttpStatusCode.Forbidden when raw.Contains("PERMISSION_DENIED_FILE_TRANSFER", StringComparison.OrdinalIgnoreCase) =>
                NexusError.Forbidden(raw, code: "PERMISSION_DENIED_FILE_TRANSFER"),
            HttpStatusCode.Forbidden => NexusError.Forbidden(
                string.IsNullOrWhiteSpace(raw) ? "Permissão FileTransfer recusada ou dispositivo revogado." : raw,
                code: "PERMISSION_DENIED_FILE_TRANSFER"),
            HttpStatusCode.InsufficientStorage => NexusError.Storage(
                string.IsNullOrWhiteSpace(raw) ? "Espaço insuficiente no servidor para receber o arquivo." : raw,
                code: "INSUFFICIENT_DISK_SPACE"),
            HttpStatusCode.NotFound => NexusError.NotFound(
                string.IsNullOrWhiteSpace(raw) ? "Transferência não encontrada no servidor." : raw,
                code: "TRANSFER_NOT_FOUND"),
            HttpStatusCode.Conflict when raw.Contains("CHECKSUM_MISMATCH", StringComparison.OrdinalIgnoreCase) =>
                NexusError.Conflict(raw, code: "CHECKSUM_MISMATCH"),
            HttpStatusCode.Conflict => NexusError.Conflict(
                string.IsNullOrWhiteSpace(raw) ? "Conflito no estado da transferência." : raw,
                code: "TRANSFER_CONFLICT"),
            HttpStatusCode.BadRequest => NexusError.Validation(
                string.IsNullOrWhiteSpace(raw) ? "Requisição de transferência inválida." : raw,
                code: "TRANSFER_VALIDATION_FAILED"),
            _ => NexusError.Network(
                $"Servidor retornou {(int)response.StatusCode}: {raw}",
                code: "TRANSFER_NETWORK_INTERRUPTED")
        };
    }
}

/// <summary>
/// Non-owning bounded sub-stream view over a readable stream window so HTTP chunks stream directly from disk without allocating intermediate copies.
/// </summary>
internal sealed class BoundedReadOnlySubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;

    public BoundedReadOnlySubStream(Stream inner, long startOffset, long length)
    {
        _inner = inner;
        _length = Math.Max(0, length);
        _inner.Seek(startOffset, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _inner.Seek(_inner.Position - _position + value, SeekOrigin.Begin);
            _position = value;
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, remaining);
        var read = _inner.Read(buffer, offset, toRead);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = next;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

