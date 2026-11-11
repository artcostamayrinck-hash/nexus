using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Sync;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Sync;
using Nexus.Infrastructure.Security;

namespace Nexus.Infrastructure.Sync;

/// <summary>
/// HTTP + SignalR implementation of <see cref="INexusSyncTransport"/> targeting <c>/api/v1/sync</c>
/// with TLS certificate pinning, bearer token refresh, and SignalR real-time notification relay.
/// </summary>
public sealed class ServerBasicSyncTransport : INexusSyncTransport, IDisposable
{
    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly IRealtimeClientChannel _realtimeChannel;
    private readonly RealtimeClientOptions _options;
    private readonly ILogger<ServerBasicSyncTransport> _logger;

    public ServerBasicSyncTransport(
        NexusPairingProtocolClient protocolClient,
        IRealtimeCredentialProvider credentialProvider,
        IRealtimeClientChannel realtimeChannel,
        RealtimeClientOptions options,
        ILogger<ServerBasicSyncTransport> logger)
    {
        _protocolClient = protocolClient;
        _credentialProvider = credentialProvider;
        _realtimeChannel = realtimeChannel;
        _options = options;
        _logger = logger;

        _realtimeChannel.ClipboardReceived += OnChannelClipboardReceived;
        _realtimeChannel.NoteChangeReceived += OnChannelNoteChangeReceived;
    }

    public bool IsRealtimeAvailable => _realtimeChannel.IsConnected;

    public event EventHandler<ClipboardEntryContract>? RemoteClipboardReceived;
    public event EventHandler<NexusNoteContract>? RemoteNoteChangeReceived;

    public async Task<Result> PushClipboardEntryAsync(
        ClipboardEntryContract entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var responseResult = await SendWithRetryAsync<ClipboardEntryContract, ClipboardEntryContract>(
            HttpMethod.Post,
            "api/v1/sync/clipboard",
            entry,
            cancellationToken).ConfigureAwait(false);

        return responseResult.IsSuccess
            ? Result.Success()
            : Result.Failure(responseResult.Error!);
    }

    public Task<Result<IReadOnlyList<ClipboardEntryContract>>> PullClipboardHistoryAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        return SendWithRetryAsync<object, IReadOnlyList<ClipboardEntryContract>>(
            HttpMethod.Get,
            $"api/v1/sync/clipboard?limit={bounded}",
            body: null,
            cancellationToken);
    }

    public Task<Result<NoteSyncResponse>> SyncNotesAsync(
        NoteSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendWithRetryAsync<NoteSyncRequest, NoteSyncResponse>(
            HttpMethod.Post,
            "api/v1/sync/notes/sync",
            request,
            cancellationToken);
    }

    private async Task<Result<TResponse>> SendWithRetryAsync<TRequest, TResponse>(
        HttpMethod method,
        string relativePath,
        TRequest? body,
        CancellationToken cancellationToken)
    {
        var credsResult = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credsResult.IsFailure)
        {
            return Result<TResponse>.Failure(credsResult.Error!);
        }

        var creds = credsResult.Value;
        var baseEndpoint = creds.ServerEndpoint?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseEndpoint))
        {
            return Result<TResponse>.Failure(
                NexusError.Network("Endpoint do servidor indisponível para sincronização.", code: "SYNC_ENDPOINT_UNAVAILABLE"));
        }

        var attempt = await ExecuteSingleHttpAsync<TRequest, TResponse>(
            method,
            baseEndpoint,
            relativePath,
            creds,
            body,
            cancellationToken).ConfigureAwait(false);

        if (attempt.IsFailure &&
            attempt.Error?.Code == "REALTIME_AUTH_EXPIRED")
        {
            var refreshed = await _credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                var updatedCreds = refreshed.Value with
                {
                    ServerEndpoint = string.IsNullOrWhiteSpace(refreshed.Value.ServerEndpoint)
                        ? baseEndpoint
                        : refreshed.Value.ServerEndpoint
                };

                attempt = await ExecuteSingleHttpAsync<TRequest, TResponse>(
                    method,
                    updatedCreds.ServerEndpoint.Trim().TrimEnd('/'),
                    relativePath,
                    updatedCreds,
                    body,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return attempt;
    }

    private async Task<Result<TResponse>> ExecuteSingleHttpAsync<TRequest, TResponse>(
        HttpMethod method,
        string baseEndpoint,
        string relativePath,
        RealtimeCredentialBundle creds,
        TRequest? body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateHttpClient(baseEndpoint, creds.TlsThumbprint);
            using var request = new HttpRequestMessage(method, relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "REALTIME_AUTH_EXPIRED",
                    HttpStatusCode.Forbidden when raw.Contains("PERMISSION_DENIED_CLIPBOARD", StringComparison.OrdinalIgnoreCase) => "PERMISSION_DENIED_CLIPBOARD",
                    HttpStatusCode.Forbidden when raw.Contains("PERMISSION_DENIED_NOTES", StringComparison.OrdinalIgnoreCase) => "PERMISSION_DENIED_NOTES",
                    HttpStatusCode.Forbidden => "PERMISSION_DENIED",
                    _ => "SYNC_HTTP_FAILED"
                };

                var category = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ErrorCategory.Unauthorized
                    : ErrorCategory.Network;

                return Result<TResponse>.Failure(
                    new NexusError(code, $"Sync endpoint returned {(int)response.StatusCode}: {raw}", category));
            }

            var parsed = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                return Result<TResponse>.Failure(
                    NexusError.Unexpected("Resposta vazia ou inválida do servidor de sincronização.", code: "SYNC_EMPTY_RESPONSE"));
            }

            return Result<TResponse>.Success(parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha de comunicação de sincronização ao chamar {Method} {Path}.", method, relativePath);
            return Result<TResponse>.Failure(
                NexusError.Network($"Erro de conexão durante sincronização: {ex.Message}", code: "SYNC_NETWORK_ERROR"));
        }
    }

    private HttpClient CreateHttpClient(string baseEndpoint, string? tlsThumbprint)
    {
        if (_options.SignalRMessageHandlerFactory is not null)
        {
            var handler = _options.SignalRMessageHandlerFactory(baseEndpoint, tlsThumbprint);
            return new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(baseEndpoint.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(15)
            };
        }

        return _protocolClient.CreateHttpClient(baseEndpoint, tlsThumbprint, TimeSpan.FromSeconds(15));
    }

    private void OnChannelClipboardReceived(object? sender, ClipboardEntryContract entry)
        => RemoteClipboardReceived?.Invoke(this, entry);

    private void OnChannelNoteChangeReceived(object? sender, NexusNoteContract note)
        => RemoteNoteChangeReceived?.Invoke(this, note);

    public void Dispose()
    {
        _realtimeChannel.ClipboardReceived -= OnChannelClipboardReceived;
        _realtimeChannel.NoteChangeReceived -= OnChannelNoteChangeReceived;
    }
}
