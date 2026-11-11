using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Errors;

namespace Nexus.Security.Identity;

public interface IDeviceIdentityService
{
    string ComputeFingerprint(string machineIdentifier, string platformIdentifier);
    bool IsValidFingerprint(string? fingerprint);
    Result ValidateDeviceFingerprint(string? expectedFingerprint, string? candidateFingerprint);
}

public sealed class DeviceIdentityService : IDeviceIdentityService
{
    public string ComputeFingerprint(string machineIdentifier, string platformIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformIdentifier);

        var canonical = $"{platformIdentifier.Trim().ToUpperInvariant()}::{machineIdentifier.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    public bool IsValidFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64)
        {
            return false;
        }

        foreach (var ch in fingerprint)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    public Result ValidateDeviceFingerprint(string? expectedFingerprint, string? candidateFingerprint)
    {
        if (!IsValidFingerprint(expectedFingerprint) || !IsValidFingerprint(candidateFingerprint))
        {
            return Result.Failure(NexusError.Validation("Device fingerprint format is invalid.", code: "FINGERPRINT_INVALID"));
        }

        var expectedBytes = Encoding.ASCII.GetBytes(expectedFingerprint!.ToLowerInvariant());
        var candidateBytes = Encoding.ASCII.GetBytes(candidateFingerprint!.ToLowerInvariant());

        return CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes)
            ? Result.Success()
            : Result.Failure(NexusError.Unauthorized("Device fingerprint mismatch.", "FINGERPRINT_MISMATCH"));
    }
}

