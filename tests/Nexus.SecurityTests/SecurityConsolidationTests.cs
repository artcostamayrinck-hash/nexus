using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Security.Credentials;
using Nexus.Security.Tls;
using Nexus.Security.Tokens;
using Nexus.Security.Updates;
using Xunit;

namespace Nexus.SecurityTests;

public sealed class SecurityConsolidationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-f11-sec-" + Guid.NewGuid().ToString("N"));

    public SecurityConsolidationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task TlsPinning_CreatePinnedSocketsHandler_AcceptsExpectedCertAndBlocksMismatch()
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_tempDir, "tls-vault")
        });
        var tlsManager = new LocalTlsCertificateManager(vault, new SecureTokenGenerator());
        var certRes = await tlsManager.GetOrCreateServerCertificateAsync("localhost");
        Assert.True(certRes.IsSuccess, certRes.Error?.Message);
        using var serverCert = certRes.Value;
        var expectedThumbprint = tlsManager.GetSha256Thumbprint(serverCert);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var tcp = await listener.AcceptTcpClientAsync(cts.Token);
                using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = serverCert,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false
                        },
                        cts.Token);

                    var buf = new byte[1024];
                    _ = await ssl.ReadAsync(buf, cts.Token);
                    var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray();
                    await ssl.WriteAsync(response, cts.Token);
                    await ssl.FlushAsync(cts.Token);
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException)
                {
                    // Expected when unpinned/mismatched client closes handshake cleanly.
                }
            }
        }, cts.Token);

        using (var pinnedHandler = LocalTlsCertificateManager.CreatePinnedSocketsHandler(expectedThumbprint))
        using (var pinnedClient = new HttpClient(pinnedHandler))
        {
            var body = await pinnedClient.GetStringAsync($"https://127.0.0.1:{port}/health", cts.Token);
            Assert.Equal("OK", body);
        }

        var wrongThumbprint = new string('F', 64);
        using (var attackerHandler = LocalTlsCertificateManager.CreatePinnedSocketsHandler(wrongThumbprint))
        using (var attackerClient = new HttpClient(attackerHandler))
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => attackerClient.GetStringAsync($"https://127.0.0.1:{port}/health", cts.Token));
        }

        await serverTask;
    }

    [Fact]
    public async Task CredentialStore_EncryptsSecretsAtRest_WithoutPlaintextOnDisk()
    {
        var vaultDir = Path.Combine(_tempDir, "cred-vault");
        ISecureCredentialStore store = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = vaultDir
        });

        const string sensitiveToken = "nexus_rt_ultra_sensitive_bearer_secret_99182";
        var saved = await store.SaveStringAsync("device.access.token", sensitiveToken);
        Assert.True(saved.IsSuccess, saved.Error?.Message);

        var loaded = await store.GetStringAsync("device.access.token");
        Assert.True(loaded.IsSuccess);
        Assert.Equal(sensitiveToken, loaded.Value);

        var files = Directory.GetFiles(vaultDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var rawText = await File.ReadAllTextAsync(file, Encoding.UTF8);
            Assert.DoesNotContain(sensitiveToken, rawText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ApkPackageVerifier_RejectsVersionDowngrade_EvenWhenSignatureMatches()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NEXUS Release", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));

        byte[] apkBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifestEntry = zip.CreateEntry("AndroidManifest.xml");
                using var w = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
                w.Write("""<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="com.nexus.ecosystem" android:versionCode="10000" />""");
            }
            apkBytes = ApkSigningBlockCodec.AttachApkV2SigningBlock(ms.ToArray(), cert);
        }

        var apkPath = Path.Combine(_tempDir, "downgrade.apk");
        await File.WriteAllBytesAsync(apkPath, apkBytes);

        var verifier = new ApkPackageVerifier(new ManagedApkArchiveReader(), new FixedClock(new DateTimeOffset(2026, 11, 11, 21, 0, 0, TimeSpan.Zero)));
        var manifest = new NexusReleaseManifest
        {
            TagName = "v1.0.0",
            Version = "1.0.0",
            AndroidVersionCode = 10000,
            PackageId = "com.nexus.ecosystem",
            DownloadUrl = "https://github.com/nexus/nexus-ecosystem/releases/download/v1.0.0/app.apk",
            ApkFileName = "app.apk",
            ApkSizeBytes = apkBytes.Length,
            ApkSha256 = Convert.ToHexStringLower(SHA256.HashData(apkBytes)),
            SignerCertificateSha256 = signerSha,
            MinimumCompatibleServerVersion = "1.0.0",
            PublishedAtUtc = new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.Zero)
        };

        var newerInstalled = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.1.0",
            VersionCode = 10100,
            SignerCertificateSha256 = signerSha
        };

        var result = await verifier.VerifyDownloadedApkAsync(apkPath, manifest, newerInstalled, signerSha);
        Assert.True(result.IsFailure);
        Assert.Equal("UPDATE_DOWNGRADE_BLOCKED", result.Error!.Code);
    }

    [Fact]
    public void RepositoryHygiene_NoRealKeystoresTokensOrPrivateKeysCheckedIntoWorkspace()
    {
        var root = FindRepoRoot();
        var forbiddenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".keystore", ".jks", ".pfx", ".p12", ".pem", ".key", ".snk", ".crt", ".cer"
        };

        var inspectDirs = new[] { "src", "config", "packaging", "docs", ".github" };
        foreach (var relDir in inspectDirs)
        {
            var absDir = Path.Combine(root, relDir);
            if (!Directory.Exists(absDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(absDir, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ext = Path.GetExtension(file);
                Assert.False(forbiddenExtensions.Contains(ext), $"Forbidden credential/key artifact present in repo: {file}");

                if (ext is ".json" or ".yml" or ".yaml" or ".ps1" or ".md" or ".cs" or ".xml")
                {
                    var content = File.ReadAllText(file);
                    Assert.DoesNotContain("github_pat_", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("-----BEGIN PRIVATE KEY-----", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("-----BEGIN RSA PRIVATE KEY-----", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("-----BEGIN EC PRIVATE KEY-----", content, StringComparison.Ordinal);
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Nexus.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Nexus.slnx root not found from test binary directory.");
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
