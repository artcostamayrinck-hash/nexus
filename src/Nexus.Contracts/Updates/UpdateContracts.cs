using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Updates;

public enum UpdateRepositoryVisibility
{
    PublicDirect = 0,
    PrivateServerRelay = 1
}

public enum UpdateStatusKind
{
    Idle = 0,
    Checking = 1,
    UpToDate = 2,
    UpdateAvailable = 3,
    Downloading = 4,
    Verifying = 5,
    ReadyToInstall = 6,
    PermissionRequired = 7,
    InstallPromptLaunched = 8,
    Failed = 9
}

public enum UpdateFailureReason
{
    None = 0,
    InvalidConfiguration = 1,
    InsecureTransport = 2,
    UntrustedOrigin = 3,
    RepositoryUnavailable = 4,
    ReleaseNotFound = 5,
    PrivateRepositoryAuthRequired = 6,
    DownloadInterrupted = 7,
    CorruptedPackage = 8,
    PackageIdentityMismatch = 9,
    SignatureMismatch = 10,
    DowngradeBlocked = 11,
    InstallPermissionDenied = 12,
    UserConfirmationRequired = 13,
    IncompatibleServerVersion = 14
}

/// <summary>
/// SemVer 2.0.0 parser and comparator (handles MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]).
/// </summary>
public sealed record NexusSemanticVersion : IComparable<NexusSemanticVersion>
{
    private static readonly Regex SemVerRegex = new(
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private NexusSemanticVersion(int major, int minor, int patch, string? prerelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease;
        BuildMetadata = string.IsNullOrWhiteSpace(buildMetadata) ? null : buildMetadata;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public string CanonicalCore => $"{Major}.{Minor}.{Patch}";

    public string NormalizedString => Prerelease is null ? CanonicalCore : $"{CanonicalCore}-{Prerelease}";

    public static bool TryParse(string? raw, out NexusSemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var match = SemVerRegex.Match(raw.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var pre = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        if (pre is not null)
        {
            foreach (var segment in pre.Split('.'))
            {
                if (segment.Length > 1 && segment[0] == '0' && segment.All(char.IsAsciiDigit))
                {
                    return false;
                }
            }
        }

        var build = match.Groups["build"].Success ? match.Groups["build"].Value : null;
        version = new NexusSemanticVersion(major, minor, patch, pre, build);
        return true;
    }

    public int CompareTo(NexusSemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // Per SemVer 2.0.0: normal version has higher precedence than pre-release
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        var leftParts = Prerelease.Split('.');
        var rightParts = other.Prerelease.Split('.');
        var max = Math.Min(leftParts.Length, rightParts.Length);
        for (var i = 0; i < max; i++)
        {
            var left = leftParts[i];
            var right = rightParts[i];
            var leftNum = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var lVal);
            var rightNum = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rVal);

            if (leftNum && rightNum)
            {
                cmp = lVal.CompareTo(rVal);
                if (cmp != 0) return cmp;
            }
            else if (leftNum != rightNum)
            {
                return leftNum ? -1 : 1;
            }
            else
            {
                cmp = string.CompareOrdinal(left, right);
                if (cmp != 0) return cmp;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    public override string ToString()
        => BuildMetadata is null ? NormalizedString : $"{NormalizedString}+{BuildMetadata}";
}

public sealed record InstalledAppVersionInfo : IVersionedContract
{
    public string PackageId { get; init; } = "com.nexus.ecosystem";
    public string Version { get; init; } = "1.0.0";
    public long VersionCode { get; init; } = 10000;
    public bool IsFirstInstall { get; init; }
    public string? SignerCertificateSha256 { get; init; }
    public bool CanRequestPackageInstalls { get; init; }
    public bool SupportsNativePackageInstaller { get; init; }
    public string PlatformDescription { get; init; } = string.Empty;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record UpdateConfiguration : IVersionedContract, IValidatableContract
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string RepositoryOwner { get; init; } = "nexus-ecosystem";

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string RepositoryName { get; init; } = "nexus";

    public UpdateRepositoryVisibility Visibility { get; init; } = UpdateRepositoryVisibility.PublicDirect;
    public bool IncludePrereleases { get; init; }

    [Required]
    [StringLength(260)]
    public string GitHubApiBaseUrl { get; init; } = "https://api.github.com";

    [Required]
    [StringLength(160, MinimumLength = 3)]
    public string ExpectedPackageId { get; init; } = "com.nexus.ecosystem";

    [StringLength(64)]
    public string? ExpectedSignerSha256 { get; init; }

    public bool CheckAutomatically { get; init; } = true;
    public bool RequireUserConfirmation { get; init; } = true;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    // Security invariant: client/APK UpdateConfiguration explicitly carries NO GitHub PAT or secret token.
    public IEnumerable<ContractValidationError> Validate()
    {
        if (string.IsNullOrWhiteSpace(RepositoryOwner) || RepositoryOwner.Contains('/') || RepositoryOwner.Contains(".."))
        {
            yield return new ContractValidationError(nameof(RepositoryOwner), "UPDATE_OWNER_INVALID", "Proprietário do repositório inválido.");
        }

        if (string.IsNullOrWhiteSpace(RepositoryName) || RepositoryName.Contains('/') || RepositoryName.Contains(".."))
        {
            yield return new ContractValidationError(nameof(RepositoryName), "UPDATE_REPO_INVALID", "Nome do repositório inválido.");
        }

        if (!Uri.TryCreate(GitHubApiBaseUrl, UriKind.Absolute, out var apiUri) ||
            !string.Equals(apiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ContractValidationError(nameof(GitHubApiBaseUrl), "UPDATE_HTTPS_REQUIRED", "A URL da API de atualizações deve utilizar HTTPS.");
        }

        if (!string.IsNullOrWhiteSpace(ExpectedSignerSha256) && !IsHexSha256(ExpectedSignerSha256))
        {
            yield return new ContractValidationError(nameof(ExpectedSignerSha256), "UPDATE_SIGNER_HEX_INVALID", "ExpectedSignerSha256 deve conter 64 caracteres hexadecimais.");
        }
    }

    public static bool IsHexSha256(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 64 && value.Trim().All(Uri.IsHexDigit);
}

public sealed record NexusReleaseManifest : IVersionedContract, IValidatableContract
{
    [Required]
    public string Version { get; init; } = string.Empty;

    public long AndroidVersionCode { get; init; }

    [Required]
    public string PackageId { get; init; } = "com.nexus.ecosystem";

    [Required]
    public string TagName { get; init; } = string.Empty;

    [Required]
    public string ApkFileName { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    public string ApkSha256 { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    public string SignerCertificateSha256 { get; init; } = string.Empty;

    public long ApkSizeBytes { get; init; }
    public DateTimeOffset PublishedAtUtc { get; init; }

    [Required]
    public string MinimumCompatibleServerVersion { get; init; } = "1.0.0";

    public bool IsPrerelease { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;

    [Required]
    public string DownloadUrl { get; init; } = string.Empty;

    public bool ServedThroughServerRelay { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (!NexusSemanticVersion.TryParse(Version, out _))
        {
            yield return new ContractValidationError(nameof(Version), "MANIFEST_VERSION_INVALID", $"Versão SemVer inválida no manifesto: '{Version}'.");
        }

        if (AndroidVersionCode < 1)
        {
            yield return new ContractValidationError(nameof(AndroidVersionCode), "MANIFEST_VERSION_CODE_INVALID", "AndroidVersionCode deve ser positivo.");
        }

        if (!UpdateConfiguration.IsHexSha256(ApkSha256))
        {
            yield return new ContractValidationError(nameof(ApkSha256), "MANIFEST_SHA256_INVALID", "ApkSha256 ausente ou inválido (requer 64 hex).");
        }

        if (!UpdateConfiguration.IsHexSha256(SignerCertificateSha256))
        {
            yield return new ContractValidationError(nameof(SignerCertificateSha256), "MANIFEST_SIGNER_INVALID", "SignerCertificateSha256 ausente ou inválido.");
        }

        if (ApkSizeBytes <= 0)
        {
            yield return new ContractValidationError(nameof(ApkSizeBytes), "MANIFEST_SIZE_INVALID", "ApkSizeBytes deve ser maior que zero.");
        }

        if (!Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var dlUri) ||
            !string.Equals(dlUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ContractValidationError(nameof(DownloadUrl), "MANIFEST_DOWNLOAD_HTTPS_REQUIRED", "A URL de download da release deve utilizar HTTPS.");
        }
    }
}

public sealed record UpdateAvailabilityReport : IVersionedContract
{
    public InstalledAppVersionInfo InstalledVersion { get; init; } = new();
    public NexusReleaseManifest? LatestRelease { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public bool IsFirstInstallAvailable { get; init; }
    public bool IsCompatibleWithCurrentSigner { get; init; }
    public bool IsCompatibleWithServer { get; init; } = true;
    public UpdateFailureReason FailureReason { get; init; } = UpdateFailureReason.None;
    public string Message { get; init; } = string.Empty;
    public string ComparisonSummary { get; init; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record UpdateDownloadProgress : IVersionedContract
{
    public string TagName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public UpdateStatusKind Stage { get; init; }
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public double PercentComplete => TotalBytes > 0 ? Math.Clamp(BytesDownloaded * 100.0 / TotalBytes, 0.0, 100.0) : 0.0;
    public string StatusMessage { get; init; } = string.Empty;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record VerifiedUpdatePackage : IVersionedContract
{
    public NexusReleaseManifest Release { get; init; } = new();
    public string LocalApkPath { get; init; } = string.Empty;
    public string ActualSha256Hex { get; init; } = string.Empty;
    public string ActualPackageId { get; init; } = string.Empty;
    public string ActualSignerSha256 { get; init; } = string.Empty;
    public long ActualVersionCode { get; init; }
    public bool IsChecksumVerified { get; init; }
    public bool IsSignatureVerified { get; init; }
    public bool IsPackageIdentityVerified { get; init; }
    public bool IsReadyForUserInstall => IsChecksumVerified && IsSignatureVerified && IsPackageIdentityVerified;
    public DateTimeOffset VerifiedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record UpdateInstallLaunchResult : IVersionedContract
{
    public bool Launched { get; init; }
    public bool RequiresUnknownSourcesPermission { get; init; }
    public string LocalApkPath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public UpdateFailureReason FailureReason { get; init; } = UpdateFailureReason.None;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record UpdateRelayStatusContract : IVersionedContract
{
    public bool IsConfigured { get; init; }
    public bool HasServerTokenConfigured { get; init; }
    public string RepositorySlug { get; init; } = string.Empty;
    public string ExpectedPackageId { get; init; } = "com.nexus.ecosystem";
    public string CurrentServerVersion { get; init; } = "1.0.0";
    public string MinimumCompatibleServerVersion { get; init; } = "1.0.0";
    public string StatusMessage { get; init; } = string.Empty;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record UpdateHistoryRecord : IVersionedContract
{
    public Guid RecordId { get; init; } = Guid.NewGuid();
    public string InstalledVersion { get; init; } = string.Empty;
    public string? DiscoveredVersion { get; init; }
    public UpdateRepositoryVisibility Visibility { get; init; }
    public UpdateStatusKind OutcomeStatus { get; init; }
    public UpdateFailureReason FailureReason { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset RecordedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

