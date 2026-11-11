namespace Nexus.Contracts.Common;

/// <summary>
/// Supported contract schema versions shared across server, Windows desktop, and Android clients.
/// </summary>
public static class ContractVersions
{
    public const string V1 = "1.0";
    public const string Current = V1;

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        V1
    };

    public static bool IsSupported(string? version)
        => !string.IsNullOrWhiteSpace(version) && Supported.Contains(version.Trim());
}

/// <summary>
/// Marks a contract with an explicit schema version for forward/backward compatibility checks.
/// </summary>
public interface IVersionedContract
{
    string ContractVersion { get; }
}

/// <summary>
/// Marks a contract that participates in distributed correlation tracing.
/// </summary>
public interface ICorrelatedContract
{
    Guid CorrelationId { get; }
}

/// <summary>
/// Allows contracts to declare domain-level invariants beyond attribute annotations.
/// </summary>
public interface IValidatableContract
{
    IEnumerable<ContractValidationError> Validate();
}

