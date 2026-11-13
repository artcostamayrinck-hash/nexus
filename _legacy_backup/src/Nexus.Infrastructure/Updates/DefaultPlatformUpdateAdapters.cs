using System.Reflection;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Updates;

namespace Nexus.Infrastructure.Updates;

/// <summary>
/// Cross-platform fallback inspector when running on Windows/Linux hosts or headless tests.
/// Android replaces this implementation via <c>AndroidAppPackageInspector</c> to query OS <c>PackageManager</c>.
/// </summary>
public sealed class DefaultAppPackageInspector : IAppPackageInspector
{
    private readonly ILocalDeviceContext _localDevice;
    private readonly NexusUpdateOptions _options;

    public DefaultAppPackageInspector(ILocalDeviceContext localDevice, NexusUpdateOptions options)
    {
        _localDevice = localDevice;
        _options = options;
    }

    public Task<InstalledAppVersionInfo> GetInstalledAppInfoAsync(CancellationToken cancellationToken = default)
    {
        var version = NormalizeSemVer(_localDevice.AppVersion)
            ?? NormalizeSemVer(typeof(DefaultAppPackageInspector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            ?? "1.0.0";

        var code = ComputeVersionCode(version);
        return Task.FromResult(new InstalledAppVersionInfo
        {
            PackageId = _options.DefaultPackageId,
            Version = version,
            VersionCode = code,
            IsFirstInstall = false,
            SignerCertificateSha256 = _options.DefaultExpectedSignerSha256,
            CanRequestPackageInstalls = false,
            SupportsNativePackageInstaller = false,
            PlatformDescription = $"{_localDevice.Platform} ({_localDevice.OperatingSystemVersion})"
        });
    }

    private static string? NormalizeSemVer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Split('+')[0].Trim();
        return NexusSemanticVersion.TryParse(clean, out var parsed) && parsed is not null
            ? parsed.NormalizedString
            : null;
    }

    private static long ComputeVersionCode(string semver)
    {
        if (NexusSemanticVersion.TryParse(semver, out var v) && v is not null)
        {
            return Math.Max(1L, v.Major * 10000L + v.Minor * 100L + v.Patch);
        }

        return 10000L;
    }
}

/// <summary>
/// Default non-Android platform installer handler that refuses silent execution cleanly.
/// </summary>
public sealed class UnsupportedPlatformUpdateInstaller : IAndroidUpdateInstaller
{
    public Task<bool> CanRequestPackageInstallsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<Result> OpenUnknownSourcesSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NexusError.Validation(
            "A permissão 'Instalar apps desconhecidos' aplica-se somente a dispositivos Android.",
            code: "UPDATE_ANDROID_ONLY")));

    public Task<Result<UpdateInstallLaunchResult>> LaunchSystemPackageInstallerAsync(
        VerifiedUpdatePackage verifiedPackage,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<UpdateInstallLaunchResult>.Success(new UpdateInstallLaunchResult
        {
            Launched = false,
            RequiresUnknownSourcesPermission = false,
            LocalApkPath = verifiedPackage.LocalApkPath,
            FailureReason = UpdateFailureReason.InstallPermissionDenied,
            Message = $"APK verificado pronto em '{verifiedPackage.LocalApkPath}'. O instalador nativo só pode ser acionado em Android (instalação automática/silenciosa é proibida)."
        }));
    }
}

