using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Errors;

namespace Nexus.Security.Credentials;

public sealed class SecureCredentialStoreOptions
{
    public string VaultDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXUS",
        "vault");
}

public interface ISecureCredentialStore
{
    Task<Result> SaveSecretAsync(string key, byte[] secretBytes, CancellationToken cancellationToken = default);
    Task<Result<byte[]?>> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task<Result<bool>> DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
    Task<Result<bool>> ExistsAsync(string key, CancellationToken cancellationToken = default);

    async Task<Result> SaveStringAsync(string key, string secretText, CancellationToken cancellationToken = default)
        => await SaveSecretAsync(key, Encoding.UTF8.GetBytes(secretText), cancellationToken).ConfigureAwait(false);

    async Task<Result<string?>> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await GetSecretAsync(key, cancellationToken).ConfigureAwait(false);
        return bytes.IsFailure
            ? Result<string?>.Failure(bytes.Error!)
            : Result<string?>.Success(bytes.Value is null ? null : Encoding.UTF8.GetString(bytes.Value));
    }
}

/// <summary>
/// Persists sensitive material encrypted at rest using Windows DPAPI (ProtectedData) or AES-256-GCM fallback.
/// </summary>
public sealed class PlatformSecureCredentialStore : ISecureCredentialStore
{
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("NEXUS.Vault.v1");
    private readonly SecureCredentialStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlatformSecureCredentialStore(SecureCredentialStoreOptions options)
    {
        _options = options;
    }

    public async Task<Result> SaveSecretAsync(string key, byte[] secretBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Result.Failure(NexusError.Validation("Credential key cannot be empty.", code: "VAULT_KEY_EMPTY"));
        }

        ArgumentNullException.ThrowIfNull(secretBytes);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_options.VaultDirectory);
            var filePath = GetSecretFilePath(key);
            var protectedPayload = ProtectBytes(secretBytes);
            await File.WriteAllBytesAsync(filePath, protectedPayload, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure(NexusError.Storage("Failed to persist encrypted credential.", "VAULT_WRITE_FAILED", ex.Message, ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<byte[]?>> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Result<byte[]?>.Failure(NexusError.Validation("Credential key cannot be empty.", code: "VAULT_KEY_EMPTY"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetSecretFilePath(key);
            if (!File.Exists(filePath))
            {
                return Result<byte[]?>.Success(null);
            }

            var protectedPayload = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var plain = UnprotectBytes(protectedPayload);
            return Result<byte[]?>.Success(plain);
        }
        catch (CryptographicException ex)
        {
            return Result<byte[]?>.Failure(NexusError.Unauthorized("Stored credential could not be decrypted.", "VAULT_DECRYPT_FAILED", ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<byte[]?>.Failure(NexusError.Storage("Failed to read encrypted credential.", "VAULT_READ_FAILED", ex.Message, ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<bool>> DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Result<bool>.Failure(NexusError.Validation("Credential key cannot be empty.", code: "VAULT_KEY_EMPTY"));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetSecretFilePath(key);
            if (!File.Exists(filePath))
            {
                return Result<bool>.Success(false);
            }

            File.Delete(filePath);
            return Result<bool>.Success(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<Result<bool>> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Task.FromResult(Result<bool>.Failure(NexusError.Validation("Credential key cannot be empty.", code: "VAULT_KEY_EMPTY")));
        }

        return Task.FromResult(Result<bool>.Success(File.Exists(GetSecretFilePath(key))));
    }

    private string GetSecretFilePath(string key)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));
        return Path.Combine(_options.VaultDirectory, $"{hash}.bin");
    }

    private byte[] ProtectBytes(byte[] plaintext)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectWindowsDpapi(plaintext);
        }

        var masterKey = GetOrCreateFallbackMasterKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(masterKey, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[1 + 12 + 16 + ciphertext.Length];
        output[0] = 0x02; // AES-GCM format marker
        Buffer.BlockCopy(nonce, 0, output, 1, 12);
        Buffer.BlockCopy(tag, 0, output, 13, 16);
        Buffer.BlockCopy(ciphertext, 0, output, 29, ciphertext.Length);
        return output;
    }

    private byte[] UnprotectBytes(byte[] protectedPayload)
    {
        if (OperatingSystem.IsWindows())
        {
            return UnprotectWindowsDpapi(protectedPayload);
        }

        if (protectedPayload.Length < 29 || protectedPayload[0] != 0x02)
        {
            throw new CryptographicException("Invalid AES-GCM vault payload.");
        }

        var masterKey = GetOrCreateFallbackMasterKey();
        var nonce = protectedPayload.AsSpan(1, 12);
        var tag = protectedPayload.AsSpan(13, 16);
        var ciphertext = protectedPayload.AsSpan(29);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(masterKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindowsDpapi(byte[] plaintext)
        => ProtectedData.Protect(plaintext, DpapiEntropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindowsDpapi(byte[] protectedPayload)
        => ProtectedData.Unprotect(protectedPayload, DpapiEntropy, DataProtectionScope.CurrentUser);

    private byte[] GetOrCreateFallbackMasterKey()
    {
        Directory.CreateDirectory(_options.VaultDirectory);
        var keyFile = Path.Combine(_options.VaultDirectory, ".vault.key");
        if (File.Exists(keyFile))
        {
            return File.ReadAllBytes(keyFile);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyFile, key);
        return key;
    }
}

