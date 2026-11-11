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

    private Task<Result<TResponse>> SendWithRetryAsync<TRequest, TResponse>(
        HttpMethod method,
        string relativePath,
        TRequest? body,
        CancellationToken cancellationToken)
        => NexusAuthenticatedJsonHttpExecutor.SendWithRetryAsync<TRequest, TResponse>(
            _protocolClient,
            _credentialProvider,
            _options,
            _logger,
            method,
            relativePath,
            body,
            timeout: TimeSpan.FromSeconds(15),
            missingEndpointCode: "SYNC_ENDPOINT_UNAVAILABLE",
            missingEndpointMessage: "Endpoint do servidor indisponível para sincronização.",
            customErrorMapper: static (response, raw) =>
            {
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

                return new NexusError(code, $"Sync endpoint returned {(int)response.StatusCode}: {raw}", category);
            },
            cancellationToken: cancellationToken);

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
