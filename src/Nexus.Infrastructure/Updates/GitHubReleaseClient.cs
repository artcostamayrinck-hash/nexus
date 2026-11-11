using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Updates;
using Nexus.Infrastructure.Security;

namespace Nexus.Infrastructure.Updates;

/// <summary>
/// Retrieves release metadata and streams APK binaries over HTTPS either directly from public GitHub Releases
/// (zero tokens in client) or via authenticated TLS-pinned <c>Nexus.Server</c> relay for private repositories.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseSource
{
    private const int MaxRedirects = 5;
    private readonly NexusUpdateOptions _updateOptions;
    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly RealtimeClientOptions _realtimeOptions;
    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly ILogger<GitHubReleaseClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public GitHubReleaseClient(
        NexusUpdateOptions updateOptions,
        IRealtimeCredentialProvider credentialProvider,
        RealtimeClientOptions realtimeOptions,
        NexusPairingProtocolClient protocolClient,
        ILogger<GitHubReleaseClient> logger)
    {
        _updateOptions = updateOptions;
        _credentialProvider = credentialProvider;
        _realtimeOptions = realtimeOptions;
        _protocolClient = protocolClient;
        _logger = logger;
    }

    public Task<Result<NexusReleaseManifest>> FetchLatestReleaseAsync(
        UpdateConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validation = Nexus.Contracts.Common.ContractValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            var err = validation.Errors[0];
            return Task.FromResult(Result<NexusReleaseManifest>.Failure(NexusError.Validation(err.Message, code: err.Code)));
        }

        return configuration.Visibility == UpdateRepositoryVisibility.PrivateServerRelay
            ? FetchFromServerRelayAsync(configuration, cancellationToken)
            : FetchPublicGitHubReleaseAsync(configuration, cancellationToken);
    }

    public Task<Result> DownloadReleaseApkAsync(
        UpdateConfiguration configuration,
        NexusReleaseManifest manifest,
        string destinationPartPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(manifest);

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            return Task.FromResult(Result.Failure(NexusError.Validation("URL de download da release é inválida.", code: "UPDATE_INVALID_URL")));
        }

        if (!string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Result.Failure(NexusError.Unauthorized(
                "Bloqueado: o download de atualizações exige transporte HTTPS.",
                code: "UPDATE_INSECURE_TRANSPORT")));
        }

        return configuration.Visibility == UpdateRepositoryVisibility.PrivateServerRelay || manifest.ServedThroughServerRelay
            ? DownloadViaServerRelayAsync(downloadUri, manifest, destinationPartPath, progress, cancellationToken)
            : DownloadFromTrustedPublicOriginAsync(configuration, downloadUri, manifest, destinationPartPath, progress, cancellationToken);
    }

    private async Task<Result<NexusReleaseManifest>> FetchPublicGitHubReleaseAsync(
        UpdateConfiguration config,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePublicApiUri(config.GitHubApiBaseUrl, out var apiBaseUri, out var uriError))
        {
            return Result<NexusReleaseManifest>.Failure(uriError!);
        }

        var owner = Uri.EscapeDataString(config.RepositoryOwner.Trim());
        var repo = Uri.EscapeDataString(config.RepositoryName.Trim());
        var latestUri = new Uri(apiBaseUri!, config.IncludePrereleases
            ? $"/repos/{owner}/{repo}/releases?per_page=10"
            : $"/repos/{owner}/{repo}/releases/latest");

        using var http = CreatePublicHttpClient();

        try
        {
            using var resp = await SendPublicGetAsync(http, latestUri, acceptJson: true, cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                    "Repositório privado ou acesso negado sem autenticação. Nunca incorpore PAT no APK; use o modo PrivateServerRelay via Nexus.Server.",
                    code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // On public GitHub repositories, /releases/latest returns 404 when no non-prerelease exists,
                // whereas /releases returns 200 [] when the public repo exists without releases vs 404 when private/missing.
                if (!config.IncludePrereleases)
                {
                    var probeListUri = new Uri(apiBaseUri!, $"/repos/{owner}/{repo}/releases?per_page=1");
                    using var probeResp = await SendPublicGetAsync(http, probeListUri, acceptJson: true, cancellationToken).ConfigureAwait(false);
                    if (probeResp.IsSuccessStatusCode)
                    {
                        return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                            "Nenhuma release publicada encontrada neste repositório.",
                            code: "UPDATE_RELEASE_NOT_FOUND"));
                    }

                    if (probeResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                            "Repositório privado exige relay autenticado no Nexus.Server.",
                            code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
                    }
                }

                return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                    "Nenhuma release encontrada (repositório sem releases ou privado sem relay autenticado).",
                    code: "UPDATE_RELEASE_NOT_FOUND"));
            }

            if ((int)resp.StatusCode >= 500)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                    $"Serviço GitHub Releases indisponível (HTTP {(int)resp.StatusCode}).",
                    code: "UPDATE_REPOSITORY_UNAVAILABLE"));
            }

            if (!resp.IsSuccessStatusCode)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                    $"Falha ao consultar GitHub Releases: HTTP {(int)resp.StatusCode}.",
                    code: "UPDATE_REPOSITORY_UNAVAILABLE"));
            }

            var rawJson = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            GitHubReleaseDto? selectedRelease = null;

            if (config.IncludePrereleases)
            {
                var list = JsonSerializer.Deserialize<List<GitHubReleaseDto>>(rawJson, JsonOptions);
                selectedRelease = list?
                    .Where(r => !r.Draft)
                    .OrderByDescending(r => NexusSemanticVersion.TryParse(r.TagName, out var v) ? v : null)
                    .FirstOrDefault();
            }
            else
            {
                selectedRelease = JsonSerializer.Deserialize<GitHubReleaseDto>(rawJson, JsonOptions);
            }

            if (selectedRelease is null || selectedRelease.Draft)
            {
                return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                    "Nenhuma release pública válida encontrada.",
                    code: "UPDATE_RELEASE_NOT_FOUND"));
            }

            return await BuildManifestFromGitHubDtoAsync(http, selectedRelease, config, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateRedirectSecurityException ex)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(ex.Message, code: ex.Code));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GitHub release check network failure for {Owner}/{Repo}", config.RepositoryOwner, config.RepositoryName);
            return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                $"Repositório indisponível: {ex.Message}",
                code: "UPDATE_REPOSITORY_UNAVAILABLE"));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                "Tempo esgotado ao consultar GitHub Releases.",
                code: "UPDATE_REPOSITORY_UNAVAILABLE"));
        }
    }

    private async Task<Result<NexusReleaseManifest>> BuildManifestFromGitHubDtoAsync(
        HttpClient http,
        GitHubReleaseDto dto,
        UpdateConfiguration config,
        CancellationToken cancellationToken)
    {
        var apkAsset = dto.Assets?.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.Name) &&
            a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

        if (apkAsset is null || string.IsNullOrWhiteSpace(apkAsset.BrowserDownloadUrl))
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                $"A release '{dto.TagName}' não possui artefato .apk anexado.",
                code: "UPDATE_RELEASE_NOT_FOUND"));
        }

        if (!TryValidateTrustedDownloadUri(apkAsset.BrowserDownloadUrl, out var apkUri, out var originError))
        {
            return Result<NexusReleaseManifest>.Failure(originError!);
        }

        var manifestAsset = dto.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, "nexus-update-manifest.json", StringComparison.OrdinalIgnoreCase));

        if (manifestAsset is null || string.IsNullOrWhiteSpace(manifestAsset.BrowserDownloadUrl))
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Validation(
                $"A release '{dto.TagName}' não contém o manifesto assinado 'nexus-update-manifest.json' (SHA-256 + assinatura obrigatórios).",
                code: "UPDATE_MANIFEST_MISSING"));
        }

        if (!TryValidateTrustedDownloadUri(manifestAsset.BrowserDownloadUrl, out var manifestUri, out var manOriginErr))
        {
            return Result<NexusReleaseManifest>.Failure(manOriginErr!);
        }

        using var manResp = await SendFollowingSafeHttpsRedirectsAsync(http, manifestUri!, maxHops: MaxRedirects, isTrustedHost: IsTrustedGitHubHost, cancellationToken).ConfigureAwait(false);
        if (!manResp.IsSuccessStatusCode)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                $"Não foi possível baixar o manifesto da release (HTTP {(int)manResp.StatusCode}).",
                code: manResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "UPDATE_PRIVATE_AUTH_REQUIRED"
                    : "UPDATE_REPOSITORY_UNAVAILABLE"));
        }

        var manifestJson = await manResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<NexusReleaseManifest>(manifestJson, JsonOptions);
        if (manifest is null)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Validation(
                "Manifesto JSON da release inválido.",
                code: "UPDATE_MANIFEST_INVALID"));
        }

        var normalized = manifest with
        {
            TagName = string.IsNullOrWhiteSpace(manifest.TagName) ? dto.TagName : manifest.TagName,
            PublishedAtUtc = manifest.PublishedAtUtc == default ? dto.PublishedAt : manifest.PublishedAtUtc,
            ReleaseNotes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? (dto.Body ?? string.Empty) : manifest.ReleaseNotes,
            DownloadUrl = apkUri!.ToString(),
            ApkSizeBytes = manifest.ApkSizeBytes > 0 ? manifest.ApkSizeBytes : apkAsset.Size,
            ServedThroughServerRelay = false
        };

        var validation = Nexus.Contracts.Common.ContractValidator.Validate(normalized);
        if (!validation.IsValid)
        {
            var err = validation.Errors[0];
            return Result<NexusReleaseManifest>.Failure(NexusError.Validation(err.Message, code: err.Code));
        }

        return Result<NexusReleaseManifest>.Success(normalized);
    }

    private async Task<Result<NexusReleaseManifest>> FetchFromServerRelayAsync(
        UpdateConfiguration config,
        CancellationToken cancellationToken)
    {
        var credRes = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credRes.IsFailure)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                "Relay privado requer pareamento e sessão autenticada ativa com o Nexus.Server.",
                code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
        }

        var creds = credRes.Value;
        if (!Uri.TryCreate(creds.ServerEndpoint, UriKind.Absolute, out var serverUri) ||
            !string.Equals(serverUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                "Endpoint do servidor para relay de atualização deve utilizar HTTPS.",
                code: "UPDATE_INSECURE_TRANSPORT"));
        }

        var owner = Uri.EscapeDataString(config.RepositoryOwner.Trim());
        var repo = Uri.EscapeDataString(config.RepositoryName.Trim());
        var relative = $"api/v1/updates/latest?owner={owner}&repo={repo}&includePrereleases={config.IncludePrereleases.ToString().ToLowerInvariant()}";
        var targetUri = new Uri(serverUri, relative);

        using var http = CreatePinnedServerHttpClient(creds);
        try
        {
            var reqUri = targetUri;
            using var req = new HttpRequestMessage(HttpMethod.Get, reqUri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && _realtimeOptions is not null)
            {
                var refreshed = await _credentialProvider.RefreshCredentialsAsync(cancellationToken).ConfigureAwait(false);
                if (refreshed.IsSuccess)
                {
                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, targetUri);
                    retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.Value.AccessToken);
                    using var retryResp = await http.SendAsync(retryReq, cancellationToken).ConfigureAwait(false);
                    return await ParseRelayResponseAsync(retryResp, serverUri, cancellationToken).ConfigureAwait(false);
                }
            }

            return await ParseRelayResponseAsync(resp, serverUri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                $"Servidor de atualização inacessível: {ex.Message}",
                code: "UPDATE_REPOSITORY_UNAVAILABLE"));
        }
    }

    private async Task<Result<NexusReleaseManifest>> ParseRelayResponseAsync(
        HttpResponseMessage resp,
        Uri serverBaseUri,
        CancellationToken cancellationToken)
    {
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Unauthorized(
                ExtractErrorMessage(body, "Acesso ao repositório privado negado. Verifique o pareamento e o token do servidor."),
                code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.NotFound(
                ExtractErrorMessage(body, "Nenhuma release encontrada no repositório privado."),
                code: "UPDATE_RELEASE_NOT_FOUND"));
        }

        if ((int)resp.StatusCode >= 500 || !resp.IsSuccessStatusCode)
        {
            var errCode = ExtractErrorCode(body) ?? "UPDATE_REPOSITORY_UNAVAILABLE";
            return Result<NexusReleaseManifest>.Failure(NexusError.Network(
                ExtractErrorMessage(body, $"Falha no relay Nexus.Server (HTTP {(int)resp.StatusCode})."),
                code: errCode));
        }

        var manifest = JsonSerializer.Deserialize<NexusReleaseManifest>(body, JsonOptions);
        if (manifest is null)
        {
            return Result<NexusReleaseManifest>.Failure(NexusError.Validation("Resposta inválida do relay de atualização.", code: "UPDATE_MANIFEST_INVALID"));
        }

        var downloadUri = new Uri(serverBaseUri, $"api/v1/updates/releases/{Uri.EscapeDataString(manifest.TagName)}/download");
        return Result<NexusReleaseManifest>.Success(manifest with
        {
            DownloadUrl = downloadUri.ToString(),
            ServedThroughServerRelay = true
        });
    }

    private async Task<Result> DownloadFromTrustedPublicOriginAsync(
        UpdateConfiguration config,
        Uri downloadUri,
        NexusReleaseManifest manifest,
        string destinationPartPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryValidateTrustedDownloadUri(downloadUri.ToString(), out var validatedUri, out var originErr))
        {
            return Result.Failure(originErr!);
        }

        using var http = CreatePublicHttpClient();
        return await StreamDownloadToPartFileAsync(
            http,
            validatedUri!,
            manifest,
            destinationPartPath,
            isTrustedHost: IsTrustedGitHubHost,
            bearerToken: null,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> DownloadViaServerRelayAsync(
        Uri downloadUri,
        NexusReleaseManifest manifest,
        string destinationPartPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var credRes = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credRes.IsFailure)
        {
            return Result.Failure(NexusError.Unauthorized(
                "Autenticação com Nexus.Server necessária para baixar release privada.",
                code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
        }

        var creds = credRes.Value;
        if (!Uri.TryCreate(creds.ServerEndpoint, UriKind.Absolute, out var expectedServerUri) ||
            !string.Equals(expectedServerUri.Host, downloadUri.Host, StringComparison.OrdinalIgnoreCase) ||
            expectedServerUri.Port != downloadUri.Port)
        {
            return Result.Failure(NexusError.Unauthorized(
                $"Origem de download não corresponde ao servidor pareado ({downloadUri.Host}).",
                code: "UPDATE_UNTRUSTED_ORIGIN"));
        }

        using var http = CreatePinnedServerHttpClient(creds);
        return await StreamDownloadToPartFileAsync(
            http,
            downloadUri,
            manifest,
            destinationPartPath,
            isTrustedHost: host => string.Equals(host, expectedServerUri.Host, StringComparison.OrdinalIgnoreCase),
            bearerToken: creds.AccessToken,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> StreamDownloadToPartFileAsync(
        HttpClient http,
        Uri targetUri,
        NexusReleaseManifest manifest,
        string destinationPartPath,
        Func<string, bool> isTrustedHost,
        string? bearerToken,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await SendFollowingSafeHttpsRedirectsAsync(
                http,
                targetUri,
                MaxRedirects,
                isTrustedHost,
                cancellationToken,
                bearerToken,
                acceptJson: false).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result.Failure(NexusError.Unauthorized(
                    "Download recusado por falta de autorização.",
                    code: "UPDATE_PRIVATE_AUTH_REQUIRED"));
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Failure(NexusError.NotFound(
                    "Artefato APK não encontrado na release.",
                    code: "UPDATE_RELEASE_NOT_FOUND"));
            }

            if (!resp.IsSuccessStatusCode)
            {
                return Result.Failure(NexusError.Network(
                    $"Erro ao baixar APK (HTTP {(int)resp.StatusCode}).",
                    code: "UPDATE_REPOSITORY_UNAVAILABLE"));
            }

            var dir = Path.GetDirectoryName(destinationPartPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var expectedBytes = resp.Content.Headers.ContentLength ?? manifest.ApkSizeBytes;
            if (manifest.ApkSizeBytes > 0 && expectedBytes > 0 && expectedBytes != manifest.ApkSizeBytes)
            {
                return Result.Failure(NexusError.Validation(
                    "Tamanho declarado no servidor diverge do manifesto da release.",
                    code: "UPDATE_CHECKSUM_MISMATCH"));
            }

            var totalExpected = manifest.ApkSizeBytes > 0 ? manifest.ApkSizeBytes : expectedBytes;
            long totalRead = 0;

            await using var networkStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var fileStream = new FileStream(destinationPartPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await networkStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    totalRead += read;

                    progress?.Report(new UpdateDownloadProgress
                    {
                        TagName = manifest.TagName,
                        Version = manifest.Version,
                        Stage = UpdateStatusKind.Downloading,
                        BytesDownloaded = totalRead,
                        TotalBytes = totalExpected,
                        StatusMessage = $"Baixando {manifest.ApkFileName}: {totalRead / 1024.0:F0} KB"
                    });
                }

                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (totalExpected > 0 && totalRead < totalExpected)
            {
                return Result.Failure(NexusError.Network(
                    $"Download interrompido em {totalRead} de {totalExpected} bytes.",
                    code: "UPDATE_DOWNLOAD_INTERRUPTED"));
            }

            return Result.Success();
        }
        catch (UpdateRedirectSecurityException ex)
        {
            return Result.Failure(NexusError.Unauthorized(ex.Message, code: ex.Code));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(NexusError.Cancelled("Download da atualização cancelado pelo usuário."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or EndOfStreamException)
        {
            return Result.Failure(NexusError.Network(
                $"Download interrompido: {ex.Message}",
                code: "UPDATE_DOWNLOAD_INTERRUPTED"));
        }
    }

    private async Task<HttpResponseMessage> SendPublicGetAsync(
        HttpClient http,
        Uri uri,
        bool acceptJson,
        CancellationToken cancellationToken)
        => await SendFollowingSafeHttpsRedirectsAsync(
            http,
            uri,
            MaxRedirects,
            host => IsTrustedGitHubHost(host) || string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase),
            cancellationToken,
            bearerToken: null,
            acceptJson: acceptJson).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendFollowingSafeHttpsRedirectsAsync(
        HttpClient http,
        Uri initialUri,
        int maxHops,
        Func<string, bool> isTrustedHost,
        CancellationToken cancellationToken,
        string? bearerToken = null,
        bool acceptJson = false)
    {
        var currentUri = initialUri;
        for (var hop = 0; hop <= maxHops; hop++)
        {
            if (!string.Equals(currentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("HTTPS_REQUIRED");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("NEXUS-Updater", "1.0"));
            if (acceptJson)
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            }

            // Never leak NexusBearer tokens across redirects to third-party hosts
            if (!string.IsNullOrWhiteSpace(bearerToken) && string.Equals(currentUri.Host, initialUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
            {
                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                resp.Dispose();

                if (!string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateRedirectSecurityException("UPDATE_INSECURE_TRANSPORT", $"Redirecionamento inseguro para HTTP rejeitado ({nextUri}).");
                }

                if (!isTrustedHost(nextUri.Host))
                {
                    throw new UpdateRedirectSecurityException("UPDATE_UNTRUSTED_ORIGIN", $"Redirecionamento para origem não confiável bloqueado: '{nextUri.Host}'.");
                }

                currentUri = nextUri;
                continue;
            }

            return resp;
        }

        throw new HttpRequestException("Número máximo de redirecionamentos excedido ao consultar atualização.");
    }

    private sealed class UpdateRedirectSecurityException(string code, string message) : HttpRequestException(message)
    {
        public string Code { get; } = code;
    }

    private bool TryValidatePublicApiUri(string rawUrl, out Uri? uri, out NexusError? error)
    {
        uri = null;
        error = null;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out uri))
        {
            error = NexusError.Validation("URL da API GitHub inválida.", code: "UPDATE_INVALID_URL");
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = NexusError.Unauthorized("A URL da API GitHub deve usar HTTPS obrigatoriamente.", code: "UPDATE_INSECURE_TRANSPORT");
            return false;
        }

        if (!IsTrustedGitHubHost(uri.Host))
        {
            error = NexusError.Unauthorized($"Host da API de atualização não permitido na lista confiável: '{uri.Host}'.", code: "UPDATE_UNTRUSTED_ORIGIN");
            return false;
        }

        return true;
    }

    private bool TryValidateTrustedDownloadUri(string rawUrl, out Uri? uri, out NexusError? error)
    {
        uri = null;
        error = null;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out uri))
        {
            error = NexusError.Validation("URL do ativo APK inválida.", code: "UPDATE_INVALID_URL");
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = NexusError.Unauthorized("Download de APK bloqueado: exige HTTPS.", code: "UPDATE_INSECURE_TRANSPORT");
            return false;
        }

        if (!IsTrustedGitHubHost(uri.Host))
        {
            error = NexusError.Unauthorized($"Origem de download não confiável rejeitada: '{uri.Host}'.", code: "UPDATE_UNTRUSTED_ORIGIN");
            return false;
        }

        return true;
    }

    private bool IsTrustedGitHubHost(string host)
    {
        if (_updateOptions.TrustedReleaseHosts.Contains(host))
        {
            return true;
        }

        return host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
               !host.Contains("user-images", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly SocketsHttpHandler SharedPublicGitHubHandler = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        }
    };

    private HttpClient CreatePublicHttpClient()
    {
        if (_updateOptions.PublicGitHubHandlerFactory is not null)
        {
            return new HttpClient(_updateOptions.PublicGitHubHandlerFactory(), disposeHandler: true)
            {
                Timeout = _updateOptions.HttpTimeout
            };
        }

        return new HttpClient(SharedPublicGitHubHandler, disposeHandler: false)
        {
            Timeout = _updateOptions.HttpTimeout
        };
    }

    private HttpClient CreatePinnedServerHttpClient(RealtimeCredentialBundle creds)
        => _protocolClient.CreateHttpClient(creds.ServerEndpoint, creds.TlsThumbprint, _updateOptions.HttpTimeout, _realtimeOptions.SignalRMessageHandlerFactory);

    private static string ExtractErrorMessage(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString() ?? fallback;
            }
        }
        catch { }

        return fallback;
    }

    private static string? ExtractErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                return c.GetString();
            }
        }
        catch { }

        return null;
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

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

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
