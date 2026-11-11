using System.ComponentModel.DataAnnotations;

namespace Nexus.Contracts.Common;

public sealed record ContractValidationError(
    string PropertyName,
    string Code,
    string Message);

public sealed record ContractValidationResult(
    bool IsValid,
    IReadOnlyList<ContractValidationError> Errors)
{
    public static ContractValidationResult Success()
        => new(true, Array.Empty<ContractValidationError>());

    public static ContractValidationResult Failure(IEnumerable<ContractValidationError> errors)
    {
        var list = errors.ToList().AsReadOnly();
        return new ContractValidationResult(list.Count == 0, list);
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary()
    {
        return Errors
            .GroupBy(e => string.IsNullOrWhiteSpace(e.PropertyName) ? "_" : e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.Message).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }
}

/// <summary>
/// Validates shared contracts using DataAnnotations, contract version checks, and domain invariants.
/// </summary>
public static class ContractValidator
{
    public static ContractValidationResult Validate(object? instance)
    {
        if (instance is null)
        {
            return ContractValidationResult.Failure([
                new ContractValidationError("Contract", "CONTRACT_NULL", "The contract instance cannot be null.")
            ]);
        }

        var errors = new List<ContractValidationError>();

        if (instance is IVersionedContract versioned && !ContractVersions.IsSupported(versioned.ContractVersion))
        {
            errors.Add(new ContractValidationError(
                nameof(IVersionedContract.ContractVersion),
                "UNSUPPORTED_CONTRACT_VERSION",
                $"Contract version '{versioned.ContractVersion}' is not supported. Supported versions: {ContractVersions.V1}."));
        }

        var context = new ValidationContext(instance);
        var annotationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(instance, context, annotationResults, validateAllProperties: true))
        {
            foreach (var result in annotationResults)
            {
                var members = result.MemberNames.Any() ? result.MemberNames : ["_"];
                foreach (var member in members)
                {
                    errors.Add(new ContractValidationError(
                        member,
                        "ANNOTATION_VALIDATION_FAILED",
                        result.ErrorMessage ?? $"Validation failed for '{member}'."));
                }
            }
        }

        if (instance is IValidatableContract validatable)
        {
            errors.AddRange(validatable.Validate());
        }

        return errors.Count == 0
            ? ContractValidationResult.Success()
            : ContractValidationResult.Failure(errors);
    }
}

public sealed record PagedRequest
{
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 500)]
    public int PageSize { get; init; } = 50;
}

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    string ContractVersion = ContractVersions.Current) : IVersionedContract;

