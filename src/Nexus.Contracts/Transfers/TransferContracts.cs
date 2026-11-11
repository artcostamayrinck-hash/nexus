using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Transfers;

public enum TransferDirection
{
    Upload = 0,
    Download = 1
}

public enum TransferState
{
    Pending = 0,
    InProgress = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public sealed record FileTransferMetadata : IVersionedContract, IValidatableContract
{
    public Guid TransferId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid TargetDeviceId { get; init; }

    [Required]
    [StringLength(260, MinimumLength = 1)]
    public string FileName { get; init; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string ContentType { get; init; } = "application/octet-stream";

    [Range(0, long.MaxValue)]
    public long TotalBytes { get; init; }

    [Range(0, long.MaxValue)]
    public long TransferredBytes { get; init; }

    [StringLength(64)]
    public string? Sha256Checksum { get; init; }

    public TransferDirection Direction { get; init; }
    public TransferState State { get; init; } = TransferState.Pending;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (TransferId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(TransferId), "TRANSFER_ID_EMPTY", "TransferId cannot be empty.");
        }

        if (SourceDeviceId == Guid.Empty || TargetDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SourceDeviceId), "TRANSFER_DEVICE_EMPTY", "Source and Target device IDs are required.");
        }

        if (FileName.IndexOfAny(['/', '\\']) >= 0 || FileName.Contains("..", StringComparison.Ordinal))
        {
            yield return new ContractValidationError(nameof(FileName), "TRANSFER_FILENAME_UNSAFE", "FileName must be a clean file name without path traversal.");
        }

        if (TransferredBytes > TotalBytes)
        {
            yield return new ContractValidationError(nameof(TransferredBytes), "TRANSFER_BYTES_EXCEEDED", "TransferredBytes cannot exceed TotalBytes.");
        }

        if (!string.IsNullOrWhiteSpace(Sha256Checksum) && Sha256Checksum.Length != 64)
        {
            yield return new ContractValidationError(nameof(Sha256Checksum), "CHECKSUM_LENGTH_INVALID", "SHA-256 checksum must be 64 hexadecimal characters.");
        }
    }
}

