using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Security;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Server.Options;

namespace Nexus.Server.Services;

public sealed record RelayStreamDownloadResult(
    Stream ContentStream,
    IDisposable ScopeToDispose,
    string FileName,
    long? ContentLength,
    string ExpectedSha256);

/// <summary>
/// Server-side authenticated GitHub Releases relay for Nexus.Server.
/// Holds optional private repository PAT strictly on the server (via environment/vault)
/// and serves verified manifests and APK streams to paired Android devices over NexusBearer TLS.
/// </summary>
public sealed class ServerUpdateRelayService
{
    private const int MaxRedirects = 5;
    private readonly GitHubUpdateRelayOptions _options;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IClock _clock;
    private readonly ILogger<ServerUpdateRelayService> _logger;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private readonly Dictionary<string, (NexusReleaseManifest Manifest, string AssetDownloadUri, DateTimeOffset ExpiresAtUtc)> _releaseCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> TrustedGitHubHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    };

    public ServerUpdateRelayService(
        GitHubUpdateRelayOptions options,
        ILocalDeviceContext localDevice,
        IClock clock,
        ILogger<ServerUpdateRelayService> logger)
    {
        _options = options;
        _localDevice = localDevice;
        _clock = clock;
        _logger = logger;
    }

    public UpdateRelayStatusContract GetRelayStatus()
    {
        var hasToken = !string.IsNullOrWhiteSpace(_options.ResolveToken());
        var configured = !string.IsNullOrWhiteSpace(_options.RepositoryOwner) &&
                         !string.IsNullOrWhiteSpace(_options.RepositoryName);

        return new UpdateRelayStatusContract
        {
            IsConfigured = configured,
            HasServerTokenConfigured = hasToken,
            RepositorySlug = $"{_options.RepositoryOwner}/{_options.RepositoryName}",
            ExpectedPackageId = _options.ExpectedPackageId,
            CurrentServerVersion = _localDevice.AppVersion,
            MinimumCompatibleServerVersion = _options.MinimumCompatibleServerVersion,
            StatusMessage = hasToken
                ? "Relay privado ativo com token GitHub isolado no servidor (nenhum PAT é exposto ao APK)."
                : "Relay configurado em modo público/sem token de servidor."
        };
    }

    public async Task<Result<NexusReleaseManifest>> GetLatestReleaseAsync(
        TrustedDeviceRecord requester,
        string? ownerOverride,
        string? repoOverride,
        bool? includePrereleases,
        CancellationToken cancellationToken = default)
    {
        if (!requester.Permissions.HasFlag(NexusPermission.ViewStatus))
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Forbidden(
                "Dispositivo não possui permissão ViewStatus para consultar atualizações.",
                code: "PERMISSION_DENIED_VIEWSTATUS"));
        }

        var owner = string.IsNullOrWhiteSpace(ownerOverride) ? _options.RepositoryOwner : ownerOverride.Trim();
        var repo = string.IsNullOrWhiteSpace(repoOverride) ? _options.RepositoryName : repoOverride.Trim();
        var allowPre = includePrereleases ?? _options.IncludePrereleases;

        if (!Uri.TryCreate(_options.GitHubApiBaseUrl, UriKind.Absolute, out var apiBase) ||
            !string.Equals(apiBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                "A URL base da API GitHub no servidor deve utilizar HTTPS.",
                code: "UPDATE_INSECURE_TRANSPORT"));
        }

        var token = _options.ResolveToken();
        var endpoint = allowPre
            ? new Uri(apiBase, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=10")
            : new Uri(apiBase, $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest");

        using var http = CreateGitHubHttpClient();
        try
        {
            using var resp = await SendGitHubRequestAsync(http, endpoint, token, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                    "Repositório privado no GitHub recusou a consulta do servidor (verifique a variável de ambiente do token).",
                    code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                        "Release ou repositório privado inacessível sem token configurado no Nexus.Server.",
                        code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
                }

                return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                    "Nenhuma release encontrada neste repositório privado.",
                    code: "UPDATE_RELEASE_NOT_FOUND"));
            }

            if (!resp.IsSuccessStatusCode)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                    $"Falha ao consultar GitHub Releases no relay: HTTP {(int)resp.StatusCode}.",
                    code: "UPDATE_REPOSITORY_UNAVAILABLE"));
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            GitHubReleaseDto? selected;
            if (allowPre)
            {
                var list = JsonSerializer.Deserialize<List<GitHubReleaseDto>>(json, JsonOptions);
                selected = list?.Where(r => !r.Draft)
                    .OrderByDescending(r => NexusSemanticVersion.TryParse(r.TagName, out var v) ? v : null)
                    .FirstOrDefault();
            }
            else
            {
                selected = JsonSerializer.Deserialize<GitHubReleaseDto>(json, JsonOptions);
            }

            if (selected is null || selected.Draft)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                    "Nenhuma release publicada válida encontrada.",
                    code: "UPDATE_RELEASE_NOT_FOUND"));
            }

            var apkAsset = selected.Assets?.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Name) && a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
            var manifestAsset = selected.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, "nexus-update-manifest.json", StringComparison.OrdinalIgnoreCase));

            if (apkAsset is null || manifestAsset is null)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                    "Release sem ativo .apk ou nexus-update-manifest.json.",
                    code: "UPDATE_MANIFEST_MISSING"));
            }

            var manFetchUri = SelectAssetDownloadUri(manifestAsset, token);
            if (manFetchUri is null)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                    "URL do manifesto da release é inválida ou não confiável.",
                    code: "UPDATE_UNTRUSTED_ORIGIN"));
            }

            var manAccept = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(manifestAsset.Url)
                ? "application/octet-stream"
                : "*/*";
            using var manResp = await SendGitHubRequestAsync(http, manFetchUri, token, manAccept, cancellationToken).ConfigureAwait(false);
            if (!manResp.IsSuccessStatusCode)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                    $"Falha ao baixar manifesto no relay: HTTP {(int)manResp.StatusCode}.",
                    code: manResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "UPDATE_PRIVATE_AUTH_REQUIRED"
                        : "UPDATE_REPOSITORY_UNAVAILABLE"));
            }

            var manifest = JsonSerializer.Deserialize<NexusReleaseManifest>(
                await manResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                JsonOptions);

            if (manifest is null)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Validation("Manifesto de release inválido.", code: "UPDATE_MANIFEST_INVALID"));
            }

            var apkFetchUri = SelectAssetDownloadUri(apkAsset, token);
            if (apkFetchUri is null)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized("URL de ativo APK não confiável.", code: "UPDATE_UNTRUSTED_ORIGIN"));
            }

            var normalized = manifest with
            {
                TagName = string.IsNullOrWhiteSpace(manifest.TagName) ? selected.TagName : manifest.TagName,
                ApkSizeBytes = manifest.ApkSizeBytes > 0 ? manifest.ApkSizeBytes : apkAsset.Size,
                PublishedAtUtc = manifest.PublishedAtUtc == default ? selected.PublishedAt : manifest.PublishedAtUtc,
                ReleaseNotes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? (selected.Body ?? string.Empty) : manifest.ReleaseNotes,
                DownloadUrl = $"https://nexus-relay/api/v1/updates/releases/{Uri.EscapeDataString(selected.TagName)}/download",
                ServedThroughServerRelay = true
            };

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _releaseCache[normalized.TagName] = (normalized, apkFetchUri.ToString(), _clock.UtcNow.Add(_options.MetadataCacheDuration));
            }
            finally
            {
                _cacheLock.Release();
            }

            return Result<NexusReleaseManifest>.Success(normalized);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Update relay GitHub error for {Owner}/{Repo}", owner, repo);
            return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                $"Repositório indisponível no relay: {ex.Message}",
                code: "UPDATE_REPOSITORY_UNAVAILABLE"));
        }
    }

    public async Task<Result<RelayStreamDownloadResult>> OpenAssetDownloadStreamAsync(
        TrustedDeviceRecord requester,
        string tagName,
        CancellationToken cancellationToken = default)
    {
        if (!requester.Permissions.HasFlag(NexusPermission.ViewStatus))
        {
            return Result<RelayStreamDownloadResult>.Failure(NexusError.Forbidden(
                "Dispositivo sem permissão ViewStatus para baixar atualização via relay.",
                code: "PERMISSION_DENIED_VIEWSTATUS"));
        }

        string? assetUriString = null;
        NexusReleaseManifest? manifest = null;

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_releaseCache.TryGetValue(tagName, out var cached) && cached.ExpiresAtUtc >= _clock.UtcNow)
            {
                manifest = cached.Manifest;
                assetUriString = cached.AssetDownloadUri;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        if (manifest is null || assetUriString is null)
        {
            var latestRes = await GetLatestReleaseAsync(requester, null, null, null, cancellationToken).ConfigureAwait(false);
            if (latestRes.IsFailure)
            {
                return Result<RelayStreamDownloadResult>.Failure(latestRes.Error!);
            }

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_releaseCache.TryGetValue(tagName, out var refreshed))
                {
                    manifest = refreshed.Manifest;
                    assetUriString = refreshed.AssetDownloadUri;
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        if (manifest is null || assetUriString is null || !Uri.TryCreate(assetUriString, UriKind.Absolute, out var targetUri))
        {
            return Result<RelayStreamDownloadResult>.Failure(NexusError.NotFound(
                $"Release '{tagName}' não encontrada no relay.",
                code: "UPDATE_RELEASE_NOT_FOUND"));
        }

        var token = _options.ResolveToken();
        var http = CreateGitHubHttpClient();
        try
        {
            var accept = targetUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                ? "application/octet-stream"
                : "*/*";
            var resp = await SendGitHubRequestAsync(http, targetUri, token, accept, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = resp.StatusCode;
                resp.Dispose();
                http.Dispose();
                return Result<RelayStreamDownloadResult>.Failure(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? NexusError.Unauthorized("Acesso negado ao ativo privado.", code: "UPDATE_PRIVATE_AUTH_REQUIRED")
                    : NexusError.Network($"Falha no stream GitHub via relay (HTTP {(int)status}).", code: "UPDATE_REPOSITORY_UNAVAILABLE"));
            }

            var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var scope = new CompositeDisposable(resp, http);
            return Result<RelayStreamDownloadResult>.Success(new RelayStreamDownloadResult(
                ContentStream: stream,
                ScopeToDispose: scope,
                FileName: manifest.ApkFileName,
                ContentLength: resp.Content.Headers.ContentLength ?? manifest.ApkSizeBytes,
                ExpectedSha256: manifest.ApkSha256));
        }
        catch (Exception ex)
        {
            http.Dispose();
            return Result<RelayStreamDownloadResult>.Failure(NexusError.Network(
                $"Falha ao iniciar download no relay: {ex.Message}",
                code: "UPDATE_DOWNLOAD_INTERRUPTED"));
        }
    }

    private Uri? SelectAssetDownloadUri(GitHubAssetDto asset, string? token)
    {
        var raw = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(asset.Url)
            ? asset.Url
            : asset.BrowserDownloadUrl;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsTrustedHost(uri.Host))
        {
            return null;
        }

        return uri;
    }

    private async Task<HttpResponseMessage> SendGitHubRequestAsync(
        HttpClient http,
        Uri initialUri,
        string? token,
        string acceptHeader,
        CancellationToken ct)
    {
        var currentUri = initialUri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!string.Equals(currentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !IsTrustedHost(currentUri.Host))
            {
                throw new HttpRequestException($"Destino GitHub não confiável rejeitado: {currentUri}");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("NEXUS-Server-UpdateRelay", "1.0"));
            if (!string.IsNullOrWhiteSpace(acceptHeader) && acceptHeader != "*/*")
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptHeader));
            }

            // Send server PAT ONLY to exact initial GitHub API host — NEVER forward credentials to redirect CDNs!
            if (!string.IsNullOrWhiteSpace(token) &&
                string.Equals(currentUri.Host, initialUri.Host, StringComparison.OrdinalIgnoreCase) &&
                currentUri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                var nextUri = loc.IsAbsoluteUri ? loc : new Uri(currentUri, loc);
                resp.Dispose();
                currentUri = nextUri;
                continue;
            }

            return resp;
        }

        throw new HttpRequestException("Excesso de redirecionamentos ao acessar GitHub Releases.");
    }

    private bool IsTrustedHost(string host)
    {
        if (TrustedGitHubHosts.Contains(host)) return true;
        if (Uri.TryCreate(_options.GitHubApiBaseUrl, UriKind.Absolute, out var customApi) &&
            string.Equals(customApi.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient CreateGitHubHttpClient()
    {
        HttpMessageHandler handler = _options.OutboundGitHubHandlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    private sealed class CompositeDisposable(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}

