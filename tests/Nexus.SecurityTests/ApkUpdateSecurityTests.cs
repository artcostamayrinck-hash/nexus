using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Security.Updates;
using Xunit;

namespace Nexus.SecurityTests;

public sealed class ApkUpdateSecurityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-apk-sec-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.Zero));

    public ApkUpdateSecurityTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task VerifyDownloadedApk_ValidSignedApkWithMatchingCertificateSha256_Succeeds()
    {
        using var cert = CreateSigningCertificate("CN=NEXUS Release Key");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        var apkBytes = CreateSignedApkBytes("com.nexus.ecosystem", 10200, cert);
        var apkPath = WriteTempFile("nexus-1.2.0.apk", apkBytes);
        var apkSha = Convert.ToHexStringLower(SHA256.HashData(apkBytes));

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var manifest = CreateManifest("1.2.0", 10200, apkSha, signerSha, apkBytes.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            IsFirstInstall = false,
            SignerCertificateSha256 = signerSha
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, signerSha);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.IsChecksumVerified);
        Assert.True(result.Value.IsSignatureVerified);
        Assert.True(result.Value.IsPackageIdentityVerified);
        Assert.True(result.Value.IsReadyForUserInstall);
        Assert.Equal("com.nexus.ecosystem", result.Value.ActualPackageId);
        Assert.Equal(signerSha, result.Value.ActualSignerSha256);
        Assert.Equal(apkSha, result.Value.ActualSha256Hex);
        Assert.Equal(10200, result.Value.ActualVersionCode);
    }

    [Fact]
    public async Task VerifyDownloadedApk_DifferentSigningCertificate_FailsWithSignatureMismatch()
    {
        using var trustedCert = CreateSigningCertificate("CN=NEXUS Trusted Release Key");
        using var attackerCert = CreateSigningCertificate("CN=Untrusted Attacker Key");

        var trustedSha = Convert.ToHexStringLower(SHA256.HashData(trustedCert.RawData));
        var attackerSha = Convert.ToHexStringLower(SHA256.HashData(attackerCert.RawData));

        var attackerSignedApk = CreateSignedApkBytes("com.nexus.ecosystem", 10200, attackerCert);
        var apkPath = WriteTempFile("nexus-attacker-signed.apk", attackerSignedApk);
        var attackerApkSha = Convert.ToHexStringLower(SHA256.HashData(attackerSignedApk));

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        // Even if the manifest tries to advertise the attacker cert, installed app pins trustedSha!
        var manifest = CreateManifest("1.2.0", 10200, attackerApkSha, attackerSha, attackerSignedApk.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            IsFirstInstall = false,
            SignerCertificateSha256 = trustedSha
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, trustedSha);

        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_SIGNATURE_MISMATCH", result.Error!.Code);
    }

    [Fact]
    public async Task VerifyDownloadedApk_UnsignedApkWithoutV2Block_FailsWithSignatureMissing()
    {
        using var trustedCert = CreateSigningCertificate("CN=NEXUS Release Key");
        var trustedSha = Convert.ToHexStringLower(SHA256.HashData(trustedCert.RawData));

        var unsignedBytes = CreateUnsignedZipApk("com.nexus.ecosystem", 10200);
        var apkPath = WriteTempFile("nexus-unsigned.apk", unsignedBytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(unsignedBytes));

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var manifest = CreateManifest("1.2.0", 10200, sha, trustedSha, unsignedBytes.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            IsFirstInstall = true
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, trustedSha);

        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_SIGNATURE_MISSING", result.Error!.Code);
    }

    [Fact]
    public async Task VerifyDownloadedApk_CorruptedApkBytes_FailsWithChecksumMismatch()
    {
        using var cert = CreateSigningCertificate("CN=NEXUS Release Key");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        var validApk = CreateSignedApkBytes("com.nexus.ecosystem", 10200, cert);
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(validApk));

        // Flip a byte inside the APK without changing length
        var corrupted = (byte[])validApk.Clone();
        corrupted[corrupted.Length / 2] ^= 0xFF;
        var apkPath = WriteTempFile("nexus-corrupted.apk", corrupted);

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var manifest = CreateManifest("1.2.0", 10200, expectedSha, signerSha, validApk.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            SignerCertificateSha256 = signerSha
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, signerSha);

        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_CHECKSUM_MISMATCH", result.Error!.Code);
    }

    [Fact]
    public async Task VerifyDownloadedApk_TruncatedApk_FailsWithDownloadInterrupted()
    {
        using var cert = CreateSigningCertificate("CN=NEXUS Release Key");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        var validApk = CreateSignedApkBytes("com.nexus.ecosystem", 10200, cert);
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(validApk));

        var truncated = validApk[..(validApk.Length - 64)];
        var apkPath = WriteTempFile("nexus-truncated.apk", truncated);

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var manifest = CreateManifest("1.2.0", 10200, expectedSha, signerSha, validApk.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            SignerCertificateSha256 = signerSha
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, signerSha);

        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_DOWNLOAD_INTERRUPTED", result.Error!.Code);
    }

    [Fact]
    public async Task VerifyDownloadedApk_SpoofedPackageId_FailsWithPackageIdentityMismatch()
    {
        using var cert = CreateSigningCertificate("CN=NEXUS Release Key");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        var wrongPkgApk = CreateSignedApkBytes("com.rogue.spoofed", 10200, cert);
        var apkSha = Convert.ToHexStringLower(SHA256.HashData(wrongPkgApk));
        var apkPath = WriteTempFile("nexus-wrongpkg.apk", wrongPkgApk);

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var manifest = CreateManifest("1.2.0", 10200, apkSha, signerSha, wrongPkgApk.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            SignerCertificateSha256 = signerSha
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, signerSha);

        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_PACKAGE_IDENTITY_MISMATCH", result.Error!.Code);
    }

    [Fact]
    public async Task VerifyDownloadedApk_LowerVersionCode_FailsWithDowngradeBlocked()
    {
        using var cert = CreateSigningCertificate("CN=NEXUS Release Key");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        var olderApk = CreateSignedApkBytes("com.nexus.ecosystem", 10000, cert);
        var apkSha = Convert.ToHexStringLower(SHA256.HashData(olderApk));
        var apkPath = WriteTempFile("nexus-downgrade.apk", olderApk);

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var manifest = CreateManifest("1.0.0", 10000, apkSha, signerSha, olderApk.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.2.0",
            VersionCode = 10200,
            IsFirstInstall = false,
            SignerCertificateSha256 = signerSha
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, signerSha);

        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_DOWNGRADE_BLOCKED", result.Error!.Code);
    }

    [Fact]
    public async Task VerifyDownloadedApk_NonZipGarbageWithMatchingManifestHash_FailsWithApkCorrupted()
    {
        using var cert = CreateSigningCertificate("CN=NEXUS Release Key");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        var fakePayload = Encoding.UTF8.GetBytes("THIS IS NOT A REAL ZIP OR APK ARCHIVE");
        var sha = Convert.ToHexStringLower(SHA256.HashData(fakePayload));
        var apkPath = WriteTempFile("not-an-apk.apk", fakePayload);

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock);
        var manifest = CreateManifest("1.2.0", 10200, sha, signerSha, fakePayload.Length);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            IsFirstInstall = true
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, installed, signerSha);

        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_APK_CORRUPTED", result.Error!.Code);
    }

    private string WriteTempFile(string fileName, byte[] content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static NexusReleaseManifest CreateManifest(
        string version,
        long versionCode,
        string apkSha256,
        string signerSha256,
        long sizeBytes) => new()
    {
        Version = version,
        AndroidVersionCode = versionCode,
        PackageId = "com.nexus.ecosystem",
        TagName = $"v{version}",
        ApkFileName = $"com.nexus.ecosystem-{version}.apk",
        ApkSha256 = apkSha256,
        SignerCertificateSha256 = signerSha256,
        ApkSizeBytes = sizeBytes,
        PublishedAtUtc = new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero),
        MinimumCompatibleServerVersion = "1.0.0",
        DownloadUrl = $"https://github.com/nexus-ecosystem/nexus/releases/download/v{version}/com.nexus.ecosystem-{version}.apk"
    };

    private static X509Certificate2 CreateSigningCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    private static byte[] CreateSignedApkBytes(string packageId, long versionCode, X509Certificate2 cert)
        => ApkSigningBlockCodec.AttachApkV2SigningBlock(CreateUnsignedZipApk(packageId, versionCode), cert);

    private static byte[] CreateUnsignedZipApk(string packageId, long versionCode)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry("AndroidManifest.xml");
            using (var w = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                w.Write($"""<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{packageId}" android:versionCode="{versionCode}" android:versionName="1.2.0" />""");
            }

            var dexEntry = zip.CreateEntry("classes.dex");
            using var dexStream = dexEntry.Open();
            dexStream.Write("dex\n039\0nexus-payload"u8);
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}

