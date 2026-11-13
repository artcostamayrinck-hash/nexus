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

public enum TransferDuplicatePolicy
{
    Rename = 0,
    Overwrite = 1,
    Skip = 2
}

public enum TransferStage
{
    Queued = 0,
    Uploading = 1,
    ReadyForDownload = 2,
    Downloading = 3,
    Verifying = 4,
    Completed = 5,
    Paused = 6,
    Failed = 7,
    Cancelled = 8
}

public enum TransferFailureReason
{
    None = 0,
    ValidationFailed = 1,
    UnauthorizedOrPermissionDenied = 2,
    InsufficientDiskSpace = 3,
    IntegrityChecksumMismatch = 4,
    NetworkInterrupted = 5,
    DuplicateSkipped = 6,
    FileNotFound = 7,
    CancelledByUser = 8,
    StorageFailure = 9
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

public sealed record FileTransferRecord : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid TransferId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public string SourceDeviceName { get; init; } = string.Empty;
    public Guid TargetDeviceId { get; init; }
    public string TargetDeviceName { get; init; } = string.Empty;

    [Required]
    [StringLength(260, MinimumLength = 1)]
    public string OriginalFileName { get; init; } = string.Empty;

    [Required]
    [StringLength(260, MinimumLength = 1)]
    public string SafeFileName { get; init; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string ContentType { get; init; } = "application/octet-stream";

    [Range(0, long.MaxValue)]
    public long TotalBytes { get; init; }

    [Range(0, long.MaxValue)]
    public long TransferredBytes { get; init; }

    [Range(0, long.MaxValue)]
    public long ServerUploadedBytes { get; init; }

    [StringLength(64)]
    public string Sha256Checksum { get; init; } = string.Empty;

    public TransferDirection Direction { get; init; }
    public TransferState State { get; init; } = TransferState.Pending;
    public TransferStage Stage { get; init; } = TransferStage.Queued;
    public TransferDuplicatePolicy DuplicatePolicy { get; init; } = TransferDuplicatePolicy.Rename;
    public TransferFailureReason FailureReason { get; init; } = TransferFailureReason.None;
    public string? FailureMessage { get; init; }
    public string? LocalFilePath { get; init; }
    public double BytesPerSecond { get; init; }
    public int ChunkSizeBytes { get; init; } = 256 * 1024;
    public bool AutoExecute { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public double PercentComplete => TotalBytes <= 0
        ? (State == TransferState.Completed ? 100d : 0d)
        : Math.Clamp(Math.Round((double)TransferredBytes / TotalBytes * 100d, 2), 0d, 100d);

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

        if (HasUnsafeFileName(OriginalFileName) || HasUnsafeFileName(SafeFileName))
        {
            yield return new ContractValidationError(nameof(OriginalFileName), "TRANSFER_FILENAME_UNSAFE", "File names must not contain path separators or traversal sequences.");
        }

        if (TransferredBytes > TotalBytes || ServerUploadedBytes > TotalBytes)
        {
            yield return new ContractValidationError(nameof(TransferredBytes), "TRANSFER_BYTES_EXCEEDED", "TransferredBytes cannot exceed TotalBytes.");
        }

        if (string.IsNullOrWhiteSpace(Sha256Checksum) || Sha256Checksum.Length != 64)
        {
            yield return new ContractValidationError(nameof(Sha256Checksum), "CHECKSUM_LENGTH_INVALID", "SHA-256 checksum must be 64 hexadecimal characters.");
        }

        if (ChunkSizeBytes < 4096 || ChunkSizeBytes > 8 * 1024 * 1024)
        {
            yield return new ContractValidationError(nameof(ChunkSizeBytes), "CHUNK_SIZE_INVALID", "ChunkSizeBytes must be between 4 KB and 8 MB.");
        }

        if (AutoExecute)
        {
            yield return new ContractValidationError(nameof(AutoExecute), "AUTO_EXECUTE_FORBIDDEN", "Automatic execution of transferred files is forbidden.");
        }
    }

    private static bool HasUnsafeFileName(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny(['/', '\\', '\0']) >= 0
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal);
    }
}

public sealed record FileTransferProgressEvent : IVersionedContract, ICorrelatedContract
{
    public Guid TransferId { get; init; }
    public Guid CorrelationId { get; init; }
    public Guid SourceDeviceId { get; init; }
    public Guid TargetDeviceId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long TransferredBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public double PercentComplete { get; init; }
    public TransferState State { get; init; }
    public TransferStage Stage { get; init; }
    public TransferFailureReason FailureReason { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record InitiateFileTransferRequest : IVersionedContract, ICorrelatedContract, IValidatableContract
{
    public Guid TransferId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public string SourceDeviceName { get; init; } = string.Empty;
    public Guid TargetDeviceId { get; init; }

    [Required]
    [StringLength(260, MinimumLength = 1)]
    public string FileName { get; init; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string ContentType { get; init; } = "application/octet-stream";

    [Range(0, long.MaxValue)]
    public long TotalBytes { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 64)]
    public string Sha256Checksum { get; init; } = string.Empty;

    public int ChunkSizeBytes { get; init; } = 256 * 1024;
    public TransferDuplicatePolicy DuplicatePolicy { get; init; } = TransferDuplicatePolicy.Rename;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (TransferId == Guid.Empty || SourceDeviceId == Guid.Empty || TargetDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(TransferId), "TRANSFER_IDS_REQUIRED", "TransferId, SourceDeviceId, and TargetDeviceId are required.");
        }

        if (SourceDeviceId == TargetDeviceId)
        {
            yield return new ContractValidationError(nameof(TargetDeviceId), "TRANSFER_SAME_DEVICE", "Source and Target device cannot be identical.");
        }

        if (string.IsNullOrWhiteSpace(FileName)
            || FileName.IndexOfAny(['/', '\\', '\0']) >= 0
            || FileName.Contains("..", StringComparison.Ordinal)
            || FileName.Contains(':', StringComparison.Ordinal))
        {
            yield return new ContractValidationError(nameof(FileName), "TRANSFER_FILENAME_UNSAFE", "FileName contains disallowed path or drive characters.");
        }

        if (string.IsNullOrWhiteSpace(Sha256Checksum) || Sha256Checksum.Length != 64)
        {
            yield return new ContractValidationError(nameof(Sha256Checksum), "CHECKSUM_LENGTH_INVALID", "SHA-256 checksum must be 64 hexadecimal characters.");
        }

        if (ChunkSizeBytes < 4096 || ChunkSizeBytes > 8 * 1024 * 1024)
        {
            yield return new ContractValidationError(nameof(ChunkSizeBytes), "CHUNK_SIZE_INVALID", "ChunkSizeBytes must be between 4 KB and 8 MB.");
        }
    }
}

public sealed record InitiateFileTransferResponse : IVersionedContract
{
    public Guid TransferId { get; init; }
    public string SafeFileName { get; init; } = string.Empty;
    public long AcceptedOffsetBytes { get; init; }
    public int ChunkSizeBytes { get; init; }
    public TransferState State { get; init; }
    public TransferStage Stage { get; init; }
    public bool AlreadyUploadedOnServer { get; init; }
    public long ServerAvailableFreeBytes { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record ChunkUploadResponse : IVersionedContract
{
    public Guid TransferId { get; init; }
    public long UploadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double PercentComplete { get; init; }
    public TransferStage Stage { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record AcknowledgeFileTransferRequest : IVersionedContract
{
    public Guid TransferId { get; init; }
    public bool Succeeded { get; init; }
    public string? VerifiedSha256 { get; init; }
    public long ReceivedBytes { get; init; }
    public TransferFailureReason FailureReason { get; init; }
    public string? FailureMessage { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record TransferControlRequest : IVersionedContract
{
    public Guid TransferId { get; init; }
    public string? Reason { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record IncomingTransfersResponse : IVersionedContract
{
    public IReadOnlyList<FileTransferRecord> Transfers { get; init; } = [];
    public DateTimeOffset ServerUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record TransferQueueOptions
{
    public int MaxConcurrentTransfers { get; init; } = 2;
    public int DefaultChunkSizeBytes { get; init; } = 256 * 1024;
    public TransferDuplicatePolicy DefaultDuplicatePolicy { get; init; } = TransferDuplicatePolicy.Rename;
    public bool AutoDownloadIncoming { get; init; } = true;
    public bool AutoResumeOnReconnect { get; init; } = true;
    public long MinimumFreeSpaceSafetyMarginBytes { get; init; } = 10 * 1024 * 1024;
}
