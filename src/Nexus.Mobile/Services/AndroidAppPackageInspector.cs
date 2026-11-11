using System.Security.Cryptography;
using Android.Content.PM;
using Nexus.Contracts.Updates;
using Nexus.Core.Updates;

namespace Nexus.Mobile.Services;

/// <summary>
/// Reads actual installed package metadata and signing certificate SHA-256 directly from Android PackageManager.
/// </summary>
public sealed class AndroidAppPackageInspector : IAppPackageInspector
{
    public Task<InstalledAppVersionInfo> GetInstalledAppInfoAsync(CancellationToken cancellationToken = default)
    {
        var context = global::Android.App.Application.Context;
        var pm = context.PackageManager!;
        var packageName = context.PackageName ?? "com.nexus.ecosystem";

        var flags = OperatingSystem.IsAndroidVersionAtLeast(28)
            ? PackageInfoFlags.SigningCertificates
            : GetLegacySignatureFlags();

        var info = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? pm.GetPackageInfo(packageName, PackageManager.PackageInfoFlags.Of((long)flags))
            : GetLegacyPackageInfo(pm, packageName, flags);

        var versionName = info?.VersionName ?? "1.0.0";
        long versionCode = OperatingSystem.IsAndroidVersionAtLeast(28)
            ? info?.LongVersionCode ?? 10000L
            : GetLegacyVersionCode(info);

        var signerHex = ExtractFirstSignerSha256(info);
        var canInstall = !OperatingSystem.IsAndroidVersionAtLeast(26) || pm.CanRequestPackageInstalls();

        return Task.FromResult(new InstalledAppVersionInfo
        {
            PackageId = packageName,
            Version = versionName,
            VersionCode = versionCode,
            IsFirstInstall = false,
            SignerCertificateSha256 = signerHex,
            CanRequestPackageInstalls = canInstall,
            SupportsNativePackageInstaller = true,
            PlatformDescription = $"Android {DeviceInfo.VersionString} (API {(int)global::Android.OS.Build.VERSION.SdkInt})"
        });
    }

    internal static string? ExtractFirstSignerSha256(PackageInfo? info)
    {
        if (info is null) return null;

        if (OperatingSystem.IsAndroidVersionAtLeast(28) && info.SigningInfo is not null)
        {
            var signers = info.SigningInfo.GetApkContentsSigners();
            if (signers is { Length: > 0 })
            {
                var raw = signers[0].ToByteArray();
                if (raw is { Length: > 0 })
                {
                    return Convert.ToHexStringLower(SHA256.HashData(raw));
                }
            }
        }

        return ExtractLegacySignerSha256(info);
    }

    internal static PackageInfoFlags GetLegacySignatureFlags()
    {
#pragma warning disable CA1422
        return PackageInfoFlags.Signatures;
#pragma warning restore CA1422
    }

    internal static PackageInfo? GetLegacyPackageInfo(PackageManager pm, string packageName, PackageInfoFlags flags)
    {
#pragma warning disable CA1422
        return pm.GetPackageInfo(packageName, flags);
#pragma warning restore CA1422
    }

    internal static long GetLegacyVersionCode(PackageInfo? info)
    {
#pragma warning disable CA1422
        return info?.VersionCode ?? 10000L;
#pragma warning restore CA1422
    }

    private static string? ExtractLegacySignerSha256(PackageInfo info)
    {
#pragma warning disable CA1422
        var sigs = info.Signatures;
        if (sigs is { Count: > 0 } && sigs[0]?.ToByteArray() is { Length: > 0 } raw)
        {
            return Convert.ToHexStringLower(SHA256.HashData(raw));
        }
#pragma warning restore CA1422
        return null;
    }
}

