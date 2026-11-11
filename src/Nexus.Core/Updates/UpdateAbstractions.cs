using Nexus.Contracts.Updates;
using Nexus.Core.Errors;

namespace Nexus.Core.Updates;

public interface IAppPackageInspector
{
    Task<InstalledAppVersionInfo> GetInstalledAppInfoAsync(CancellationToken cancellationToken = default);
}

public sealed record ApkVerificationInspection(
    string PackageId,
    long VersionCode,
    IReadOnlyList<string> SignerCertificateSha256List,
    bool HasAndroidManifest,
    bool HasValidZipStructure);

public interface IApkPackageVerifier
{
    Task<Result<VerifiedUpdatePackage>> VerifyDownloadedApkAsync(
        string apkFilePath,
        NexusReleaseManifest manifest,
        InstalledAppVersionInfo installedApp,
        string? configuredExpectedSignerSha256,
        CancellationToken cancellationToken = default);
}

public interface IAndroidUpdateInstaller
{
    Task<bool> CanRequestPackageInstallsAsync(CancellationToken cancellationToken = default);
    Task<Result> OpenUnknownSourcesSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<UpdateInstallLaunchResult>> LaunchSystemPackageInstallerAsync(
        VerifiedUpdatePackage verifiedPackage,
        CancellationToken cancellationToken = default);
}

public interface IGitHubReleaseSource
{
    Task<Result<NexusReleaseManifest>> FetchLatestReleaseAsync(
        UpdateConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<Result> DownloadReleaseApkAsync(
        UpdateConfiguration configuration,
        NexusReleaseManifest manifest,
        string destinationPartPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IUpdateHistoryRepository
{
    Task<Result> AppendAsync(UpdateHistoryRecord record, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<UpdateHistoryRecord>>> ListRecentAsync(int limit = 50, CancellationToken cancellationToken = default);
}

public interface INexusUpdateService
{
    event EventHandler<UpdateDownloadProgress>? ProgressChanged;

    Task<UpdateConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<Result<UpdateConfiguration>> SaveConfigurationAsync(UpdateConfiguration configuration, CancellationToken cancellationToken = default);
    Task<InstalledAppVersionInfo> GetInstalledVersionAsync(CancellationToken cancellationToken = default);
    Task<Result<UpdateAvailabilityReport>> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<Result<VerifiedUpdatePackage>> DownloadAndVerifyUpdateAsync(
        NexusReleaseManifest manifest,
        CancellationToken cancellationToken = default);
    Task<Result<UpdateInstallLaunchResult>> LaunchUserConfirmedInstallAsync(
        VerifiedUpdatePackage package,
        bool userConfirmed,
        CancellationToken cancellationToken = default);
    Task<Result> OpenUnknownSourcesPermissionSettingsAsync(CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<UpdateHistoryRecord>>> GetHistoryAsync(int limit = 50, CancellationToken cancellationToken = default);
}

