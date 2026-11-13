using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Security;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Updates;
using Nexus.Desktop.Services.Settings;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Settings;
using Nexus.Infrastructure.Updates;
using Nexus.Mobile.Services.ViewModels;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Nexus.Security.Updates;
using Xunit;

namespace Nexus.UnitTests;

public sealed class NexusUpdateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-upd-unit-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 11, 21, 0, 0, TimeSpan.Zero));
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public NexusUpdateTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void SemanticVersion_ParsesAndOrdersCoreAndPrereleaseCorrectly()
    {
        Assert.True(NexusSemanticVersion.TryParse("v1.2.0-alpha.1+build.9", out var alpha));
        Assert.True(NexusSemanticVersion.TryParse("1.2.0-beta.2", out var beta));
        Assert.True(NexusSemanticVersion.TryParse("v1.2.0-rc.1", out var rc1));
        Assert.True(NexusSemanticVersion.TryParse("1.2.0", out var stable));
        Assert.True(NexusSemanticVersion.TryParse("1.2.1", out var patch));

        Assert.True(alpha!.CompareTo(beta) < 0);
        Assert.True(beta!.CompareTo(rc1) < 0);
        Assert.True(rc1!.CompareTo(stable) < 0);
        Assert.True(stable!.CompareTo(patch) < 0);
        Assert.False(NexusSemanticVersion.TryParse("1.02.0", out _));
        Assert.False(NexusSemanticVersion.TryParse("not-semver", out _));
    }

    [Fact]
    public async Task Scenario1_FirstInstall_DetectsAvailableReleaseAndVerifiesSignedApk()
    {
        using var cert = CreateSigningCert("CN=NEXUS Release");
        var signerSha = CertSha256(cert);
        var apk = CreateSignedApk("com.nexus.ecosystem", 10000, cert);
        var handler = CreatePublicReleaseHandler("v1.0.0", "1.0.0", 10000, signerSha, apk);

        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            IsFirstInstall = true,
            SignerCertificateSha256 = null,
            SupportsNativePackageInstaller = true,
            CanRequestPackageInstalls = true
        };

        await using var harness = await CreateHarnessAsync(installed, handler);
        await harness.Service.SaveConfigurationAsync(new UpdateConfiguration
        {
            RepositoryOwner = "nexus-ecosystem",
            RepositoryName = "nexus",
            ExpectedSignerSha256 = signerSha
        });

        var check = await harness.Service.CheckForUpdatesAsync();
        Assert.True(check.IsSuccess, check.Error?.Message);
        Assert.True(check.Value.IsFirstInstallAvailable);
        Assert.True(check.Value.IsUpdateAvailable);
        Assert.True(check.Value.IsCompatibleWithCurrentSigner);

        var verify = await harness.Service.DownloadAndVerifyUpdateAsync(check.Value.LatestRelease!);
        Assert.True(verify.IsSuccess, verify.Error?.Message);
        Assert.True(verify.Value.IsReadyForUserInstall);
        Assert.True(File.Exists(verify.Value.LocalApkPath));
    }

    [Fact]
    public async Task Scenario2_HigherVersionAvailable_DownloadsVerifiesProgressAndPersistsSqliteHistory()
    {
        using var cert = CreateSigningCert("CN=NEXUS Official Signing Key");
        var signerSha = CertSha256(cert);
        var apk = CreateSignedApk("com.nexus.ecosystem", 10200, cert);
        var handler = CreatePublicReleaseHandler("v1.2.0", "1.2.0", 10200, signerSha, apk);

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

        await using var harness = await CreateHarnessAsync(installed, handler);
        var stages = new List<UpdateStatusKind>();
        harness.Service.ProgressChanged += (_, p) => stages.Add(p.Stage);

        var check = await harness.Service.CheckForUpdatesAsync();
        Assert.True(check.IsSuccess, check.Error?.Message);
        Assert.True(check.Value.IsUpdateAvailable);
        Assert.Equal("1.2.0", check.Value.LatestRelease!.Version);

        var verify = await harness.Service.DownloadAndVerifyUpdateAsync(check.Value.LatestRelease);
        Assert.True(verify.IsSuccess, verify.Error?.Message);
        Assert.True(verify.Value.IsChecksumVerified);
        Assert.True(verify.Value.IsSignatureVerified);
        Assert.True(verify.Value.IsPackageIdentityVerified);

        // Must never silently auto-launch installer without confirmation
        Assert.Empty(harness.Installer.LaunchedPackages);

        var blockedSilent = await harness.Service.LaunchUserConfirmedInstallAsync(verify.Value, userConfirmed: false);
        Assert.True(blockedSilent.IsFailure);
        Assert.Equal("UPDATE_USER_CONFIRMATION_REQUIRED", blockedSilent.Error!.Code);
        Assert.Empty(harness.Installer.LaunchedPackages);

        var confirmed = await harness.Service.LaunchUserConfirmedInstallAsync(verify.Value, userConfirmed: true);
        Assert.True(confirmed.IsSuccess);
        Assert.True(confirmed.Value.Launched);
        Assert.Single(harness.Installer.LaunchedPackages);

        Assert.Contains(UpdateStatusKind.Checking, stages);
        Assert.Contains(UpdateStatusKind.UpdateAvailable, stages);
        Assert.Contains(UpdateStatusKind.Downloading, stages);
        Assert.Contains(UpdateStatusKind.Verifying, stages);
        Assert.Contains(UpdateStatusKind.ReadyToInstall, stages);
        Assert.Contains(UpdateStatusKind.InstallPromptLaunched, stages);

        var history = await harness.Service.GetHistoryAsync();
        Assert.True(history.IsSuccess);
        Assert.True(history.Value.Count >= 2);
        Assert.Contains(history.Value, h => h.OutcomeStatus == UpdateStatusKind.ReadyToInstall && h.DiscoveredVersion == "1.2.0");
    }

    [Fact]
    public async Task Scenario3_AlreadyUpToDate_ReportsUpToDateAndDoesNotTriggerInstall()
    {
        using var cert = CreateSigningCert("CN=NEXUS Release");
        var signerSha = CertSha256(cert);
        var apk = CreateSignedApk("com.nexus.ecosystem", 10200, cert);
        var handler = CreatePublicReleaseHandler("v1.2.0", "1.2.0", 10200, signerSha, apk);

        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.2.0",
            VersionCode = 10200,
            IsFirstInstall = false,
            SignerCertificateSha256 = signerSha
        };

        await using var harness = await CreateHarnessAsync(installed, handler);
        var check = await harness.Service.CheckForUpdatesAsync();

        Assert.True(check.IsSuccess);
        Assert.False(check.Value.IsUpdateAvailable);
        Assert.NotNull(check.Value.LatestRelease);
        Assert.Equal(UpdateFailureReason.None, check.Value.FailureReason);
    }

    [Fact]
    public async Task Scenario4_InterruptedDownload_FailsWithDownloadInterruptedAndDoesNotPromoteApk()
    {
        using var cert = CreateSigningCert("CN=NEXUS Release");
        var signerSha = CertSha256(cert);
        var fullApk = CreateSignedApk("com.nexus.ecosystem", 10200, cert);
        var partialApk = fullApk[..(fullApk.Length / 2)];
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(fullApk));

        var handler = CreatePublicReleaseHandler(
            "v1.2.0",
            "1.2.0",
            10200,
            signerSha,
            fullApk,
            overrideApkSha256: expectedSha,
            overrideDeclaredSize: fullApk.Length,
            overrideApkDownloadResponder: () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new TruncatedStreamContent(partialApk, fullApk.Length)
            });

        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            SignerCertificateSha256 = signerSha
        };

        await using var harness = await CreateHarnessAsync(installed, handler);
        var check = await harness.Service.CheckForUpdatesAsync();
        Assert.True(check.IsSuccess);

        var downloadRes = await harness.Service.DownloadAndVerifyUpdateAsync(check.Value.LatestRelease!);
        Assert.True(downloadRes.IsFailure);
        Assert.Equal("UPDATE_DOWNLOAD_INTERRUPTED", downloadRes.Error!.Code);

        var verifiedDir = Path.Combine(harness.StorageOptions.RootDirectory, harness.UpdateOptions.VerifiedFolderName);
        if (Directory.Exists(verifiedDir))
        {
            Assert.Empty(Directory.GetFiles(verifiedDir));
        }
    }

    [Fact]
    public async Task Scenario5_CorruptedApkDownload_DeletesStagingPartAndBlocksVerification()
    {
        using var cert = CreateSigningCert("CN=NEXUS Release");
        var signerSha = CertSha256(cert);
        var validApk = CreateSignedApk("com.nexus.ecosystem", 10200, cert);
        var corruptedBytes = (byte[])validApk.Clone();
        corruptedBytes[corruptedBytes.Length / 3] ^= 0x5A;
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(validApk));

        var handler = CreatePublicReleaseHandler(
            "v1.2.0",
            "1.2.0",
            10200,
            signerSha,
            validApk,
            overrideApkSha256: expectedSha,
            overrideDeclaredSize: validApk.Length,
            overrideApkDownloadResponder: () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(corruptedBytes)
            });

        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            SignerCertificateSha256 = signerSha
        };

        await using var harness = await CreateHarnessAsync(installed, handler);
        var check = await harness.Service.CheckForUpdatesAsync();
        Assert.True(check.IsSuccess);

        var downloadRes = await harness.Service.DownloadAndVerifyUpdateAsync(check.Value.LatestRelease!);
        Assert.True(downloadRes.IsFailure);
        Assert.Equal("UPDATE_CHECKSUM_MISMATCH", downloadRes.Error!.Code);

        var stagingDir = Path.Combine(harness.StorageOptions.RootDirectory, harness.UpdateOptions.StagingFolderName);
        if (Directory.Exists(stagingDir))
        {
            Assert.Empty(Directory.GetFiles(stagingDir));
        }
    }

    [Fact]
    public async Task Scenario6_DifferentSigningKey_BlockedAtManifestCheckAndApkVerification()
    {
        using var installedCert = CreateSigningCert("CN=NEXUS Official Key");
        using var rogueCert = CreateSigningCert("CN=Rogue Repackager Key");
        var installedSha = CertSha256(installedCert);
        var rogueSha = CertSha256(rogueCert);
        var rogueApk = CreateSignedApk("com.nexus.ecosystem", 10200, rogueCert);

        var handler = CreatePublicReleaseHandler("v1.2.0", "1.2.0", 10200, rogueSha, rogueApk);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            IsFirstInstall = false,
            SignerCertificateSha256 = installedSha
        };

        await using var harness = await CreateHarnessAsync(installed, handler);
        var check = await harness.Service.CheckForUpdatesAsync();

        Assert.True(check.IsFailure);
        Assert.Equal("UPDATE_SIGNATURE_MISMATCH", check.Error!.Code);
    }

    [Fact]
    public async Task Scenario7_RepositoryUnavailable_ReturnsNetworkErrorAndPersistsFailureHistory()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("GitHub DNS/network unreachable"));
        var installed = new InstalledAppVersionInfo { PackageId = "com.nexus.ecosystem", Version = "1.0.0", VersionCode = 10000 };

        await using var harness = await CreateHarnessAsync(installed, handler);
        var check = await harness.Service.CheckForUpdatesAsync();

        Assert.True(check.IsFailure);
        Assert.Equal("UPDATE_REPOSITORY_UNAVAILABLE", check.Error!.Code);

        var history = await harness.Service.GetHistoryAsync();
        Assert.Single(history.Value);
        Assert.Equal(UpdateFailureReason.RepositoryUnavailable, history.Value[0].FailureReason);
    }

    [Fact]
    public async Task Scenario8_ReleaseNotFound_ReturnsReleaseNotFoundAndLogsHistory()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
            {
                // Public repo exists, 0 releases published
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var installed = new InstalledAppVersionInfo { PackageId = "com.nexus.ecosystem", Version = "1.0.0", VersionCode = 10000 };
        await using var harness = await CreateHarnessAsync(installed, handler);

        var check = await harness.Service.CheckForUpdatesAsync();

        Assert.True(check.IsFailure);
        Assert.Equal("UPDATE_RELEASE_NOT_FOUND", check.Error!.Code);

        var history = await harness.Service.GetHistoryAsync();
        Assert.Single(history.Value);
        Assert.Equal(UpdateFailureReason.ReleaseNotFound, history.Value[0].FailureReason);
    }

    [Fact]
    public async Task Scenario9_PrivateRepositoryWithoutAuth_BlockedAndRejectsUntrustedOriginsAndPlainHttp()
    {
        // GitHub returns 401/404 for private repository queried anonymously in PublicDirect mode
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var installed = new InstalledAppVersionInfo { PackageId = "com.nexus.ecosystem", Version = "1.0.0", VersionCode = 10000 };
        await using var harness = await CreateHarnessAsync(installed, handler);

        var check = await harness.Service.CheckForUpdatesAsync();
        Assert.True(check.IsFailure);
        Assert.Equal("UPDATE_PRIVATE_AUTH_REQUIRED", check.Error!.Code);

        // Verify HTTPS + origin pinning rejects http:// and untrusted domains
        var httpCfgRes = await harness.Service.SaveConfigurationAsync(new UpdateConfiguration
        {
            RepositoryOwner = "nexus-ecosystem",
            RepositoryName = "nexus",
            GitHubApiBaseUrl = "http://api.github.com"
        });
        Assert.True(httpCfgRes.IsFailure);
        Assert.Equal("UPDATE_HTTPS_REQUIRED", httpCfgRes.Error!.Code);

        // Untrusted redirect domain during APK download is blocked
        using var cert = CreateSigningCert("CN=NEXUS Release");
        var signerSha = CertSha256(cert);
        var apk = CreateSignedApk("com.nexus.ecosystem", 10200, cert);
        var evilRedirectHandler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.Host == "github.com")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
                resp.Headers.Location = new Uri("https://evil-mirror.example.net/payload.apk");
                return resp;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using var evilHarness = await CreateHarnessAsync(installed, evilRedirectHandler);
        var evilManifest = new NexusReleaseManifest
        {
            Version = "1.2.0",
            AndroidVersionCode = 10200,
            PackageId = "com.nexus.ecosystem",
            TagName = "v1.2.0",
            ApkFileName = "nexus.apk",
            ApkSha256 = Convert.ToHexStringLower(SHA256.HashData(apk)),
            SignerCertificateSha256 = signerSha,
            ApkSizeBytes = apk.Length,
            PublishedAtUtc = _clock.UtcNow,
            MinimumCompatibleServerVersion = "1.0.0",
            DownloadUrl = "https://github.com/nexus-ecosystem/nexus/releases/download/v1.2.0/nexus.apk"
        };

        var dlEvil = await evilHarness.Service.DownloadAndVerifyUpdateAsync(evilManifest);
        Assert.True(dlEvil.IsFailure);
        Assert.Equal("UPDATE_UNTRUSTED_ORIGIN", dlEvil.Error!.Code);

        // Client UpdateConfiguration carries zero GitHub PAT properties
        var secretProps = typeof(UpdateConfiguration)
            .GetProperties()
            .Where(p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Pat", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(secretProps);
    }

    [Fact]
    public async Task MobileUpdateViewModel_RequiresUnknownSourcesAndExplicitConfirmation_AndDesktopSettingsViewModelIntegratesCleanly()
    {
        using var cert = CreateSigningCert("CN=NEXUS Release");
        var signerSha = CertSha256(cert);
        var apk = CreateSignedApk("com.nexus.ecosystem", 10200, cert);
        var handler = CreatePublicReleaseHandler("v1.2.0", "1.2.0", 10200, signerSha, apk);

        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            SignerCertificateSha256 = signerSha,
            SupportsNativePackageInstaller = true,
            CanRequestPackageInstalls = false // Android needs REQUEST_INSTALL_PACKAGES first
        };

        await using var harness = await CreateHarnessAsync(installed, handler);
        var mobileVm = new MobileUpdateViewModel(harness.Service);
        await mobileVm.InitializeCommand.ExecuteAsync();
        await mobileVm.CheckUpdatesCommand.ExecuteAsync();

        Assert.True(mobileVm.HasAvailableUpdate);
        Assert.Contains("1.2.0", mobileVm.AvailabilityTitle);

        await mobileVm.DownloadUpdateCommand.ExecuteAsync();
        Assert.True(mobileVm.HasVerifiedPackage);
        Assert.False(mobileVm.UserConfirmedInstall);

        // Clicking install without ticking confirmation keeps installer closed
        await mobileVm.InstallUpdateCommand.ExecuteAsync();
        Assert.Empty(harness.Installer.LaunchedPackages);

        // User confirms, but Android Unknown Sources permission isn't granted yet -> prompts permission settings
        mobileVm.UserConfirmedInstall = true;
        await mobileVm.InstallUpdateCommand.ExecuteAsync();
        Assert.True(mobileVm.RequiresPermissionPrompt);
        Assert.Empty(harness.Installer.LaunchedPackages);

        // Grant permission via settings action & retry -> now launches system installer
        await mobileVm.OpenUnknownSourcesCommand.ExecuteAsync();
        await mobileVm.InstallUpdateCommand.ExecuteAsync();
        Assert.False(mobileVm.RequiresPermissionPrompt);
        Assert.Single(harness.Installer.LaunchedPackages);

        // Desktop SettingsViewModel integrates cleanly with INexusUpdateService
        var desktopSettingsSvc = new DesktopSettingsService(harness.SettingsStore);
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_tempDir, "desktop-vault")
        });
        var devCtx = new DefaultLocalDeviceContext(
            Guid.NewGuid(), "WIN-DEV", DevicePlatform.Windows, DeviceType.Desktop, "Windows 11", "1.0.0", DeviceCapability.All);
        var crypto = new CryptographicIdentityProvider(vault, devCtx, new SystemTextJsonSerializer(), _clock);

        var desktopVm = new SettingsViewModel(desktopSettingsSvc, crypto, harness.Service);
        await desktopVm.LoadAsync();
        await desktopVm.CheckUpdatesCommand.ExecuteAsync();
        Assert.Contains("1.2.0", desktopVm.UpdateCheckStatusText);
    }

    private async Task<UpdateHarness> CreateHarnessAsync(
        InstalledAppVersionInfo installed,
        HttpMessageHandler publicHandler)
    {
        var dir = Path.Combine(_tempDir, "h-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var storageOpts = new NexusStorageOptions
        {
            RootDirectory = dir,
            DatabaseFileName = "nexus-updates.db",
            EnablePooling = false
        };

        var connFactory = new SqliteConnectionFactory(storageOpts);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState());
        var initializer = new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance);
        await initializer.InitializeAsync();

        var settingsStore = new SqliteSettingsStore(connFactory, failureHandler, new SystemTextJsonSerializer(), _clock);
        var historyRepo = new SqliteUpdateHistoryRepository(connFactory, failureHandler);

        var updateOpts = new NexusUpdateOptions
        {
            DefaultRepositoryOwner = "nexus-ecosystem",
            DefaultRepositoryName = "nexus",
            DefaultPackageId = "com.nexus.ecosystem",
            PublicGitHubHandlerFactory = () => publicHandler
        };

        var localCtx = new DefaultLocalDeviceContext(
            Guid.NewGuid(), "ANDROID-TEST", DevicePlatform.Android, DeviceType.Phone, "Android 15", installed.Version, DeviceCapability.All);
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(dir, "vault")
        });
        var serializer = new SystemTextJsonSerializer();
        var crypto = new CryptographicIdentityProvider(vault, localCtx, serializer, _clock);
        var protocolClient = new NexusPairingProtocolClient(crypto, new SecureTokenGenerator(), serializer, _clock);

        var rtOptions = new RealtimeClientOptions();
        var credProvider = new NoAuthCredentialProvider();
        var client = new GitHubReleaseClient(updateOpts, credProvider, rtOptions, protocolClient, NullLogger<GitHubReleaseClient>.Instance);
        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var pkgInspector = new MutableAppInspector(installed);
        var installer = new MockAndroidInstaller(pkgInspector);
        var eventBus = new InProcessEventBus();

        var svc = new NexusUpdateService(
            settingsStore,
            updateOpts,
            storageOpts,
            pkgInspector,
            client,
            verifier,
            installer,
            historyRepo,
            eventBus,
            _clock,
            NullLogger<NexusUpdateService>.Instance);

        return new UpdateHarness(svc, installer, settingsStore, storageOpts, updateOpts, connFactory);
    }

    private StubHttpHandler CreatePublicReleaseHandler(
        string tagName,
        string version,
        long versionCode,
        string signerSha,
        byte[] apkBytes,
        string? overrideApkSha256 = null,
        long? overrideDeclaredSize = null,
        Func<HttpResponseMessage>? overrideApkDownloadResponder = null)
    {
        var apkSha = overrideApkSha256 ?? Convert.ToHexStringLower(SHA256.HashData(apkBytes));
        var size = overrideDeclaredSize ?? apkBytes.Length;
        var apkFileName = $"com.nexus.ecosystem-{version}.apk";
        var apkDownloadUrl = $"https://github.com/nexus-ecosystem/nexus/releases/download/{tagName}/{apkFileName}";
        var manifestUrl = $"https://github.com/nexus-ecosystem/nexus/releases/download/{tagName}/nexus-update-manifest.json";

        var manifest = new NexusReleaseManifest
        {
            Version = version,
            AndroidVersionCode = versionCode,
            PackageId = "com.nexus.ecosystem",
            TagName = tagName,
            ApkFileName = apkFileName,
            ApkSha256 = apkSha,
            SignerCertificateSha256 = signerSha,
            ApkSizeBytes = size,
            PublishedAtUtc = _clock.UtcNow,
            MinimumCompatibleServerVersion = "1.0.0",
            ReleaseNotes = $"Release notas {version}",
            DownloadUrl = apkDownloadUrl
        };

        var releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = tagName,
            name = $"NEXUS {tagName}",
            body = $"Release notas {version}",
            draft = false,
            prerelease = false,
            published_at = _clock.UtcNow,
            assets = new[]
            {
                new { name = apkFileName, size, browser_download_url = apkDownloadUrl },
                new { name = "nexus-update-manifest.json", size = 512L, browser_download_url = manifestUrl }
            }
        }, WebJson);

        var manifestJson = JsonSerializer.Serialize(manifest, WebJson);

        return new StubHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/releases/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }

            if (url == manifestUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
                };
            }

            if (url == apkDownloadUrl)
            {
                return overrideApkDownloadResponder is not null
                    ? overrideApkDownloadResponder()
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(apkBytes)
                    };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static string CertSha256(X509Certificate2 cert)
        => Convert.ToHexStringLower(SHA256.HashData(cert.RawData));

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
            dexStream.Write("dex\n039\0unit"u8);
        }

        return ApkSigningBlockCodec.AttachApkV2SigningBlock(ms.ToArray(), cert);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class UpdateHarness(
        NexusUpdateService service,
        MockAndroidInstaller installer,
        ISettingsStore settingsStore,
        NexusStorageOptions storageOptions,
        NexusUpdateOptions updateOptions,
        SqliteConnectionFactory connFactory) : IAsyncDisposable
    {
        public NexusUpdateService Service { get; } = service;
        public MockAndroidInstaller Installer { get; } = installer;
        public ISettingsStore SettingsStore { get; } = settingsStore;
        public NexusStorageOptions StorageOptions { get; } = storageOptions;
        public NexusUpdateOptions UpdateOptions { get; } = updateOptions;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private readonly SqliteConnectionFactory _ = connFactory;
    }

    private sealed class MutableAppInspector(InstalledAppVersionInfo current) : IAppPackageInspector
    {
        public InstalledAppVersionInfo Current { get; set; } = current;
        public Task<InstalledAppVersionInfo> GetInstalledAppInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);
    }

    private sealed class MockAndroidInstaller(MutableAppInspector inspector) : IAndroidUpdateInstaller
    {
        public List<VerifiedUpdatePackage> LaunchedPackages { get; } = [];

        public Task<bool> CanRequestPackageInstallsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(inspector.Current.CanRequestPackageInstalls);

        public Task<Result> OpenUnknownSourcesSettingsAsync(CancellationToken cancellationToken = default)
        {
            inspector.Current = inspector.Current with { CanRequestPackageInstalls = true };
            return Task.FromResult(Result.Success());
        }

        public Task<Result<UpdateInstallLaunchResult>> LaunchSystemPackageInstallerAsync(
            VerifiedUpdatePackage verifiedPackage,
            CancellationToken cancellationToken = default)
        {
            LaunchedPackages.Add(verifiedPackage);
            return Task.FromResult(Result<UpdateInstallLaunchResult>.Success(new UpdateInstallLaunchResult
            {
                Launched = true,
                RequiresUnknownSourcesPermission = false,
                LocalApkPath = verifiedPackage.LocalApkPath,
                Message = "Instalador nativo Android iniciado após confirmação do usuário."
            }));
        }
    }

    private sealed class NoAuthCredentialProvider : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("Sem sessão")));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("Sem refresh")));
    }

    private sealed class TruncatedStreamContent(byte[] partialBytes, long declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(partialBytes, 0, partialBytes.Length);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(partialBytes, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}

