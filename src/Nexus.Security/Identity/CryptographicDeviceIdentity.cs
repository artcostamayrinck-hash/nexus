using System.Security.Cryptography;
using System.Text;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Security.Credentials;

namespace Nexus.Security.Identity;

public interface ICryptographicIdentityProvider
{
    Task<Result<DeviceCryptographicIdentity>> GetOrCreateIdentityAsync(CancellationToken cancellationToken = default);
    Task<Result<DeviceCryptographicIdentity>> RenameLocalDeviceAsync(string newName, CancellationToken cancellationToken = default);
    Task<Result<string>> SignStringAsync(string canonicalPayload, CancellationToken cancellationToken = default);
    Result VerifySignature(string publicKeySpkiBase64, string canonicalPayload, string signatureBase64);
    string ComputeShortVerificationCode(string hostFingerprint, string clientFingerprint, string challengeNonce);
}

internal sealed record PersistedIdentityMaterial(
    Guid DeviceId,
    string CustomName,
    DateTimeOffset RegisteredAtUtc,
    string PrivateKeyPkcs8Base64,
    string PublicKeySpkiBase64,
    string PublicKeyFingerprint);

/// <summary>
/// Manages each installation's persistent ECDSA P-256 identity and cryptographic signing/verification.
/// </summary>
public sealed class CryptographicIdentityProvider : ICryptographicIdentityProvider
{
    private const string IdentityVaultKey = "nexus.local.identity.v1";

    private readonly ISecureCredentialStore _credentialStore;
    private readonly ILocalDeviceContext _localDevice;
    private readonly IJsonSerializer _serializer;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PersistedIdentityMaterial? _cached;

    public CryptographicIdentityProvider(
        ISecureCredentialStore credentialStore,
        ILocalDeviceContext localDevice,
        IJsonSerializer serializer,
        IClock clock)
    {
        _credentialStore = credentialStore;
        _localDevice = localDevice;
        _serializer = serializer;
        _clock = clock;
    }

    public async Task<Result<DeviceCryptographicIdentity>> GetOrCreateIdentityAsync(CancellationToken cancellationToken = default)
    {
        var materialResult = await GetOrCreateMaterialAsync(cancellationToken).ConfigureAwait(false);
        return materialResult.IsFailure
            ? Result<DeviceCryptographicIdentity>.Failure(materialResult.Error!)
            : Result<DeviceCryptographicIdentity>.Success(ToContract(materialResult.Value));
    }

    public async Task<Result<DeviceCryptographicIdentity>> RenameLocalDeviceAsync(string newName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim().Length < 2)
        {
            return Result<DeviceCryptographicIdentity>.Failure(
                NexusError.Validation("Device name must have at least 2 characters.", code: "DEVICE_NAME_INVALID"));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadOrInitializeUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current.IsFailure)
            {
                return Result<DeviceCryptographicIdentity>.Failure(current.Error!);
            }

            var updated = current.Value with { CustomName = newName.Trim() };
            var saved = await _credentialStore.SaveStringAsync(IdentityVaultKey, _serializer.Serialize(updated), cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                return Result<DeviceCryptographicIdentity>.Failure(saved.Error!);
            }

            _cached = updated;
            return Result<DeviceCryptographicIdentity>.Success(ToContract(updated));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<string>> SignStringAsync(string canonicalPayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);

        var material = await GetOrCreateMaterialAsync(cancellationToken).ConfigureAwait(false);
        if (material.IsFailure)
        {
            return Result<string>.Failure(material.Error!);
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(material.Value.PrivateKeyPkcs8Base64), out _);
        var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(canonicalPayload), HashAlgorithmName.SHA256);
        return Result<string>.Success(Convert.ToBase64String(signature));
    }

    public Result VerifySignature(string publicKeySpkiBase64, string canonicalPayload, string signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64) ||
            string.IsNullOrWhiteSpace(canonicalPayload) ||
            string.IsNullOrWhiteSpace(signatureBase64))
        {
            return Result.Failure(NexusError.Validation("Missing public key, payload, or signature.", code: "ECDSA_INPUT_INVALID"));
        }

        try
        {
            var spki = Convert.FromBase64String(publicKeySpkiBase64.Trim());
            var signature = Convert.FromBase64String(signatureBase64.Trim());

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);

            var valid = ecdsa.VerifyData(Encoding.UTF8.GetBytes(canonicalPayload), signature, HashAlgorithmName.SHA256);
            return valid
                ? Result.Success()
                : Result.Failure(NexusError.Unauthorized("ECDSA device signature verification failed.", "ECDSA_SIGNATURE_INVALID"));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return Result.Failure(NexusError.Unauthorized("Malformed cryptographic key or signature.", "ECDSA_MALFORMED", ex.Message));
        }
    }

    public string ComputeShortVerificationCode(string hostFingerprint, string clientFingerprint, string challengeNonce)
    {
        var input = $"{hostFingerprint.ToLowerInvariant()}|{clientFingerprint.ToLowerInvariant()}|{challengeNonce}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var numeric = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        return $"{numeric / 1000:D3}-{numeric % 1000:D3}";
    }

    public static string ComputePublicKeyFingerprint(byte[] spkiBytes)
        => Convert.ToHexStringLower(SHA256.HashData(spkiBytes));

    private async Task<Result<PersistedIdentityMaterial>> GetOrCreateMaterialAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return Result<PersistedIdentityMaterial>.Success(_cached);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadOrInitializeUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<PersistedIdentityMaterial>> LoadOrInitializeUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return Result<PersistedIdentityMaterial>.Success(_cached);
        }

        var existing = await _credentialStore.GetStringAsync(IdentityVaultKey, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<PersistedIdentityMaterial>.Failure(existing.Error!);
        }

        if (!string.IsNullOrWhiteSpace(existing.Value))
        {
            var parsed = _serializer.Deserialize<PersistedIdentityMaterial>(existing.Value);
            if (parsed is not null && parsed.DeviceId != Guid.Empty)
            {
                _cached = parsed;
                return Result<PersistedIdentityMaterial>.Success(parsed);
            }
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        var spki = ecdsa.ExportSubjectPublicKeyInfo();

        var material = new PersistedIdentityMaterial(
            DeviceId: _localDevice.DeviceId,
            CustomName: _localDevice.DeviceName,
            RegisteredAtUtc: _clock.UtcNow,
            PrivateKeyPkcs8Base64: Convert.ToBase64String(pkcs8),
            PublicKeySpkiBase64: Convert.ToBase64String(spki),
            PublicKeyFingerprint: ComputePublicKeyFingerprint(spki));

        var save = await _credentialStore.SaveStringAsync(IdentityVaultKey, _serializer.Serialize(material), cancellationToken).ConfigureAwait(false);
        if (save.IsFailure)
        {
            return Result<PersistedIdentityMaterial>.Failure(save.Error!);
        }

        _cached = material;
        return Result<PersistedIdentityMaterial>.Success(material);
    }

    private DeviceCryptographicIdentity ToContract(PersistedIdentityMaterial m)
    {
        return new DeviceCryptographicIdentity
        {
            DeviceId = m.DeviceId,
            CustomName = m.CustomName,
            Platform = _localDevice.Platform,
            DeviceType = _localDevice.DeviceType,
            AppVersion = _localDevice.AppVersion,
            OperatingSystemVersion = _localDevice.OperatingSystemVersion,
            PublicKeySpkiBase64 = m.PublicKeySpkiBase64,
            PublicKeyFingerprint = m.PublicKeyFingerprint,
            RegisteredAtUtc = m.RegisteredAtUtc
        };
    }
}

/// <summary>
/// Canonical string builders ensuring host, client, and server sign and verify identical message formats.
/// </summary>
public static class PairingSignaturePayloads
{
    public static string ForCreatePairing(Guid hostDeviceId, string serverEndpoint, DateTimeOffset timestampUtc, string nonce)
        => $"CREATE|{hostDeviceId:D}|{serverEndpoint.Trim()}|{timestampUtc.ToUnixTimeSeconds()}|{nonce}";

    public static string ForQrPayload(PairingQrPayload qr)
        => $"QR|{qr.PairingId:D}|{qr.HostDeviceId:D}|{qr.HostPublicKeyFingerprint}|{qr.ServerEndpoint}|{qr.ChallengeNonce}|{qr.OneTimeSecret}|{qr.IssuedAtUtc.ToUnixTimeSeconds()}|{qr.ExpiresAtUtc.ToUnixTimeSeconds()}";

    public static string ForPairingClaim(SubmitPairingClaimRequest claim)
        => $"CLAIM|{claim.PairingId:D}|{claim.ChallengeNonce}|{claim.OneTimeSecret}|{claim.ClientDeviceId:D}|{claim.ClientPublicKeyFingerprint}|{claim.ClientTimestampUtc.ToUnixTimeSeconds()}|{claim.ClientNonce}";

    public static string ForConfirmPairing(Guid pairingId, Guid hostDeviceId, bool approve, NexusPermission permissions, DateTimeOffset timestampUtc, string nonce)
        => $"CONFIRM|{pairingId:D}|{hostDeviceId:D}|{approve}|{(int)permissions}|{timestampUtc.ToUnixTimeSeconds()}|{nonce}";

    public static string ForFinalizeTokens(Guid pairingId, Guid clientDeviceId, DateTimeOffset timestampUtc, string nonce)
        => $"FINALIZE|{pairingId:D}|{clientDeviceId:D}|{timestampUtc.ToUnixTimeSeconds()}|{nonce}";

    public static string ForRefreshToken(Guid deviceId, string refreshToken, DateTimeOffset timestampUtc, string nonce)
        => $"REFRESH|{deviceId:D}|{refreshToken}|{timestampUtc.ToUnixTimeSeconds()}|{nonce}";
}

