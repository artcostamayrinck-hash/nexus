using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Automation;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Infrastructure.Security;

namespace Nexus.Infrastructure.Automation;

/// <summary>
/// HTTP client targeting <c>/api/v1/panel/*</c> and <c>/api/v1/automations/*</c> with TLS pinning,
/// bearer token refresh on HTTP 401, and SystemTextJsonSerializer compatibility.
/// </summary>
public sealed class HttpPanelAndAutomationClient : IPanelAndAutomationClient
{
    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly RealtimeClientOptions _options;
    private readonly ILogger<HttpPanelAndAutomationClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new SystemTextJsonSerializer().Options;

    public HttpPanelAndAutomationClient(
        NexusPairingProtocolClient protocolClient,
        IRealtimeCredentialProvider credentialProvider,
        RealtimeClientOptions options,
        ILogger<HttpPanelAndAutomationClient> logger)
    {
        _protocolClient = protocolClient;
        _credentialProvider = credentialProvider;
        _options = options;
        _logger = logger;
    }

    public Task<Result<IReadOnlyList<PanelProfile>>> GetRemoteProfilesAsync(CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, IReadOnlyList<PanelProfile>>(
            HttpMethod.Get,
            "api/v1/panel/profiles",
            body: null,
            cancellationToken);

    public Task<Result<PanelProfile>> SaveRemoteProfileAsync(PanelProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SendWithRetryAsync<PanelProfile, PanelProfile>(
            HttpMethod.Put,
            "api/v1/panel/profiles",
            profile,
            cancellationToken);
    }

    public Task<Result<PanelProfile>> ActivateRemoteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, PanelProfile>(
            HttpMethod.Post,
            $"api/v1/panel/profiles/{profileId:D}/activate",
            body: null,
            cancellationToken);

    public Task<Result<AutomationEngineSettings>> GetRemoteEngineSettingsAsync(CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, AutomationEngineSettings>(
            HttpMethod.Get,
            "api/v1/automations/settings",
            body: null,
            cancellationToken);

    public Task<Result<AutomationEngineSettings>> SaveRemoteEngineSettingsAsync(
        AutomationEngineSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SendWithRetryAsync<AutomationEngineSettings, AutomationEngineSettings>(
            HttpMethod.Put,
            "api/v1/automations/settings",
            settings,
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<AutomationRule>>> GetRemoteRulesAsync(CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, IReadOnlyList<AutomationRule>>(
            HttpMethod.Get,
            "api/v1/automations/rules",
            body: null,
            cancellationToken);

    public Task<Result<AutomationRule>> SaveRemoteRuleAsync(AutomationRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return SendWithRetryAsync<AutomationRule, AutomationRule>(
            HttpMethod.Put,
            "api/v1/automations/rules",
            rule,
            cancellationToken);
    }

    public async Task<Result> DeleteRemoteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var res = await SendWithRetryAsync<object, object>(
            HttpMethod.Delete,
            $"api/v1/automations/rules/{ruleId:D}",
            body: null,
            cancellationToken).ConfigureAwait(false);
        return res.IsSuccess ? Result.Success() : Result.Failure(res.Error!);
    }

    public Task<Result<IReadOnlyList<AutomationExecutionRecord>>> GetRemoteHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => SendWithRetryAsync<object, IReadOnlyList<AutomationExecutionRecord>>(
            HttpMethod.Get,
            $"api/v1/automations/history?limit={Math.Clamp(limit, 1, 500)}",
            body: null,
            cancellationToken);

    public Task<Result<IReadOnlyList<AutomationExecutionRecord>>> DispatchRemoteTriggerAsync(
        AutomationTriggerDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendWithRetryAsync<AutomationTriggerDispatchRequest, IReadOnlyList<AutomationExecutionRecord>>(
            HttpMethod.Post,
            "api/v1/automations/trigger",
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
            ignoreResponseBodyOnSuccess: method == HttpMethod.Delete,
            missingEndpointCode: "ENDPOINT_MISSING",
            missingEndpointMessage: "Endpoint do servidor não configurado.",
            cancellationToken: cancellationToken);
}

