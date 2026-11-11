using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Modules;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Modules;
using Nexus.Infrastructure.Security;

namespace Nexus.Infrastructure.Modules;

/// <summary>
/// HTTP client targeting <c>/api/v1/modules/*</c> for Android/Windows remote module invocation
/// with TLS pinning, bearer token refresh on 401, and SystemTextJsonSerializer compatibility.
/// </summary>
public sealed class HttpIntegrationModulesClient : IIntegrationModulesClient
{
    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly RealtimeClientOptions _options;
    private readonly ILogger<HttpIntegrationModulesClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new SystemTextJsonSerializer().Options;

    public HttpIntegrationModulesClient(
        NexusPairingProtocolClient protocolClient,
        IRealtimeCredentialProvider credentialProvider,
        RealtimeClientOptions options,
        ILogger<HttpIntegrationModulesClient> logger)
    {
        _protocolClient = protocolClient;
        _credentialProvider = credentialProvider;
        _options = options;
        _logger = logger;
    }

    public Task<Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>> GetRemoteModuleStatusesAsync(
        CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, IReadOnlyList<IntegrationModuleStatusSnapshot>>(
            HttpMethod.Get,
            "api/v1/modules/status",
            body: null,
            cancellationToken);

    public Task<Result<IntegrationModuleStatusSnapshot>> SetRemoteModuleEnabledAsync(
        UpdateModuleToggleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendWithRetryAsync<UpdateModuleToggleRequest, IntegrationModuleStatusSnapshot>(
            HttpMethod.Post,
            "api/v1/modules/toggle",
            request,
            cancellationToken);
    }

    public Task<Result<RemoteControlExecutionReceipt>> SendControlCommandAsync(
        RemoteControlCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendWithRetryAsync<RemoteControlCommandRequest, RemoteControlExecutionReceipt>(
            HttpMethod.Post,
            "api/v1/modules/control/execute",
            request,
            cancellationToken);
    }

    public Task<Result<ControlModuleSettings>> GetRemoteControlSettingsAsync(CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, ControlModuleSettings>(
            HttpMethod.Get,
            "api/v1/modules/control/settings",
            body: null,
            cancellationToken);

    public Task<Result<ControlModuleSettings>> SaveRemoteControlSettingsAsync(
        ControlModuleSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SendWithRetryAsync<ControlModuleSettings, ControlModuleSettings>(
            HttpMethod.Put,
            "api/v1/modules/control/settings",
            settings,
            cancellationToken);
    }

    public Task<Result<PushMobileNotificationReceipt>> PushMobileNotificationAsync(
        PushMobileNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendWithRetryAsync<PushMobileNotificationRequest, PushMobileNotificationReceipt>(
            HttpMethod.Post,
            "api/v1/modules/notify/push",
            request,
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<MobileNotificationRecord>>> GetRemoteNotificationHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        return SendWithRetryAsync<object, IReadOnlyList<MobileNotificationRecord>>(
            HttpMethod.Get,
            $"api/v1/modules/notify/history?limit={safeLimit}",
            body: null,
            cancellationToken);
    }

    public async Task<Result> DismissRemoteNotificationAsync(
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var res = await SendWithRetryAsync<object, object>(
            HttpMethod.Post,
            $"api/v1/modules/notify/{notificationId:D}/dismiss",
            body: null,
            cancellationToken).ConfigureAwait(false);
        return res.IsSuccess ? Result.Success() : Result.Failure(res.Error!);
    }

    public Task<Result<HostTelemetrySnapshot>> GetRemoteMonitorSnapshotAsync(CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, HostTelemetrySnapshot>(
            HttpMethod.Get,
            "api/v1/modules/monitor/snapshot",
            body: null,
            cancellationToken);

    public Task<Result<MediaPlaybackStateContract>> GetRemoteMediaStateAsync(CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, MediaPlaybackStateContract>(
            HttpMethod.Get,
            "api/v1/modules/media/state",
            body: null,
            cancellationToken);

    public Task<Result<MediaPlaybackStateContract>> SendMediaCommandAsync(
        MediaControlCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendWithRetryAsync<MediaControlCommandRequest, MediaPlaybackStateContract>(
            HttpMethod.Post,
            "api/v1/modules/media/command",
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
            ignoreResponseBodyOnSuccess: relativePath.EndsWith("/clear", StringComparison.Ordinal),
            missingEndpointCode: "MODULES_ENDPOINT_EMPTY",
            missingEndpointMessage: "Endpoint do servidor não configurado.",
            cancellationToken: cancellationToken);
}
