using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Updates;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Settings;
using Nexus.Infrastructure.Updates;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Nexus.Security.Updates;
using Nexus.Server.Options;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class UpdateRelayIntegrationTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-p10-int-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.Zero));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task PrivateRelay_AuthenticatedPairedAndroid_DownloadsAndVerifiesApkWithoutClientKnowingServerPat()
    {
        const string serverPat = "ghp_super_secret_server_only_pat_999";
        using var signingCert = CreateSigningCert("CN=NEXUS Production Release");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(signingCert.RawData));
        var signedApk = CreateSignedApk("com.nexus.ecosystem", 10300, signingCert);
        var apkSha = Convert.ToHexStringLower(SHA256.HashData(signedApk));

        var sawBearerOnGitHubApi = false;
        var leakedBearerToS3Redirect = false;

        var outboundHandler = new DelegatingStubHandler(req =>
        {
            var uri = req.RequestUri!;
            if (uri.Host == "api.github.com")
            {
                if (req.Headers.Authorization?.Scheme == "Bearer" && req.Headers.Authorization.Parameter == serverPat)
                {
                    sawBearerOnGitHubApi = true;
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
                {
                    var releaseDto = new
                    {
                        tag_name = "v1.3.0",
                        name = "NEXUS v1.3.0 Private",
                        body = "Private release distributed safely via Nexus.Server relay",
                        draft = false,
                        prerelease = false,
                        published_at = _clock.UtcNow,
                        assets = new[]
                        {
                            new
                            {
                                name = "com.nexus.ecosystem-1.3.0.apk",
                                size = (long)signedApk.Length,
                                browser_download_url = "https://github.com/nexus-org/nexus-private/releases/download/v1.3.0/com.nexus.ecosystem-1.3.0.apk",
                                url = "https://api.github.com/repos/nexus-org/nexus-private/releases/assets/1001"
                            },
                            new
                            {
                                name = "nexus-update-manifest.json",
                                size = 512L,
                                browser_download_url = "https://github.com/nexus-org/nexus-private/releases/download/v1.3.0/nexus-update-manifest.json",
                                url = "https://api.github.com/repos/nexus-org/nexus-private/releases/assets/1002"
                            }
                        }
                    };

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(releaseDto, JsonOptions), Encoding.UTF8, "application/json")
                    };
                }

                if (uri.AbsolutePath.EndsWith("/releases/assets/1002", StringComparison.Ordinal))
                {
                    var manifest = new NexusReleaseManifest
                    {
                        Version = "1.3.0",
                        AndroidVersionCode = 10300,
                        PackageId = "com.nexus.ecosystem",
                        TagName = "v1.3.0",
                        ApkFileName = "com.nexus.ecosystem-1.3.0.apk",
                        ApkSha256 = apkSha,
                        SignerCertificateSha256 = signerSha,
                        ApkSizeBytes = signedApk.Length,
                        PublishedAtUtc = _clock.UtcNow,
                        MinimumCompatibleServerVersion = "1.0.0",
                        ReleaseNotes = "Private relay test",
                        DownloadUrl = "https://github.com/nexus-org/nexus-private/releases/download/v1.3.0/com.nexus.ecosystem-1.3.0.apk"
                    };

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, "application/json")
                    };
                }

                if (uri.AbsolutePath.EndsWith("/releases/assets/1001", StringComparison.Ordinal))
                {
                    // Simulate real GitHub private asset 302 redirect to short-lived S3 pre-signed URL
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri("https://objects.githubusercontent.com/private-presigned/com.nexus.ecosystem-1.3.0.apk?sig=ok");
                    return redirect;
                }
            }

            if (uri.Host == "objects.githubusercontent.com")
            {
                if (req.Headers.Authorization is not null)
                {
                    leakedBearerToS3Redirect = true;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(signedApk)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var factory = new UpdateWebFactory(
            _storageDir,
            _clock,
            new GitHubUpdateRelayOptions
            {
                RepositoryOwner = "nexus-org",
                RepositoryName = "nexus-private",
                ExpectedPackageId = "com.nexus.ecosystem",
                ExpectedSignerSha256 = signerSha,
                ServerTokenResolver = () => serverPat,
                OutboundGitHubHandlerFactory = () => outboundHandler
            });

        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var host = CreateCryptoProvider("srv-upd", "NEXUS Server Host", DevicePlatform.Windows);
        var androidCreds = await PairAndroidNodeAsync(http, host, "and-upd", "Pixel 9", NexusPermission.All);

        // Status endpoint reports relay readiness WITHOUT leaking the GitHub PAT
        using var statusReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/updates/status");
        statusReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", androidCreds.AccessToken);
        var statusResp = await http.SendAsync(statusReq);
        statusResp.EnsureSuccessStatusCode();
        var rawStatusJson = await statusResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(serverPat, rawStatusJson, StringComparison.Ordinal);

        var statusObj = JsonSerializer.Deserialize<UpdateRelayStatusContract>(rawStatusJson, JsonOptions)!;
        Assert.True(statusObj.IsConfigured);
        Assert.True(statusObj.HasServerTokenConfigured);
        Assert.Equal("nexus-org/nexus-private", statusObj.RepositorySlug);

        // Run client NexusUpdateService end-to-end in PrivateServerRelay mode (client holds ZERO GitHub token)
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            IsFirstInstall = false,
            SignerCertificateSha256 = signerSha,
            SupportsNativePackageInstaller = true,
            CanRequestPackageInstalls = true
        };

        var updateService = await BuildClientUpdateServiceAsync(factory, androidCreds, installed);
        await updateService.SaveConfigurationAsync(new UpdateConfiguration
        {
            RepositoryOwner = "nexus-org",
            RepositoryName = "nexus-private",
            Visibility = UpdateRepositoryVisibility.PrivateServerRelay,
            ExpectedSignerSha256 = signerSha
        });

        var check = await updateService.CheckForUpdatesAsync();
        Assert.True(check.IsSuccess, check.Error?.Message);
        Assert.True(check.Value.IsUpdateAvailable);
        Assert.True(check.Value.LatestRelease!.ServedThroughServerRelay);
        Assert.StartsWith("https://localhost/api/v1/updates/releases/", check.Value.LatestRelease.DownloadUrl, StringComparison.OrdinalIgnoreCase);

        var verify = await updateService.DownloadAndVerifyUpdateAsync(check.Value.LatestRelease);
        Assert.True(verify.IsSuccess, verify.Error?.Message);
        Assert.True(verify.Value.IsReadyForUserInstall);
        Assert.Equal("com.nexus.ecosystem", verify.Value.ActualPackageId);
        Assert.Equal(signerSha, verify.Value.ActualSignerSha256);

        Assert.True(sawBearerOnGitHubApi);
        Assert.False(leakedBearerToS3Redirect);
    }

    [Fact]
    public async Task PrivateRelay_UnauthenticatedOrUnauthorizedDevice_RejectedWith401Or403()
    {
        using var factory = new UpdateWebFactory(
            _storageDir,
            _clock,
            new GitHubUpdateRelayOptions
            {
                RepositoryOwner = "nexus-org",
                RepositoryName = "nexus-private",
                ServerTokenResolver = () => "ghp_valid_token"
            });

        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var anonStatus = await http.GetAsync("/api/v1/updates/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonStatus.StatusCode);

        var anonLatest = await http.GetAsync("/api/v1/updates/latest");
        Assert.Equal(HttpStatusCode.Unauthorized, anonLatest.StatusCode);

        var host = CreateCryptoProvider("srv-auth", "NEXUS Host", DevicePlatform.Windows);
        var limitedCreds = await PairAndroidNodeAsync(http, host, "and-limited", "ClipboardOnly", NexusPermission.Clipboard);

        using var forbiddenReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/updates/latest");
        forbiddenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", limitedCreds.AccessToken);
        var forbiddenResp = await http.SendAsync(forbiddenReq);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);
    }

    [Fact]
    public async Task PrivateRelay_MissingServerTokenAndNonExistentRelease_ReturnAccurateStatusAndBuiltInModule()
    {
        // Case A: private repo returns 404 when server has no token configured -> 401 UPDATE_PRIVATE_AUTH_REQUIRED
        var noTokenHandler = new DelegatingStubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using (var factoryNoToken = new UpdateWebFactory(
            Path.Combine(_storageDir, "no-token"),
            _clock,
            new GitHubUpdateRelayOptions
            {
                RepositoryOwner = "nexus-org",
                RepositoryName = "nexus-private",
                ServerTokenResolver = () => null,
                OutboundGitHubHandlerFactory = () => noTokenHandler
            }))
        {
            using var http = factoryNoToken.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var host = CreateCryptoProvider("srv-notok", "NEXUS Host", DevicePlatform.Windows);
            var creds = await PairAndroidNodeAsync(http, host, "and-notok", "Android", NexusPermission.All);

            using var sysReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
            var sysResp = await http.SendAsync(sysReq);
            sysResp.EnsureSuccessStatusCode();
            var sysJson = await sysResp.Content.ReadAsStringAsync();
            Assert.Contains("\"nexus.update\"", sysJson, StringComparison.Ordinal);

            using var latestReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/updates/latest");
            latestReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            var latestResp = await http.SendAsync(latestReq);
            Assert.Equal(HttpStatusCode.Unauthorized, latestResp.StatusCode);
            var body = await latestResp.Content.ReadAsStringAsync();
            Assert.Contains("UPDATE_PRIVATE_AUTH_REQUIRED", body, StringComparison.Ordinal);
        }

        // Case B: valid token present on server, repository exists, but zero releases published -> 404 UPDATE_RELEASE_NOT_FOUND
        var emptyReleasesHandler = new DelegatingStubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using (var factoryEmptyRepo = new UpdateWebFactory(
            Path.Combine(_storageDir, "empty-rel"),
            _clock,
            new GitHubUpdateRelayOptions
            {
                RepositoryOwner = "nexus-org",
                RepositoryName = "nexus-private",
                ServerTokenResolver = () => "ghp_configured_token",
                OutboundGitHubHandlerFactory = () => emptyReleasesHandler
            }))
        {
            using var http = factoryEmptyRepo.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var host = CreateCryptoProvider("srv-empty", "NEXUS Host", DevicePlatform.Windows);
            var creds = await PairAndroidNodeAsync(http, host, "and-empty", "Android", NexusPermission.All);

            using var latestReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/updates/latest");
            latestReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            var latestResp = await http.SendAsync(latestReq);
            Assert.Equal(HttpStatusCode.NotFound, latestResp.StatusCode);
            var body = await latestResp.Content.ReadAsStringAsync();
            Assert.Contains("UPDATE_RELEASE_NOT_FOUND", body, StringComparison.Ordinal);
        }
    }

    private async Task<NexusUpdateService> BuildClientUpdateServiceAsync(
        UpdateWebFactory factory,
        RealtimeCredentialBundle creds,
        InstalledAppVersionInfo installed)
    {
        var clientDir = Path.Combine(_storageDir, "client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(clientDir);

        var storageOpts = new NexusStorageOptions
        {
            RootDirectory = clientDir,
            DatabaseFileName = "nexus-client-upd.db",
            EnablePooling = false
        };
        var connFactory = new SqliteConnectionFactory(storageOpts);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState());
        await new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance).InitializeAsync();

        var settingsStore = new SqliteSettingsStore(connFactory, failureHandler, new SystemTextJsonSerializer(), _clock);
        var historyRepo = new SqliteUpdateHistoryRepository(connFactory, failureHandler);

        var crypto = CreateCryptoProvider("client-proto-" + Guid.NewGuid().ToString("N"), "AndroidClient", DevicePlatform.Android);
        var protocol = new NexusPairingProtocolClient(crypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var rtOptions = new RealtimeClientOptions
        {
            SignalRMessageHandlerFactory = (_, _) => factory.Server.CreateHandler()
        };

        var updateOpts = new NexusUpdateOptions();
        var releaseClient = new GitHubReleaseClient(
            updateOpts,
            new StaticCredentialProvider(creds),
            rtOptions,
            protocol,
            NullLogger<GitHubReleaseClient>.Instance);

        return new NexusUpdateService(
            settingsStore,
            updateOpts,
            storageOpts,
            new StaticAppInspector(installed),
            releaseClient,
            new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock),
            new NoOpInstaller(),
            historyRepo,
            new InProcessEventBus(),
            _clock,
            NullLogger<NexusUpdateService>.Instance);
    }

    private async Task<RealtimeCredentialBundle> PairAndroidNodeAsync(
        HttpClient http,
        CryptographicIdentityProvider hostCrypto,
        string folderName,
        string deviceName,
        NexusPermission permissions)
    {
        var dir = Path.Combine(_storageDir, "nodes", folderName);
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(dir, "vault")
        });
        var localCtx = new DefaultLocalDeviceContext(
            Guid.Empty, deviceName, DevicePlatform.Android, DeviceType.Phone, "Android 15", "1.0.0", DeviceCapability.All);
        var serializer = new SystemTextJsonSerializer();
        var clientCrypto = new CryptographicIdentityProvider(vault, localCtx, serializer, _clock);
        var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), serializer, _clock);
        var clientProtocol = new NexusPairingProtocolClient(clientCrypto, new SecureTokenGenerator(), serializer, _clock);

        const string endpoint = "https://localhost";
        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync(endpoint, null, 120)).Value;
        var createResp = await http.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions);
        createResp.EnsureSuccessStatusCode();
        var createSnap = (await createResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;

        var qr = clientProtocol.ParseAndVerifyQrPayload(createSnap.QrPayloadJson!).Value;
        var claim = (await clientProtocol.BuildClaimRequestAsync(qr, permissions)).Value;
        (await http.PostAsJsonAsync("/api/v1/pairing/claim", claim.Request, JsonOptions)).EnsureSuccessStatusCode();

        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: true, permissions)).Value;
        (await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions)).EnsureSuccessStatusCode();

        var finalizeReq = (await clientProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeReq, JsonOptions);
        finalizeResp.EnsureSuccessStatusCode();
        var tokens = (await finalizeResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;
        var identity = (await clientCrypto.GetOrCreateIdentityAsync()).Value;

        return new RealtimeCredentialBundle(
            identity.DeviceId,
            identity.CustomName,
            tokens.AccessToken,
            tokens.AccessTokenExpiresAtUtc,
            endpoint,
            null);
    }

    private CryptographicIdentityProvider CreateCryptoProvider(string folderName, string deviceName, DevicePlatform platform)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(
            Guid.Empty, deviceName, platform, DeviceType.Desktop, "Windows 11", "1.0.0", DeviceCapability.All);
        return new CryptographicIdentityProvider(vault, ctx, new SystemTextJsonSerializer(), _clock);
    }

    private static X509Certificate2 CreateSigningCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    private static byte[] CreateSignedApk(string packageId, long versionCode, X509Certificate2 cert)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry("AndroidManifest.xml");
            using (var w = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                w.Write($"""<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{packageId}" android:versionCode="{versionCode}" />""");
            }

            var dexEntry = zip.CreateEntry("classes.dex");
            using var dexStream = dexEntry.Open();
            dexStream.Write("dex\n039\0relay"u8);
        }

        return ApkSigningBlockCodec.AttachApkV2SigningBlock(ms.ToArray(), cert);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StaticCredentialProvider(RealtimeCredentialBundle bundle) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(bundle));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("No refresh")));
    }

    private sealed class StaticAppInspector(InstalledAppVersionInfo info) : IAppPackageInspector
    {
        public Task<InstalledAppVersionInfo> GetInstalledAppInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(info);
    }

    private sealed class NoOpInstaller : IAndroidUpdateInstaller
    {
        public Task<bool> CanRequestPackageInstallsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Result> OpenUnknownSourcesSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<UpdateInstallLaunchResult>> LaunchSystemPackageInstallerAsync(
            VerifiedUpdatePackage verifiedPackage,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<UpdateInstallLaunchResult>.Success(new UpdateInstallLaunchResult
            {
                Launched = true,
                LocalApkPath = verifiedPackage.LocalApkPath,
                Message = "OK"
            }));
    }

    private sealed class UpdateWebFactory(
        string storageDir,
        MutableClock clock,
        GitHubUpdateRelayOptions relayOptions) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-server-p10.db",
                    ["Nexus:Storage:EnablePooling"] = "false",
                    ["Nexus:Tls:BindKestrelHttps"] = "false",
                    ["Nexus:Tls:RequireHttps"] = "false",
                    ["Nexus:Security:EnableLocalHostBridge"] = "false",
                    ["Nexus:Discovery:EnableMdns"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
                services.RemoveAll<GitHubUpdateRelayOptions>();
                services.AddSingleton(relayOptions);
            });
        }
    }
}

