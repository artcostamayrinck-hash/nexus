using System.Security.Cryptography;
using System.Text;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;
using Nexus.Security.Credentials;

namespace Nexus.Security.Tokens;

public sealed class DeviceTokenOptions
{
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}

public sealed record AccessTokenClaims(
    string TokenId,
    Guid DeviceId,
    NexusPermission Permissions,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record IssuedTokenResult(
    DeviceAuthTokenPair TokenPair,
    string RefreshTokenHash);

public interface IDeviceTokenService
{
    Task<Result<IssuedTokenResult>> IssueTokenPairAsync(
        Guid deviceId,
        NexusPermission permissions,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<Result<AccessTokenClaims>> ValidateAccessTokenAsync(
        string token,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    string HashRefreshToken(string refreshToken);
}

/// <summary>
/// Issues and validates HMAC-SHA256 signed access tokens and CSPRNG refresh tokens.
/// </summary>
public sealed class DeviceTokenService : IDeviceTokenService
{
    private const string SigningKeyVaultName = "nexus.server.token.hmac.v1";

    private readonly ISecureCredentialStore _credentialStore;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly DeviceTokenOptions _options;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private byte[]? _signingKey;

    public DeviceTokenService(
        ISecureCredentialStore credentialStore,
        ITokenGenerator tokenGenerator,
        DeviceTokenOptions options)
    {
        _credentialStore = credentialStore;
        _tokenGenerator = tokenGenerator;
        _options = options;
    }

    public async Task<Result<IssuedTokenResult>> IssueTokenPairAsync(
        Guid deviceId,
        NexusPermission permissions,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            return Result<IssuedTokenResult>.Failure(NexusError.Validation("DeviceId cannot be empty.", code: "TOKEN_DEVICE_EMPTY"));
        }

        var keyResult = await GetOrCreateSigningKeyAsync(cancellationToken).ConfigureAwait(false);
        if (keyResult.IsFailure)
        {
            return Result<IssuedTokenResult>.Failure(keyResult.Error!);
        }

        var tokenId = _tokenGenerator.GenerateToken(16);
        var accessExpires = nowUtc.Add(_options.AccessTokenLifetime);
        var refreshExpires = nowUtc.Add(_options.RefreshTokenLifetime);

        var claimsPayload = $"{tokenId}|{deviceId:D}|{(int)permissions}|{nowUtc.ToUnixTimeSeconds()}|{accessExpires.ToUnixTimeSeconds()}";
        var encodedClaims = Base64UrlEncode(Encoding.UTF8.GetBytes(claimsPayload));
        var mac = HMACSHA256.HashData(keyResult.Value, Encoding.ASCII.GetBytes($"v1.{encodedClaims}"));
        var accessToken = $"v1.{encodedClaims}.{Base64UrlEncode(mac)}";

        var refreshToken = _tokenGenerator.GenerateToken(48);
        var refreshHash = HashRefreshToken(refreshToken);

        var pair = new DeviceAuthTokenPair
        {
            DeviceId = deviceId,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            AccessTokenExpiresAtUtc = accessExpires,
            RefreshTokenExpiresAtUtc = refreshExpires,
            Permissions = permissions
        };

        return Result<IssuedTokenResult>.Success(new IssuedTokenResult(pair, refreshHash));
    }

    public async Task<Result<AccessTokenClaims>> ValidateAccessTokenAsync(
        string token,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Result<AccessTokenClaims>.Failure(NexusError.Unauthorized("Access token is missing.", "TOKEN_MISSING"));
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != "v1")
        {
            return Result<AccessTokenClaims>.Failure(NexusError.Unauthorized("Access token format is invalid.", "TOKEN_MALFORMED"));
        }

        var keyResult = await GetOrCreateSigningKeyAsync(cancellationToken).ConfigureAwait(false);
        if (keyResult.IsFailure)
        {
            return Result<AccessTokenClaims>.Failure(keyResult.Error!);
        }

        byte[] providedSig;
        byte[] claimsBytes;
        try
        {
            claimsBytes = Base64UrlDecode(parts[1]);
            providedSig = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return Result<AccessTokenClaims>.Failure(NexusError.Unauthorized("Access token encoding is invalid.", "TOKEN_ENCODING_INVALID"));
        }

        var expectedSig = HMACSHA256.HashData(keyResult.Value, Encoding.ASCII.GetBytes($"v1.{parts[1]}"));
        if (providedSig.Length != expectedSig.Length || !CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
        {
            return Result<AccessTokenClaims>.Failure(NexusError.Unauthorized("Access token signature is invalid.", "TOKEN_SIGNATURE_INVALID"));
        }

        var rawClaims = Encoding.UTF8.GetString(claimsBytes).Split('|');
        if (rawClaims.Length != 5 ||
            !Guid.TryParse(rawClaims[1], out var deviceId) ||
            !int.TryParse(rawClaims[2], out var permInt) ||
            !long.TryParse(rawClaims[3], out var iatSec) ||
            !long.TryParse(rawClaims[4], out var expSec))
        {
            return Result<AccessTokenClaims>.Failure(NexusError.Unauthorized("Access token claims are corrupted.", "TOKEN_CLAIMS_INVALID"));
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSec);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSec);

        if (nowUtc >= expiresAt)
        {
            return Result<AccessTokenClaims>.Failure(NexusError.Unauthorized("Access token has expired.", "TOKEN_EXPIRED"));
        }

        return Result<AccessTokenClaims>.Success(new AccessTokenClaims(
            rawClaims[0],
            deviceId,
            (NexusPermission)permInt,
            issuedAt,
            expiresAt));
    }

    public string HashRefreshToken(string refreshToken)
        => _tokenGenerator.HashToken(refreshToken);

    private async Task<Result<byte[]>> GetOrCreateSigningKeyAsync(CancellationToken cancellationToken)
    {
        if (_signingKey is not null)
        {
            return Result<byte[]>.Success(_signingKey);
        }

        await _keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_signingKey is not null)
            {
                return Result<byte[]>.Success(_signingKey);
            }

            var existing = await _credentialStore.GetSecretAsync(SigningKeyVaultName, cancellationToken).ConfigureAwait(false);
            if (existing.IsFailure)
            {
                return Result<byte[]>.Failure(existing.Error!);
            }

            if (existing.Value is { Length: >= 32 })
            {
                _signingKey = existing.Value;
                return Result<byte[]>.Success(_signingKey);
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            var saved = await _credentialStore.SaveSecretAsync(SigningKeyVaultName, generated, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                return Result<byte[]>.Failure(saved.Error!);
            }

            _signingKey = generated;
            return Result<byte[]>.Success(_signingKey);
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }
}

