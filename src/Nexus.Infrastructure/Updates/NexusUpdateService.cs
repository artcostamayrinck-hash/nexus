using Microsoft.Extensions.Logging;
using Nexus.Contracts.Events;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Storage;
using Nexus.Core.Updates;
using Nexus.Infrastructure.Persistence;
using Nexus.Security.Updates;

namespace Nexus.Infrastructure.Updates;

/// <summary>
/// Full NEXUS Update orchestrator: checks installed SemVer/signer identity, queries public GitHub or private Nexus.Server relay,
/// compares SemVer, downloads over HTTPS into isolated staging, verifies SHA-256 + APK signature + package ID,
/// and delegates installation ONLY upon explicit user confirmation.
/// </summary>
public sealed class NexusUpdateService : INexusUpdateService
{
    private const string SettingsKey = "updates.configuration.v1";

    private readonly ISettingsStore _settingsStore;
    private readonly NexusUpdateOptions _options;
    private readonly NexusStorageOptions _storageOptions;
    private readonly IAppPackageInspector _packageInspector;
    private readonly IGitHubReleaseSource _releaseSource;
    private readonly IApkPackageVerifier _apkVerifier;
    private readonly IAndroidUpdateInstaller _androidInstaller;
    private readonly IUpdateHistoryRepository _historyRepository;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly ILogger<NexusUpdateService> _logger;

    public NexusUpdateService(
        ISettingsStore settingsStore,
        NexusUpdateOptions options,
        NexusStorageOptions storageOptions,
        IAppPackageInspector packageInspector,
        IGitHubReleaseSource releaseSource,
        IApkPackageVerifier apkVerifier,
        IAndroidUpdateInstaller androidInstaller,
        IUpdateHistoryRepository historyRepository,
        IEventBus eventBus,
        IClock clock,
        ILogger<NexusUpdateService> logger)
    {
        _settingsStore = settingsStore;
        _options = options;
        _storageOptions = storageOptions;
        _packageInspector = packageInspector;
        _releaseSource = releaseSource;
        _apkVerifier = apkVerifier;
        _androidInstaller = androidInstaller;
        _historyRepository = historyRepository;
        _eventBus = eventBus;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<UpdateDownloadProgress>? ProgressChanged;

    public async Task<UpdateConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _settingsStore.GetAsync<UpdateConfiguration>(SettingsKey, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess && loaded.Value is not null
            ? loaded.Value
            : _options.CreateDefaultConfiguration();
    }

    public async Task<Result<UpdateConfiguration>> SaveConfigurationAsync(
        UpdateConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validation = Nexus.Contracts.Common.ContractValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            var err = validation.Errors[0];
            return Result<UpdateConfiguration>.Failure(NexusError.Validation(err.Message, code: err.Code));
        }

        // Enforce explicit user confirmation policy globally
        var normalized = configuration with
        {
            RepositoryOwner = configuration.RepositoryOwner.Trim(),
            RepositoryName = configuration.RepositoryName.Trim(),
            ExpectedPackageId = configuration.ExpectedPackageId.Trim(),
            ExpectedSignerSha256 = string.IsNullOrWhiteSpace(configuration.ExpectedSignerSha256)
                ? null
                : configuration.ExpectedSignerSha256.Trim().ToLowerInvariant(),
            RequireUserConfirmation = true
        };

        var saveRes = await _settingsStore.SetAsync(SettingsKey, normalized, cancellationToken).ConfigureAwait(false);
        return saveRes.IsSuccess
            ? Result<UpdateConfiguration>.Success(normalized)
            : Result<UpdateConfiguration>.Failure(saveRes.Error!);
    }

    public Task<InstalledAppVersionInfo> GetInstalledVersionAsync(CancellationToken cancellationToken = default)
        => _packageInspector.GetInstalledAppInfoAsync(cancellationToken);

    public async Task<Result<UpdateAvailabilityReport>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var installed = await _packageInspector.GetInstalledAppInfoAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        ReportProgress(string.Empty, installed.Version, UpdateStatusKind.Checking, 0, 0, "Consultando versão mais recente...");

        var fetchRes = await _releaseSource.FetchLatestReleaseAsync(config, cancellationToken).ConfigureAwait(false);
        if (fetchRes.IsFailure)
        {
            var reason = MapErrorToFailureReason(fetchRes.Error!);
            var failReport = new UpdateAvailabilityReport
            {
                InstalledVersion = installed,
                LatestRelease = null,
                IsUpdateAvailable = false,
                IsFirstInstallAvailable = false,
                IsCompatibleWithCurrentSigner = false,
                IsCompatibleWithServer = true,
                FailureReason = reason,
                Message = fetchRes.Error!.Message,
                ComparisonSummary = $"Instalado: v{installed.Version} · Falha ao consultar: {fetchRes.Error.Message}",
                CheckedAtUtc = now
            };

            await RecordHistoryAsync(installed.Version, null, config.Visibility, UpdateStatusKind.Failed, reason, failReport.Message, now, cancellationToken).ConfigureAwait(false);
            return Result<UpdateAvailabilityReport>.Failure(fetchRes.Error!);
        }

        var release = fetchRes.Value;

        if (!string.Equals(release.PackageId, config.ExpectedPackageId, StringComparison.Ordinal))
        {
            const string msg = "Identidade de pacote declarada na release diverge do pacote NEXUS esperado.";
            await RecordHistoryAsync(installed.Version, release.Version, config.Visibility, UpdateStatusKind.Failed, UpdateFailureReason.PackageIdentityMismatch, msg, now, cancellationToken).ConfigureAwait(false);
            return Result<UpdateAvailabilityReport>.Failure(NexusError.Unauthorized(msg, code: "UPDATE_PACKAGE_IDENTITY_MISMATCH"));
        }

        // Compare signer identity early so incompatible APK releases are surfaced cleanly
        var expectedSigner = !string.IsNullOrWhiteSpace(installed.SignerCertificateSha256)
            ? installed.SignerCertificateSha256
            : config.ExpectedSignerSha256;
        var signerCompatible = string.IsNullOrWhiteSpace(expectedSigner) ||
                               ApkPackageVerifier.FixedTimeHexEquals(release.SignerCertificateSha256, expectedSigner);

        if (!signerCompatible)
        {
            const string msg = "A assinatura da release não corresponde ao certificado da versão instalada. Atualização direta incompatível no Android.";
            await RecordHistoryAsync(installed.Version, release.Version, config.Visibility, UpdateStatusKind.Failed, UpdateFailureReason.SignatureMismatch, msg, now, cancellationToken).ConfigureAwait(false);
            return Result<UpdateAvailabilityReport>.Failure(NexusError.Unauthorized(msg, code: "UPDATE_SIGNATURE_MISMATCH"));
        }

        // Evaluate SemVer + VersionCode
        if (!NexusSemanticVersion.TryParse(release.Version, out var remoteSemVer) || remoteSemVer is null)
        {
            return Result<UpdateAvailabilityReport>.Failure(NexusError.Validation("Versão remota SemVer inválida.", code: "MANIFEST_VERSION_INVALID"));
        }

        NexusSemanticVersion.TryParse(installed.Version, out var localSemVer);

        bool isNewer;
        bool isFirstInstall = installed.IsFirstInstall;
        if (isFirstInstall)
        {
            isNewer = true;
        }
        else if (localSemVer is not null)
        {
            var cmp = remoteSemVer.CompareTo(localSemVer);
            isNewer = cmp > 0 && release.AndroidVersionCode >= installed.VersionCode;
        }
        else
        {
            isNewer = release.AndroidVersionCode > installed.VersionCode;
        }

        var outcome = isNewer ? UpdateStatusKind.UpdateAvailable : UpdateStatusKind.UpToDate;
        var summary = isFirstInstall
            ? $"Primeira instalação disponível: v{release.Version} (versionCode {release.AndroidVersionCode})."
            : isNewer
                ? $"Nova versão disponível: v{release.Version} (atual: v{installed.Version})."
                : $"NEXUS já está atualizado (atual: v{installed.Version}, última release: v{release.Version}).";

        var report = new UpdateAvailabilityReport
        {
            InstalledVersion = installed,
            LatestRelease = release,
            IsUpdateAvailable = isNewer,
            IsFirstInstallAvailable = isFirstInstall,
            IsCompatibleWithCurrentSigner = signerCompatible,
            IsCompatibleWithServer = true,
            FailureReason = UpdateFailureReason.None,
            Message = summary,
            ComparisonSummary = summary,
            CheckedAtUtc = now
        };

        await RecordHistoryAsync(installed.Version, release.Version, config.Visibility, outcome, UpdateFailureReason.None, summary, now, cancellationToken).ConfigureAwait(false);
        ReportProgress(release.TagName, release.Version, outcome, 0, release.ApkSizeBytes, summary);

        await _eventBus.PublishAsync(new DiagnosticDomainEvent(
            NexusEventTypes.UpdateCheckCompleted,
            summary), cancellationToken).ConfigureAwait(false);

        return Result<UpdateAvailabilityReport>.Success(report);
    }

    public async Task<Result<VerifiedUpdatePackage>> DownloadAndVerifyUpdateAsync(
        NexusReleaseManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var installed = await _packageInspector.GetInstalledAppInfoAsync(cancellationToken).ConfigureAwait(false);

        var safeName = SanitizeApkFileName(string.IsNullOrWhiteSpace(manifest.ApkFileName)
            ? $"nexus-{manifest.TagName}.apk"
            : manifest.ApkFileName);
        if (!safeName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            safeName += ".apk";
        }

        var stagingDir = Path.Combine(_storageOptions.RootDirectory, _options.StagingFolderName);
        var verifiedDir = Path.Combine(_storageOptions.RootDirectory, _options.VerifiedFolderName);
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(verifiedDir);

        var partPath = Path.Combine(stagingDir, safeName + ".part");
        var verifiedPath = Path.Combine(verifiedDir, safeName);

        var reporter = new Progress<UpdateDownloadProgress>(p => ProgressChanged?.Invoke(this, p));
        var downloadRes = await _releaseSource.DownloadReleaseApkAsync(config, manifest, partPath, reporter, cancellationToken).ConfigureAwait(false);

        if (downloadRes.IsFailure)
        {
            var reason = MapErrorToFailureReason(downloadRes.Error!);
            ReportProgress(manifest.TagName, manifest.Version, UpdateStatusKind.Failed, 0, manifest.ApkSizeBytes, downloadRes.Error!.Message);
            await RecordHistoryAsync(installed.Version, manifest.Version, config.Visibility, UpdateStatusKind.Failed, reason, downloadRes.Error.Message, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return Result<VerifiedUpdatePackage>.Failure(downloadRes.Error!);
        }

        ReportProgress(manifest.TagName, manifest.Version, UpdateStatusKind.Verifying, manifest.ApkSizeBytes, manifest.ApkSizeBytes, "Verificando SHA-256, identidade do pacote e assinatura criptográfica do APK...");

        var verifyRes = await _apkVerifier.VerifyDownloadedApkAsync(
            partPath,
            manifest,
            installed,
            config.ExpectedSignerSha256,
            cancellationToken).ConfigureAwait(false);

        if (verifyRes.IsFailure)
        {
            TryDeleteFile(partPath);
            var reason = MapErrorToFailureReason(verifyRes.Error!);
            ReportProgress(manifest.TagName, manifest.Version, UpdateStatusKind.Failed, 0, manifest.ApkSizeBytes, verifyRes.Error!.Message);
            await RecordHistoryAsync(installed.Version, manifest.Version, config.Visibility, UpdateStatusKind.Failed, reason, verifyRes.Error.Message, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return Result<VerifiedUpdatePackage>.Failure(verifyRes.Error!);
        }

        if (File.Exists(verifiedPath))
        {
            File.Delete(verifiedPath);
        }

        File.Move(partPath, verifiedPath);

        var finalPkg = verifyRes.Value with { LocalApkPath = verifiedPath };
        var msg = $"APK v{manifest.Version} validado (SHA-256 + assinatura). Aguardando confirmação explícita do usuário para instalar.";
        ReportProgress(manifest.TagName, manifest.Version, UpdateStatusKind.ReadyToInstall, manifest.ApkSizeBytes, manifest.ApkSizeBytes, msg);

        await RecordHistoryAsync(installed.Version, manifest.Version, config.Visibility, UpdateStatusKind.ReadyToInstall, UpdateFailureReason.None, msg, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

        await _eventBus.PublishAsync(new DiagnosticDomainEvent(
            NexusEventTypes.UpdatePackageVerified,
            manifest.Version), cancellationToken).ConfigureAwait(false);

        return Result<VerifiedUpdatePackage>.Success(finalPkg);
    }

    public async Task<Result<UpdateInstallLaunchResult>> LaunchUserConfirmedInstallAsync(
        VerifiedUpdatePackage package,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!userConfirmed)
        {
            return Result<UpdateInstallLaunchResult>.Failure(NexusError.Validation(
                "Instalação bloqueada: o usuário deve confirmar explicitamente antes de abrir o instalador do Android.",
                code: "UPDATE_USER_CONFIRMATION_REQUIRED"));
        }

        if (!package.IsReadyForUserInstall || !File.Exists(package.LocalApkPath))
        {
            return Result<UpdateInstallLaunchResult>.Failure(NexusError.Validation(
                "O pacote APK ainda não foi verificado integralmente (SHA-256 e assinatura obrigatórios).",
                code: "UPDATE_APK_NOT_VERIFIED"));
        }

        var canInstall = await _androidInstaller.CanRequestPackageInstallsAsync(cancellationToken).ConfigureAwait(false);
        if (!canInstall)
        {
            var installed = await _packageInspector.GetInstalledAppInfoAsync(cancellationToken).ConfigureAwait(false);
            if (installed.SupportsNativePackageInstaller)
            {
                const string permMsg = "Permissão 'Instalar apps desconhecidos' necessária para iniciar o instalador do Android.";
                ReportProgress(package.Release.TagName, package.Release.Version, UpdateStatusKind.PermissionRequired, package.Release.ApkSizeBytes, package.Release.ApkSizeBytes, permMsg);
                return Result<UpdateInstallLaunchResult>.Success(new UpdateInstallLaunchResult
                {
                    Launched = false,
                    RequiresUnknownSourcesPermission = true,
                    LocalApkPath = package.LocalApkPath,
                    FailureReason = UpdateFailureReason.InstallPermissionDenied,
                    Message = permMsg
                });
            }
        }

        var launchRes = await _androidInstaller.LaunchSystemPackageInstallerAsync(package, cancellationToken).ConfigureAwait(false);
        if (launchRes.IsSuccess)
        {
            var status = launchRes.Value.Launched ? UpdateStatusKind.InstallPromptLaunched : UpdateStatusKind.ReadyToInstall;
            ReportProgress(package.Release.TagName, package.Release.Version, status, package.Release.ApkSizeBytes, package.Release.ApkSizeBytes, launchRes.Value.Message);

            await _eventBus.PublishAsync(new DiagnosticDomainEvent(
                NexusEventTypes.UpdateInstallPrompted,
                package.Release.Version), cancellationToken).ConfigureAwait(false);
        }

        return launchRes;
    }

    public Task<Result> OpenUnknownSourcesPermissionSettingsAsync(CancellationToken cancellationToken = default)
        => _androidInstaller.OpenUnknownSourcesSettingsAsync(cancellationToken);

    public Task<Result<IReadOnlyList<UpdateHistoryRecord>>> GetHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _historyRepository.ListRecentAsync(limit, cancellationToken);

    private void ReportProgress(string tag, string version, UpdateStatusKind stage, long downloaded, long total, string message)
        => ProgressChanged?.Invoke(this, new UpdateDownloadProgress
        {
            TagName = tag,
            Version = version,
            Stage = stage,
            BytesDownloaded = downloaded,
            TotalBytes = total,
            StatusMessage = message
        });

    private async Task RecordHistoryAsync(
        string installedVersion,
        string? discoveredVersion,
        UpdateRepositoryVisibility visibility,
        UpdateStatusKind status,
        UpdateFailureReason reason,
        string message,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rec = new UpdateHistoryRecord
        {
            RecordId = Guid.NewGuid(),
            InstalledVersion = installedVersion,
            DiscoveredVersion = discoveredVersion,
            Visibility = visibility,
            OutcomeStatus = status,
            FailureReason = reason,
            Message = message,
            RecordedAtUtc = now
        };
        var res = await _historyRepository.AppendAsync(rec, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            _logger.LogDebug("Failed to persist update history record: {Error}", res.Error?.Message);
        }
    }

    public static UpdateFailureReason MapErrorToFailureReason(NexusError error) => error.Code switch
    {
        "UPDATE_OWNER_INVALID" or "UPDATE_REPO_INVALID" or "UPDATE_SIGNER_HEX_INVALID" or "UPDATE_INVALID_URL" => UpdateFailureReason.InvalidConfiguration,
        "UPDATE_HTTPS_REQUIRED" or "UPDATE_INSECURE_TRANSPORT" or "MANIFEST_DOWNLOAD_HTTPS_REQUIRED" => UpdateFailureReason.InsecureTransport,
        "UPDATE_UNTRUSTED_ORIGIN" => UpdateFailureReason.UntrustedOrigin,
        "UPDATE_REPOSITORY_UNAVAILABLE" => UpdateFailureReason.RepositoryUnavailable,
        "UPDATE_RELEASE_NOT_FOUND" => UpdateFailureReason.ReleaseNotFound,
        "UPDATE_PRIVATE_AUTH_REQUIRED" => UpdateFailureReason.PrivateRepositoryAuthRequired,
        "UPDATE_DOWNLOAD_INTERRUPTED" => UpdateFailureReason.DownloadInterrupted,
        "UPDATE_CHECKSUM_MISMATCH" or "UPDATE_APK_CORRUPTED" or "UPDATE_APK_MISSING" or "UPDATE_APK_READ_ERROR" or "UPDATE_MANIFEST_MISSING" or "UPDATE_MANIFEST_INVALID" => UpdateFailureReason.CorruptedPackage,
        "UPDATE_PACKAGE_IDENTITY_MISMATCH" => UpdateFailureReason.PackageIdentityMismatch,
        "UPDATE_SIGNATURE_MISMATCH" or "UPDATE_SIGNATURE_MISSING" => UpdateFailureReason.SignatureMismatch,
        "UPDATE_DOWNGRADE_BLOCKED" => UpdateFailureReason.DowngradeBlocked,
        "UPDATE_USER_CONFIRMATION_REQUIRED" => UpdateFailureReason.UserConfirmationRequired,
        _ => error.Category == ErrorCategory.Network
            ? UpdateFailureReason.RepositoryUnavailable
            : UpdateFailureReason.InvalidConfiguration
    };

    private static string SanitizeApkFileName(string rawName)
    {
        var leaf = Path.GetFileName(rawName.Trim());
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return "nexus-update.apk";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(leaf.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Replace("..", "_", StringComparison.Ordinal);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }
}

