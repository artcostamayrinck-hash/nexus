using System.Security.Cryptography;
using Android.Content.PM;
using Nexus.Core.Errors;
using Nexus.Core.Updates;
using Nexus.Security.Updates;

namespace Nexus.Mobile.Services;

/// <summary>
/// Native Android APK archive inspector combining managed ZIP validation with Android OS
/// <c>PackageManager.GetPackageArchiveInfo</c> signature and package ID inspection.
/// </summary>
public sealed class AndroidApkArchiveReader : IApkArchiveReader
{
    private readonly ManagedApkArchiveReader _managedFallback = new();

    public async Task<Result<ApkVerificationInspection>> InspectAsync(
        string apkFilePath,
        CancellationToken cancellationToken = default)
    {
        var managedRes = await _managedFallback.InspectAsync(apkFilePath, cancellationToken).ConfigureAwait(false);
        if (managedRes.IsFailure)
        {
            return managedRes;
        }

        var context = global::Android.App.Application.Context;
        var pm = context.PackageManager!;
        var flags = OperatingSystem.IsAndroidVersionAtLeast(28)
            ? PackageInfoFlags.SigningCertificates
            : AndroidAppPackageInspector.GetLegacySignatureFlags();

        var archiveInfo = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? pm.GetPackageArchiveInfo(apkFilePath, PackageManager.PackageInfoFlags.Of((long)flags))
            : GetLegacyArchiveInfo(pm, apkFilePath, flags);

        if (archiveInfo is null)
        {
            return Result<ApkVerificationInspection>.Failure(NexusError.Validation(
                "O sistema Android rejeitou a leitura estrutural deste arquivo APK.",
                code: "UPDATE_APK_CORRUPTED"));
        }

        var certList = new List<string>();
        var nativeSigner = AndroidAppPackageInspector.ExtractFirstSignerSha256(archiveInfo);
        if (!string.IsNullOrWhiteSpace(nativeSigner))
        {
            certList.Add(nativeSigner);
        }

        foreach (var managedCert in managedRes.Value.SignerCertificateSha256List)
        {
            if (!certList.Contains(managedCert, StringComparer.OrdinalIgnoreCase))
            {
                certList.Add(managedCert);
            }
        }

        var versionCode = OperatingSystem.IsAndroidVersionAtLeast(28)
            ? archiveInfo.LongVersionCode
            : AndroidAppPackageInspector.GetLegacyVersionCode(archiveInfo);

        return Result<ApkVerificationInspection>.Success(new ApkVerificationInspection(
            PackageId: archiveInfo.PackageName ?? managedRes.Value.PackageId,
            VersionCode: versionCode,
            SignerCertificateSha256List: certList,
            HasAndroidManifest: true,
            HasValidZipStructure: managedRes.Value.HasValidZipStructure));
    }

    private static PackageInfo? GetLegacyArchiveInfo(PackageManager pm, string path, PackageInfoFlags flags)
    {
#pragma warning disable CA1422
        return pm.GetPackageArchiveInfo(path, flags);
#pragma warning restore CA1422
    }
}

