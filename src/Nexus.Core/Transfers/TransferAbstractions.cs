using Nexus.Contracts.Transfers;
using Nexus.Core.Errors;

namespace Nexus.Core.Transfers;

public sealed record DuplicateResolutionResult(
    string ResolvedFileName,
    string FullPath,
    bool ShouldSkip);

public interface ITransferRepository
{
    Task<Result<FileTransferRecord>> UpsertAsync(FileTransferRecord record, CancellationToken cancellationToken = default);
    Task<Result<FileTransferRecord?>> GetByIdAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<FileTransferRecord>>> ListAsync(
        TransferDirection? direction = null,
        TransferState? state = null,
        int limit = 200,
        CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<FileTransferRecord>>> ListResumableAsync(CancellationToken cancellationToken = default);
    Task<Result<int>> DeleteFinishedHistoryAsync(CancellationToken cancellationToken = default);
}

public interface ITransferStorageProvider
{
    string StagingDirectory { get; }
    string ReceivedDirectory { get; }

    Result<string> SanitizeFileName(string rawFileName);
    Result<string> ResolveSafeStagingPath(Guid transferId, string safeFileName, bool isPartFile);
    Result<DuplicateResolutionResult> ResolveSafeReceivedDestination(string safeFileName, TransferDuplicatePolicy duplicatePolicy);
    Result<long> CheckAvailableDiskSpace(string targetDirectory, long requiredBytes, long safetyMarginBytes);
    Task<Result<string>> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken = default);
    Task<Result<long>> WriteChunkAtOffsetAsync(
        string partFilePath,
        long offset,
        Stream dataStream,
        long? expectedTotalBytes = null,
        CancellationToken cancellationToken = default);
    Task<Result<string>> FinalizeVerifiedFileAsync(
        string partFilePath,
        string finalDestinationPath,
        string expectedSha256Hex,
        TransferDuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken = default);
    Task<Result> DeleteSafeFileIfExistsAsync(string filePath, CancellationToken cancellationToken = default);
    long GetExistingFileLength(string filePath);
}

public interface IFileTransferTransport
{
    Task<Result<InitiateFileTransferResponse>> InitiateTransferAsync(
        InitiateFileTransferRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ChunkUploadResponse>> UploadChunkAsync(
        Guid transferId,
        long offset,
        Stream chunkStream,
        long chunkLength,
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord>> FinalizeUploadAsync(
        Guid transferId,
        CancellationToken cancellationToken = default);

    Task<Result<long>> DownloadToPartFileAsync(
        FileTransferRecord transfer,
        string partFilePath,
        long existingOffsetBytes,
        Action<long, double>? progressCallback,
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord>> AcknowledgeTransferAsync(
        AcknowledgeFileTransferRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord?>> GetTransferStatusAsync(
        Guid transferId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FileTransferRecord>>> ListIncomingTransfersAsync(
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord?>> NotifyPauseAsync(
        Guid transferId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord?>> NotifyCancelAsync(
        Guid transferId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

public interface ITransferManager : IAsyncDisposable
{
    TransferQueueOptions Options { get; }
    IReadOnlyList<FileTransferRecord> ActiveTransfers { get; }
    IReadOnlyList<FileTransferRecord> History { get; }

    event EventHandler<FileTransferRecord>? TransferUpdated;

    Task<Result> InitializeAsync(CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FileTransferRecord>>> EnqueueSendFilesAsync(
        Guid targetDeviceId,
        string targetDeviceName,
        IReadOnlyList<string> localFilePaths,
        TransferDuplicatePolicy? duplicatePolicy = null,
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord>> EnqueueSendFileAsync(
        Guid targetDeviceId,
        string targetDeviceName,
        string localFilePath,
        TransferDuplicatePolicy? duplicatePolicy = null,
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord>> EnqueueReceiveTransferAsync(
        FileTransferRecord remoteTransfer,
        bool startImmediately = true,
        CancellationToken cancellationToken = default);

    Task<Result<FileTransferRecord>> PauseTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<Result<FileTransferRecord>> ResumeTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<Result<FileTransferRecord>> CancelTransferAsync(Guid transferId, string? reason = null, CancellationToken cancellationToken = default);
    Task<Result<int>> ResumeInterruptedTransfersAsync(CancellationToken cancellationToken = default);
    Task<Result<int>> SyncIncomingTransfersAsync(CancellationToken cancellationToken = default);
    Task<Result> UpdateConfigurationAsync(TransferQueueOptions options, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<FileTransferRecord>>> GetAllTransfersAsync(CancellationToken cancellationToken = default);
    Task<Result<int>> ClearHistoryAsync(CancellationToken cancellationToken = default);
}

