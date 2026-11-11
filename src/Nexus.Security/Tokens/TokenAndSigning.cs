using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Errors;

namespace Nexus.Security.Tokens;

public interface ITokenGenerator
{
    string GenerateToken(int byteLength = 32);
    string HashToken(string rawToken);
    bool VerifyTokenHash(string rawToken, string expectedHash);
}

public sealed class SecureTokenGenerator : ITokenGenerator
{
    public string GenerateToken(int byteLength = 32)
    {
        if (byteLength < 16 || byteLength > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "Token byte length must be between 16 and 256.");
        }

        var buffer = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public string HashToken(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(hash);
    }

    public bool VerifyTokenHash(string rawToken, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actual = Encoding.ASCII.GetBytes(HashToken(rawToken));
        var expected = Encoding.ASCII.GetBytes(expectedHash.Trim().ToLowerInvariant());
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public interface IPayloadSigner
{
    string Sign(string payload, ReadOnlySpan<byte> secretKey);
    Result Verify(string payload, string signatureHex, ReadOnlySpan<byte> secretKey);
}

public sealed class HmacPayloadSigner : IPayloadSigner
{
    public string Sign(string payload, ReadOnlySpan<byte> secretKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (secretKey.Length < 16)
        {
            throw new ArgumentException("Secret key must be at least 16 bytes.", nameof(secretKey));
        }

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(secretKey, payloadBytes);
        return Convert.ToHexStringLower(mac);
    }

    public Result Verify(string payload, string signatureHex, ReadOnlySpan<byte> secretKey)
    {
        if (payload is null || string.IsNullOrWhiteSpace(signatureHex) || secretKey.Length < 16)
        {
            return Result.Failure(NexusError.Validation("Invalid signature verification input.", code: "SIGNATURE_INPUT_INVALID"));
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHex.Trim());
        }
        catch (FormatException)
        {
            return Result.Failure(NexusError.Validation("Signature must be valid hexadecimal.", code: "SIGNATURE_FORMAT_INVALID"));
        }

        var computed = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(payload));
        if (provided.Length != computed.Length || !CryptographicOperations.FixedTimeEquals(provided, computed))
        {
            return Result.Failure(NexusError.Unauthorized("Payload signature verification failed.", "SIGNATURE_MISMATCH"));
        }

        return Result.Success();
    }
}

