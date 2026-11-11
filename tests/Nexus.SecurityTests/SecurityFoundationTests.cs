using System.Security.Cryptography;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Pairing;
using Nexus.Security.Replay;
using Nexus.Security.Tls;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.SecurityTests;

public sealed class SecurityFoundationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-sec-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DeviceIdentityService_ComputesDeterministicFingerprintAndVerifiesFixedTime()
    {
        var svc = new DeviceIdentityService();
        var fp1 = svc.ComputeFingerprint("MACHINE-ABC", "Windows");
        var fp2 = svc.ComputeFingerprint("MACHINE-ABC", "windows");

        Assert.Equal(fp1, fp2);
        Assert.True(svc.IsValidFingerprint(fp1));
        Assert.True(svc.ValidateDeviceFingerprint(fp1, fp2).IsSuccess);

        var tampered = new string('0', 64);
        var mismatch = svc.ValidateDeviceFingerprint(fp1, tampered);
        Assert.True(mismatch.IsFailure);
        Assert.Equal(ErrorCategory.Unauthorized, mismatch.Error!.Category);
    }

    [Fact]
    public void SecureTokenGenerator_ProducesUniqueTokensAndVerifiesHash()
    {
        var generator = new SecureTokenGenerator();
        var t1 = generator.GenerateToken();
        var t2 = generator.GenerateToken();

        Assert.NotEqual(t1, t2);
        var hash = generator.HashToken(t1);
        Assert.True(generator.VerifyTokenHash(t1, hash));
        Assert.False(generator.VerifyTokenHash(t2, hash));
    }

    [Fact]
    public void HmacPayloadSigner_SignsAndDetectsTampering()
    {
        var signer = new HmacPayloadSigner();
        var key = RandomNumberGenerator.GetBytes(32);
        var otherKey = RandomNumberGenerator.GetBytes(32);
        const string payload = """{"deviceId":"123","action":"ping"}""";

        var sig = signer.Sign(payload, key);
        Assert.True(signer.Verify(payload, sig, key).IsSuccess);
        Assert.True(signer.Verify(payload + " ", sig, key).IsFailure);
        Assert.True(signer.Verify(payload, sig, otherKey).IsFailure);
        Assert.True(signer.Verify(payload, "not-hex!", key).IsFailure);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\Windows\\System32")]
    [InlineData("C:\\secret.txt")]
    [InlineData("/absolute/path")]
    [InlineData("file\0.txt")]
    public void InputSanitizer_RejectsUnsafePaths(string unsafePath)
    {
        var result = Nexus.Security.Validation.InputSanitizer.SanitizeRelativePath(unsafePath);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Validation, result.Error!.Category);
    }

    [Fact]
    public void InputSanitizer_ValidatesEndpointsSafely()
    {
        Assert.True(Nexus.Security.Validation.InputSanitizer.ValidateEndpointUri("https://192.168.1.50:5280").IsSuccess);
        Assert.True(Nexus.Security.Validation.InputSanitizer.ValidateEndpointUri("http://127.0.0.1:5280").IsSuccess);
        Assert.True(Nexus.Security.Validation.InputSanitizer.ValidateEndpointUri("ftp://files.local").IsFailure);
        Assert.True(Nexus.Security.Validation.InputSanitizer.ValidateEndpointUri("javascript:alert(1)").IsFailure);
    }

    [Fact]
    public async Task CryptographicIdentityProvider_PersistsAcrossRestarts_RenamesAndVerifiesEcdsaSignatures()
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_tempDir, "identity-vault")
        });

        var localCtx = new DefaultLocalDeviceContext(
            Guid.Empty,
            "Workstation-01",
            DevicePlatform.Windows,
            DeviceType.Desktop,
            "Windows 11",
            "0.3.0",
            DeviceCapability.All);

        var clock = new SystemClock();
        var serializer = new SystemTextJsonSerializer();

        var provider1 = new CryptographicIdentityProvider(vault, localCtx, serializer, clock);
        var id1 = await provider1.GetOrCreateIdentityAsync();
        Assert.True(id1.IsSuccess);
        Assert.Equal(64, id1.Value.PublicKeyFingerprint.Length);

        var sig = await provider1.SignStringAsync("canonical-challenge-123");
        Assert.True(sig.IsSuccess);

        // Recreate provider from disk vault (simulating app restart)
        var provider2 = new CryptographicIdentityProvider(vault, localCtx, serializer, clock);
        var id2 = await provider2.GetOrCreateIdentityAsync();
        Assert.True(id2.IsSuccess);
        Assert.Equal(id1.Value.DeviceId, id2.Value.DeviceId);
        Assert.Equal(id1.Value.PublicKeyFingerprint, id2.Value.PublicKeyFingerprint);
        Assert.Equal(id1.Value.PublicKeySpkiBase64, id2.Value.PublicKeySpkiBase64);

        Assert.True(provider2.VerifySignature(id2.Value.PublicKeySpkiBase64, "canonical-challenge-123", sig.Value).IsSuccess);
        Assert.True(provider2.VerifySignature(id2.Value.PublicKeySpkiBase64, "tampered-payload", sig.Value).IsFailure);

        var renamed = await provider2.RenameLocalDeviceAsync("Workstation Principal");
        Assert.True(renamed.IsSuccess);
        Assert.Equal("Workstation Principal", renamed.Value.CustomName);
        Assert.Equal(id1.Value.DeviceId, renamed.Value.DeviceId);
        Assert.Equal(id1.Value.PublicKeyFingerprint, renamed.Value.PublicKeyFingerprint);
    }

    [Fact]
    public async Task PlatformSecureCredentialStore_EncryptsAtRestAndDeletesCleanly()
    {
        var vaultDir = Path.Combine(_tempDir, "secrets-vault");
        ISecureCredentialStore store = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions { VaultDirectory = vaultDir });

        const string plaintext = "super-secret-refresh-token-xyz";
        var saved = await store.SaveStringAsync("mobile.tokens", plaintext);
        Assert.True(saved.IsSuccess);
        Assert.True((await store.ExistsAsync("mobile.tokens")).Value);

        // Ensure ciphertext on disk does not contain plaintext ASCII
        var files = Directory.GetFiles(vaultDir);
        Assert.Single(files);
        var rawDiskText = await File.ReadAllTextAsync(files[0]);
        Assert.DoesNotContain(plaintext, rawDiskText, StringComparison.Ordinal);

        ISecureCredentialStore store2 = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions { VaultDirectory = vaultDir });
        var loaded = await store2.GetStringAsync("mobile.tokens");
        Assert.True(loaded.IsSuccess);
        Assert.Equal(plaintext, loaded.Value);

        Assert.True((await store2.DeleteSecretAsync("mobile.tokens")).Value);
        Assert.False((await store2.ExistsAsync("mobile.tokens")).Value);
    }

    [Fact]
    public async Task DeviceTokenService_EnforcesExpirationAndTamperDetection()
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_tempDir, "token-vault")
        });
        var tokenService = new DeviceTokenService(
            vault,
            new SecureTokenGenerator(),
            new DeviceTokenOptions
            {
                AccessTokenLifetime = TimeSpan.FromMinutes(10),
                RefreshTokenLifetime = TimeSpan.FromDays(7)
            });

        var issuedAt = DateTimeOffset.UtcNow;
        var deviceId = Guid.CreateVersion7();
        var issued = await tokenService.IssueTokenPairAsync(
            deviceId,
            NexusPermission.ViewStatus | NexusPermission.ManageDevices,
            issuedAt);

        Assert.True(issued.IsSuccess);
        Assert.StartsWith("v1.", issued.Value.TokenPair.AccessToken, StringComparison.Ordinal);

        var validCheck = await tokenService.ValidateAccessTokenAsync(issued.Value.TokenPair.AccessToken, issuedAt.AddMinutes(5));
        Assert.True(validCheck.IsSuccess);
        Assert.Equal(deviceId, validCheck.Value.DeviceId);
        Assert.True(validCheck.Value.Permissions.HasFlag(NexusPermission.ManageDevices));

        var expiredCheck = await tokenService.ValidateAccessTokenAsync(issued.Value.TokenPair.AccessToken, issuedAt.AddMinutes(11));
        Assert.True(expiredCheck.IsFailure);
        Assert.Equal("TOKEN_EXPIRED", expiredCheck.Error!.Code);

        var tampered = issued.Value.TokenPair.AccessToken[..^4] + "AAAA";
        var tamperedCheck = await tokenService.ValidateAccessTokenAsync(tampered, issuedAt.AddMinutes(1));
        Assert.True(tamperedCheck.IsFailure);
        Assert.Equal(ErrorCategory.Unauthorized, tamperedCheck.Error!.Category);
    }

    [Fact]
    public void NonceReplayProtector_RejectsReusedNonceAndStaleTimestamp()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var protector = new InMemoryNonceReplayProtector(clock, TimeSpan.FromMinutes(5));
        var deviceId = Guid.CreateVersion7();
        const string nonce = "nonce-unique-1234567890";

        Assert.True(protector.ValidateAndRecord("pairing.claim", deviceId, clock.UtcNow, nonce).IsSuccess);

        var replay = protector.ValidateAndRecord("pairing.claim", deviceId, clock.UtcNow, nonce);
        Assert.True(replay.IsFailure);
        Assert.Equal("REPLAY_NONCE_DETECTED", replay.Error!.Code);

        var stale = protector.ValidateAndRecord("pairing.claim", deviceId, clock.UtcNow.AddMinutes(-10), "nonce-fresh-1234567890");
        Assert.True(stale.IsFailure);
        Assert.Equal("REPLAY_TIMESTAMP_SKEW", stale.Error!.Code);
    }

    [Fact]
    public async Task LocalTlsCertificateManager_GeneratesPersistentCertAndPinnedHandler()
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_tempDir, "tls-vault")
        });
        var tlsManager = new LocalTlsCertificateManager(vault, new SecureTokenGenerator());

        var cert1 = await tlsManager.GetOrCreateServerCertificateAsync("nexus-host.local");
        var cert2 = await tlsManager.GetOrCreateServerCertificateAsync("nexus-host.local");

        Assert.True(cert1.IsSuccess);
        Assert.True(cert2.IsSuccess);

        var tp1 = tlsManager.GetSha256Thumbprint(cert1.Value);
        var tp2 = tlsManager.GetSha256Thumbprint(cert2.Value);
        Assert.Equal(64, tp1.Length);
        Assert.Equal(tp1, tp2);

        using var handler = LocalTlsCertificateManager.CreatePinnedTlsHandler(tp1);
        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void QrCodeGeneratorService_ProducesValidPngBytes()
    {
        var qrService = new QrCodeGeneratorService();
        var bytes = qrService.GeneratePng("""{"pairingId":"123","secret":"abc"}""", 6);

        Assert.True(bytes.Length > 100);
        // PNG magic signature: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
