using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Messaging;

public sealed record ContractError(
    string Code,
    string Message,
    string? Detail = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null);

public sealed record NexusRequest<TPayload> : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid? TargetDeviceId { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string Action { get; init; } = string.Empty;

    public TPayload? Payload { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan? Timeout { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (RequestId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(RequestId), "REQUEST_ID_EMPTY", "RequestId cannot be empty.");
        }

        if (CorrelationId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CorrelationId), "CORRELATION_ID_EMPTY", "CorrelationId cannot be empty.");
        }

        if (SourceDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SourceDeviceId), "SOURCE_DEVICE_ID_EMPTY", "SourceDeviceId cannot be empty.");
        }

        if (Timeout.HasValue && Timeout.Value <= TimeSpan.Zero)
        {
            yield return new ContractValidationError(nameof(Timeout), "TIMEOUT_NON_POSITIVE", "Timeout must be greater than zero.");
        }
    }
}

public sealed record NexusResponse<TPayload> : IVersionedContract, ICorrelatedContract
{
    public Guid ResponseId { get; init; } = Guid.NewGuid();
    public Guid RequestId { get; init; }
    public Guid CorrelationId { get; init; }
    public bool IsSuccess { get; init; }
    public TPayload? Payload { get; init; }
    public ContractError? Error { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public static NexusResponse<TPayload> Success(
        TPayload payload,
        Guid requestId = default,
        Guid correlationId = default,
        DateTimeOffset? completedAtUtc = null)
    {
        return new NexusResponse<TPayload>
        {
            RequestId = requestId == Guid.Empty ? Guid.NewGuid() : requestId,
            CorrelationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
            IsSuccess = true,
            Payload = payload,
            CompletedAtUtc = completedAtUtc ?? DateTimeOffset.UtcNow
        };
    }

    public static NexusResponse<TPayload> Failure(
        ContractError error,
        Guid requestId = default,
        Guid correlationId = default,
        DateTimeOffset? completedAtUtc = null)
    {
        return new NexusResponse<TPayload>
        {
            RequestId = requestId == Guid.Empty ? Guid.NewGuid() : requestId,
            CorrelationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
            IsSuccess = false,
            Error = error,
            CompletedAtUtc = completedAtUtc ?? DateTimeOffset.UtcNow
        };
    }
}

