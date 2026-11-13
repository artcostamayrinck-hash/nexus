using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;
using Nexus.Contracts.Messaging;

namespace Nexus.Contracts.Commands;

public enum CommandPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

public enum CommandExecutionState
{
    Queued = 0,
    Dispatched = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
    TimedOut = 6
}

public sealed record DeviceCommand : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid TargetDeviceId { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string CommandName { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();
    public CommandPriority Priority { get; init; } = CommandPriority.Normal;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public DateTimeOffset IssuedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (CommandId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CommandId), "COMMAND_ID_EMPTY", "CommandId cannot be empty.");
        }

        if (SourceDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SourceDeviceId), "SOURCE_DEVICE_ID_EMPTY", "SourceDeviceId cannot be empty.");
        }

        if (TargetDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(TargetDeviceId), "TARGET_DEVICE_ID_EMPTY", "TargetDeviceId cannot be empty.");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            yield return new ContractValidationError(nameof(Timeout), "COMMAND_TIMEOUT_INVALID", "Command timeout must be greater than zero.");
        }
    }
}

public sealed record CommandExecutionResult : IVersionedContract, IValidatableContract
{
    public Guid CommandId { get; init; }
    public Guid TargetDeviceId { get; init; }
    public CommandExecutionState State { get; init; }
    public int? ExitCode { get; init; }

    [StringLength(4096)]
    public string? Output { get; init; }

    public ContractError? Error { get; init; }
    public DateTimeOffset FinishedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (CommandId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CommandId), "COMMAND_ID_EMPTY", "CommandId cannot be empty.");
        }

        if (TargetDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(TargetDeviceId), "TARGET_DEVICE_ID_EMPTY", "TargetDeviceId cannot be empty.");
        }

        if (State == CommandExecutionState.Failed && Error is null)
        {
            yield return new ContractValidationError(nameof(Error), "FAILED_COMMAND_MISSING_ERROR", "Failed command result must include Error details.");
        }
    }
}

