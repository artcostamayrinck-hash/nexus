using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Transfers;

namespace Nexus.Infrastructure.Transfers;

/// <summary>
/// Coordinates persistent transfer queues, bounded concurrency, pause/resume,
/// HTTP streaming uploads/downloads, SHA-256 verification, and SignalR control notifications.
/// </summary>
public sealed class TransferManagerService : ITransferManager
{
    private static readonly IReadOnlyDictionary<string, string> ExtensionContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain",
            [".md"] = "text/markdown",
            [".json"] = "application/json",
            [".csv"] = "text/csv",
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".mp4"] = "video/mp4",
            [".mp3"] = "audio/mpeg",
            [".zip"] = "application/zip",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

    private readonly ITransferRepository _repository;
    private readonly ITransferStorageProvider _storage;
    private readonly IFileTransferTransport _transport;
    private readonly IRealtimeClientChannel _realtimeChannel;
    private readonly IRealtimeCredentialProvider _credentialProvider;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly ILogger<TransferManagerService> _logger;

    private readonly ConcurrentDictionary<Guid, FileTransferRecord> _records = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeWorkers = new();
    private readonly ConcurrentDictionary<Guid, bool> _pauseFlags = new();
    private readonly ConcurrentDictionary<Guid, bool> _cancelFlags = new();
    private readonly SemaphoreSlim _schedulerGate = new(1, 1);

    private TransferQueueOptions _options = new();
    private bool _initialized;
    private bool _disposed;

    public TransferManagerService(
        ITransferRepository repository,
        ITransferStorageProvider storage,
        IFileTransferTransport transport,
        IRealtimeClientChannel realtimeChannel,
        IRealtimeCredentialProvider credentialProvider,
        IEventBus eventBus,
        IClock clock,
        ILogger<TransferManagerService> logger)
    {
        _repository = repository;
        _storage = storage;
        _transport = transport;
        _realtimeChannel = realtimeChannel;
        _credentialProvider = credentialProvider;
        _eventBus = eventBus;
        _clock = clock;
        _logger = logger;

        _realtimeChannel.TransferAvailableReceived += OnTransferAvailableReceived;
        _realtimeChannel.TransferProgressReceived += OnTransferProgressReceived;
        _realtimeChannel.StateChanged += OnRealtimeStateChanged;
    }

    public TransferQueueOptions Options => _options;

    public IReadOnlyList<FileTransferRecord> ActiveTransfers =>
        _records.Values
            .Where(r => r.State is TransferState.Pending or TransferState.InProgress or TransferState.Paused)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToArray();

    public IReadOnlyList<FileTransferRecord> History =>
        _records.Values
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToArray();

    public event EventHandler<FileTransferRecord>? TransferUpdated;

    public async Task<Result> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var listResult = await _repository.ListAsync(limit: 500, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (listResult.IsFailure)
        {
            return Result.Failure(listResult.Error!);
        }

        _records.Clear();
        foreach (var item in listResult.Value)
        {
            var normalized = item;
            if (item.State == TransferState.InProgress)
            {
                normalized = item with
                {
                    State = TransferState.Failed,
                    Stage = TransferStage.Failed,
                    FailureReason = TransferFailureReason.NetworkInterrupted,
                    FailureMessage = "Transferência interrompida ao encerrar o aplicativo; pronta para retomada.",
                    BytesPerSecond = 0,
                    UpdatedAtUtc = _clock.UtcNow
                };
                await _repository.UpsertAsync(normalized, cancellationToken).ConfigureAwait(false);
            }

            _records[normalized.TransferId] = normalized;
        }

        _initialized = true;
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<FileTransferRecord>>> EnqueueSendFilesAsync(
        Guid targetDeviceId,
        string targetDeviceName,
        IReadOnlyList<string> localFilePaths,
        TransferDuplicatePolicy? duplicatePolicy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localFilePaths);
        if (localFilePaths.Count == 0)
        {
            return Result<IReadOnlyList<FileTransferRecord>>.Failure(
                NexusError.Validation("Selecione ao menos um arquivo para enviar.", code: "TRANSFER_FILES_EMPTY"));
        }

        var enqueued = new List<FileTransferRecord>(localFilePaths.Count);
        foreach (var path in localFilePaths)
        {
            var result = await EnqueueSendFileAsync(targetDeviceId, targetDeviceName, path, duplicatePolicy, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result<IReadOnlyList<FileTransferRecord>>.Failure(result.Error!);
            }

            enqueued.Add(result.Value);
        }

        return Result<IReadOnlyList<FileTransferRecord>>.Success(enqueued);
    }

    public async Task<Result<FileTransferRecord>> EnqueueSendFileAsync(
        Guid targetDeviceId,
        string targetDeviceName,
        string localFilePath,
        TransferDuplicatePolicy? duplicatePolicy = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (targetDeviceId == Guid.Empty)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.Validation("Dispositivo de destino é obrigatório.", code: "TARGET_DEVICE_REQUIRED"));
        }

        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.NotFound($"Arquivo de origem não encontrado: '{localFilePath}'.", code: "SOURCE_FILE_NOT_FOUND"));
        }

        var credsResult = await _credentialProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (credsResult.IsFailure)
        {
            return Result<FileTransferRecord>.Failure(credsResult.Error!);
        }

        var creds = credsResult.Value;
        if (creds.DeviceId == targetDeviceId)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.Validation("Não é possível enviar arquivo para o próprio dispositivo.", code: "TRANSFER_SAME_DEVICE"));
        }

        var rawFileName = Path.GetFileName(localFilePath);
        var sanitizedResult = _storage.SanitizeFileName(rawFileName);
        if (sanitizedResult.IsFailure)
        {
            return Result<FileTransferRecord>.Failure(sanitizedResult.Error!);
        }

        var fileInfo = new FileInfo(localFilePath);
        var shaResult = await _storage.ComputeSha256HexAsync(localFilePath, cancellationToken).ConfigureAwait(false);
        if (shaResult.IsFailure)
        {
            return Result<FileTransferRecord>.Failure(shaResult.Error!);
        }

        var now = _clock.UtcNow;
        var record = new FileTransferRecord
        {
            TransferId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            SourceDeviceId = creds.DeviceId,
            SourceDeviceName = Environment.MachineName,
            TargetDeviceId = targetDeviceId,
            TargetDeviceName = string.IsNullOrWhiteSpace(targetDeviceName) ? targetDeviceId.ToString("N")[..8] : targetDeviceName.Trim(),
            OriginalFileName = rawFileName,
            SafeFileName = sanitizedResult.Value,
            ContentType = ResolveContentType(sanitizedResult.Value),
            TotalBytes = fileInfo.Length,
            TransferredBytes = 0,
            ServerUploadedBytes = 0,
            Sha256Checksum = shaResult.Value,
            Direction = TransferDirection.Upload,
            State = TransferState.Pending,
            Stage = TransferStage.Queued,
            DuplicatePolicy = duplicatePolicy ?? _options.DefaultDuplicatePolicy,
            FailureReason = TransferFailureReason.None,
            LocalFilePath = Path.GetFullPath(localFilePath),
            BytesPerSecond = 0,
            ChunkSizeBytes = _options.DefaultChunkSizeBytes,
            AutoExecute = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await PersistAndNotifyAsync(record, cancellationToken).ConfigureAwait(false);
        await ScheduleQueueDrainAsync().ConfigureAwait(false);
        return Result<FileTransferRecord>.Success(record);
    }

    public async Task<Result<FileTransferRecord>> EnqueueReceiveTransferAsync(
        FileTransferRecord remoteTransfer,
        bool startImmediately = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(remoteTransfer);

        if (_records.TryGetValue(remoteTransfer.TransferId, out var existing))
        {
            if (existing.State == TransferState.Completed || existing.State == TransferState.Cancelled)
            {
                return Result<FileTransferRecord>.Success(existing);
            }

            if (existing.State == TransferState.InProgress && _activeWorkers.ContainsKey(existing.TransferId))
            {
                return Result<FileTransferRecord>.Success(existing);
            }
        }

        var sanitizedResult = _storage.SanitizeFileName(remoteTransfer.SafeFileName);
        if (sanitizedResult.IsFailure)
        {
            return Result<FileTransferRecord>.Failure(sanitizedResult.Error!);
        }

        var partPathResult = _storage.ResolveSafeStagingPath(remoteTransfer.TransferId, sanitizedResult.Value, isPartFile: true);
        if (partPathResult.IsFailure)
        {
            return Result<FileTransferRecord>.Failure(partPathResult.Error!);
        }

        var existingPartLen = _storage.GetExistingFileLength(partPathResult.Value);
        var now = _clock.UtcNow;
        var downloadRecord = remoteTransfer with
        {
            SafeFileName = sanitizedResult.Value,
            Direction = TransferDirection.Download,
            State = startImmediately ? TransferState.Pending : TransferState.Paused,
            Stage = startImmediately ? TransferStage.ReadyForDownload : TransferStage.Paused,
            TransferredBytes = Math.Min(existingPartLen, remoteTransfer.TotalBytes),
            LocalFilePath = existing?.LocalFilePath ?? partPathResult.Value,
            AutoExecute = false,
            FailureReason = TransferFailureReason.None,
            FailureMessage = null,
            UpdatedAtUtc = now
        };

        await PersistAndNotifyAsync(downloadRecord, cancellationToken).ConfigureAwait(false);
        if (startImmediately)
        {
            await ScheduleQueueDrainAsync().ConfigureAwait(false);
        }

        return Result<FileTransferRecord>.Success(downloadRecord);
    }

    public async Task<Result<FileTransferRecord>> PauseTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue(transferId, out var existing))
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
        }

        if (existing.State is TransferState.Completed or TransferState.Cancelled)
        {
            return Result<FileTransferRecord>.Success(existing);
        }

        _pauseFlags[transferId] = true;
        if (_activeWorkers.TryGetValue(transferId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var paused = existing with
        {
            State = TransferState.Paused,
            Stage = TransferStage.Paused,
            BytesPerSecond = 0,
            FailureReason = TransferFailureReason.None,
            FailureMessage = "Pausado pelo usuário.",
            UpdatedAtUtc = _clock.UtcNow
        };

        await PersistAndNotifyAsync(paused, cancellationToken).ConfigureAwait(false);
        _ = await _transport.NotifyPauseAsync(transferId, "Pausado pelo usuário", cancellationToken).ConfigureAwait(false);
        return Result<FileTransferRecord>.Success(paused);
    }

    public async Task<Result<FileTransferRecord>> ResumeTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue(transferId, out var existing))
        {
            var fromDb = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
            if (fromDb.IsFailure || fromDb.Value is null)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
            }

            existing = fromDb.Value;
            _records[transferId] = existing;
        }

        if (existing.State is TransferState.Completed or TransferState.Cancelled)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.Conflict("Transferência finalizada ou cancelada não pode ser retomada.", code: "TRANSFER_NOT_RESUMABLE"));
        }

        _pauseFlags.TryRemove(transferId, out _);
        _cancelFlags.TryRemove(transferId, out _);

        var resumed = existing with
        {
            State = TransferState.Pending,
            Stage = existing.Direction == TransferDirection.Upload ? TransferStage.Queued : TransferStage.ReadyForDownload,
            FailureReason = TransferFailureReason.None,
            FailureMessage = null,
            BytesPerSecond = 0,
            UpdatedAtUtc = _clock.UtcNow
        };

        await PersistAndNotifyAsync(resumed, cancellationToken).ConfigureAwait(false);
        await ScheduleQueueDrainAsync().ConfigureAwait(false);
        return Result<FileTransferRecord>.Success(resumed);
    }

    public async Task<Result<FileTransferRecord>> CancelTransferAsync(
        Guid transferId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!_records.TryGetValue(transferId, out var existing))
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
        }

        if (existing.State == TransferState.Completed)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.Conflict("Transferência já concluída não pode ser cancelada.", code: "TRANSFER_ALREADY_COMPLETED"));
        }

        _cancelFlags[transferId] = true;
        _pauseFlags.TryRemove(transferId, out _);

        if (_activeWorkers.TryGetValue(transferId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var partPathResult = _storage.ResolveSafeStagingPath(transferId, existing.SafeFileName, isPartFile: true);
        if (partPathResult.IsSuccess)
        {
            await _storage.DeleteSafeFileIfExistsAsync(partPathResult.Value, cancellationToken).ConfigureAwait(false);
        }

        var cancelled = existing with
        {
            State = TransferState.Cancelled,
            Stage = TransferStage.Cancelled,
            BytesPerSecond = 0,
            FailureReason = TransferFailureReason.CancelledByUser,
            FailureMessage = string.IsNullOrWhiteSpace(reason) ? "Cancelado pelo usuário." : reason.Trim(),
            CompletedAtUtc = _clock.UtcNow,
            UpdatedAtUtc = _clock.UtcNow
        };

        await PersistAndNotifyAsync(cancelled, cancellationToken).ConfigureAwait(false);
        _ = await _transport.NotifyCancelAsync(transferId, reason, cancellationToken).ConfigureAwait(false);
        return Result<FileTransferRecord>.Success(cancelled);
    }

    public async Task<Result<int>> ResumeInterruptedTransfersAsync(CancellationToken cancellationToken = default)
    {
        var resumedCount = 0;
        var candidates = _records.Values
            .Where(r => r.State == TransferState.Failed && r.FailureReason == TransferFailureReason.NetworkInterrupted)
            .OrderBy(r => r.CreatedAtUtc)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var res = await ResumeTransferAsync(candidate.TransferId, cancellationToken).ConfigureAwait(false);
            if (res.IsSuccess)
            {
                resumedCount++;
            }
        }

        return Result<int>.Success(resumedCount);
    }

    public async Task<Result<int>> SyncIncomingTransfersAsync(CancellationToken cancellationToken = default)
    {
        var remoteResult = await _transport.ListIncomingTransfersAsync(cancellationToken).ConfigureAwait(false);
        if (remoteResult.IsFailure)
        {
            return Result<int>.Failure(remoteResult.Error!);
        }

        var addedOrUpdated = 0;
        foreach (var remote in remoteResult.Value)
        {
            if (remote.State is TransferState.Cancelled or TransferState.Completed)
            {
                continue;
            }

            if (_records.TryGetValue(remote.TransferId, out var localExisting)
                && localExisting.State is TransferState.Completed or TransferState.Cancelled or TransferState.Paused)
            {
                continue;
            }

            var res = await EnqueueReceiveTransferAsync(remote, _options.AutoDownloadIncoming, cancellationToken)
                .ConfigureAwait(false);
            if (res.IsSuccess)
            {
                addedOrUpdated++;
            }
        }

        return Result<int>.Success(addedOrUpdated);
    }

    public async Task<Result> UpdateConfigurationAsync(
        TransferQueueOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options with
        {
            MaxConcurrentTransfers = Math.Clamp(options.MaxConcurrentTransfers, 1, 8),
            DefaultChunkSizeBytes = Math.Clamp(options.DefaultChunkSizeBytes, 4096, 8 * 1024 * 1024),
            MinimumFreeSpaceSafetyMarginBytes = Math.Max(0, options.MinimumFreeSpaceSafetyMarginBytes)
        };

        await ScheduleQueueDrainAsync().ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<FileTransferRecord>>> GetAllTransfersAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = _records.Values
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToArray();
        return Result<IReadOnlyList<FileTransferRecord>>.Success(items);
    }

    public async Task<Result<int>> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        var deletedResult = await _repository.DeleteFinishedHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (deletedResult.IsFailure)
        {
            return deletedResult;
        }

        foreach (var entry in _records.Values.ToArray())
        {
            if (entry.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled)
            {
                _records.TryRemove(entry.TransferId, out _);
            }
        }

        return deletedResult;
    }

    private async Task ScheduleQueueDrainAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _schedulerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var maxConcurrent = Math.Clamp(_options.MaxConcurrentTransfers, 1, 8);
            var candidates = _records.Values
                .Where(r => r.State == TransferState.Pending
                    && ((r.Direction == TransferDirection.Upload && r.Stage == TransferStage.Queued)
                        || (r.Direction == TransferDirection.Download && r.Stage is TransferStage.ReadyForDownload or TransferStage.Downloading))
                    && !_activeWorkers.ContainsKey(r.TransferId)
                    && !_pauseFlags.ContainsKey(r.TransferId)
                    && !_cancelFlags.ContainsKey(r.TransferId))
                .OrderBy(r => r.CreatedAtUtc)
                .ToArray();

            foreach (var candidate in candidates)
            {
                if (_activeWorkers.Count >= maxConcurrent)
                {
                    break;
                }

                var cts = new CancellationTokenSource();
                if (!_activeWorkers.TryAdd(candidate.TransferId, cts))
                {
                    cts.Dispose();
                    continue;
                }

                var running = candidate with
                {
                    State = TransferState.InProgress,
                    Stage = candidate.Direction == TransferDirection.Upload ? TransferStage.Uploading : TransferStage.Downloading,
                    FailureReason = TransferFailureReason.None,
                    FailureMessage = null,
                    UpdatedAtUtc = _clock.UtcNow
                };

                await PersistAndNotifyAsync(running, CancellationToken.None).ConfigureAwait(false);

                _ = Task.Run(() => RunTransferWorkerAsync(running, cts));
            }
        }
        finally
        {
            _schedulerGate.Release();
        }
    }

    private async Task RunTransferWorkerAsync(FileTransferRecord initialRecord, CancellationTokenSource cts)
    {
        try
        {
            if (initialRecord.Direction == TransferDirection.Upload)
            {
                await ExecuteUploadAsync(initialRecord, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await ExecuteDownloadAsync(initialRecord, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (_cancelFlags.ContainsKey(initialRecord.TransferId) || _pauseFlags.ContainsKey(initialRecord.TransferId))
            {
                return;
            }

            if (_records.TryGetValue(initialRecord.TransferId, out var latest))
            {
                await FailTransferAsync(
                    latest,
                    TransferFailureReason.NetworkInterrupted,
                    "Transferência interrompida.",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado no worker de transferência {TransferId}.", initialRecord.TransferId);
            if (_records.TryGetValue(initialRecord.TransferId, out var latest))
            {
                await FailTransferAsync(
                    latest,
                    TransferFailureReason.NetworkInterrupted,
                    ex.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _activeWorkers.TryRemove(initialRecord.TransferId, out _);
            cts.Dispose();
            if (!_disposed)
            {
                _ = ScheduleQueueDrainAsync();
            }
        }
    }

    private async Task ExecuteUploadAsync(FileTransferRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.LocalFilePath) || !File.Exists(record.LocalFilePath))
        {
            await FailTransferAsync(
                record,
                TransferFailureReason.FileNotFound,
                $"Arquivo local '{record.LocalFilePath}' não encontrado.",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var initRequest = new InitiateFileTransferRequest
        {
            TransferId = record.TransferId,
            CorrelationId = record.CorrelationId,
            SourceDeviceId = record.SourceDeviceId,
            SourceDeviceName = record.SourceDeviceName,
            TargetDeviceId = record.TargetDeviceId,
            FileName = record.OriginalFileName,
            ContentType = record.ContentType,
            TotalBytes = record.TotalBytes,
            Sha256Checksum = record.Sha256Checksum,
            ChunkSizeBytes = record.ChunkSizeBytes,
            DuplicatePolicy = record.DuplicatePolicy
        };

        var initResult = await _transport.InitiateTransferAsync(initRequest, ct).ConfigureAwait(false);
        if (initResult.IsFailure)
        {
            await HandleTransportErrorAsync(record, initResult.Error!).ConfigureAwait(false);
            return;
        }

        var init = initResult.Value;
        var current = record with
        {
            SafeFileName = init.SafeFileName,
            ChunkSizeBytes = Math.Clamp(init.ChunkSizeBytes, 4096, 8 * 1024 * 1024),
            ServerUploadedBytes = init.AcceptedOffsetBytes,
            TransferredBytes = init.AcceptedOffsetBytes,
            UpdatedAtUtc = _clock.UtcNow
        };
        await PersistAndNotifyAsync(current, ct).ConfigureAwait(false);

        if (!init.AlreadyUploadedOnServer && current.TotalBytes > init.AcceptedOffsetBytes)
        {
            await using var fileStream = new FileStream(
                current.LocalFilePath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var offset = init.AcceptedOffsetBytes;
            var stopwatch = Stopwatch.StartNew();
            var sessionUploaded = 0L;

            while (offset < current.TotalBytes)
            {
                ct.ThrowIfCancellationRequested();

                var remaining = current.TotalBytes - offset;
                var chunkLen = Math.Min(current.ChunkSizeBytes, remaining);

                await using var chunkSubStream = new BoundedReadOnlySubStream(fileStream, offset, chunkLen);
                var chunkResult = await _transport
                    .UploadChunkAsync(current.TransferId, offset, chunkSubStream, chunkLen, ct)
                    .ConfigureAwait(false);

                if (chunkResult.IsFailure)
                {
                    await HandleTransportErrorAsync(current, chunkResult.Error!).ConfigureAwait(false);
                    return;
                }

                offset = chunkResult.Value.UploadedBytes;
                sessionUploaded += chunkLen;
                var elapsedSec = stopwatch.Elapsed.TotalSeconds;
                var speed = elapsedSec > 0 ? sessionUploaded / elapsedSec : sessionUploaded;

                current = current with
                {
                    TransferredBytes = offset,
                    ServerUploadedBytes = offset,
                    BytesPerSecond = speed,
                    UpdatedAtUtc = _clock.UtcNow
                };

                await PersistAndNotifyAsync(current, ct).ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
        var finalizeResult = await _transport.FinalizeUploadAsync(current.TransferId, ct).ConfigureAwait(false);
        if (finalizeResult.IsFailure)
        {
            await HandleTransportErrorAsync(current, finalizeResult.Error!).ConfigureAwait(false);
            return;
        }

        var latestFromServer = finalizeResult.Value;
        var finishedUpload = current with
        {
            SafeFileName = latestFromServer.SafeFileName,
            TransferredBytes = current.TotalBytes,
            ServerUploadedBytes = current.TotalBytes,
            BytesPerSecond = 0,
            State = latestFromServer.State == TransferState.Completed ? TransferState.Completed : TransferState.InProgress,
            Stage = latestFromServer.Stage,
            CompletedAtUtc = latestFromServer.CompletedAtUtc,
            UpdatedAtUtc = _clock.UtcNow
        };

        await PersistAndNotifyAsync(finishedUpload, CancellationToken.None).ConfigureAwait(false);
        await _eventBus.PublishAsync(
            new DiagnosticDomainEvent(
                "NexusTransfer",
                $"Envio de '{finishedUpload.SafeFileName}' enviado ao relay ({finishedUpload.TotalBytes} bytes).",
                IsError: false),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ExecuteDownloadAsync(FileTransferRecord record, CancellationToken ct)
    {
        var destResult = _storage.ResolveSafeReceivedDestination(record.SafeFileName, record.DuplicatePolicy);
        if (destResult.IsFailure)
        {
            await FailTransferAsync(
                record,
                MapFailureReason(destResult.Error!),
                destResult.Error!.Message,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var resolution = destResult.Value;
        if (resolution.ShouldSkip)
        {
            await _transport.AcknowledgeTransferAsync(
                new AcknowledgeFileTransferRequest
                {
                    TransferId = record.TransferId,
                    Succeeded = true,
                    VerifiedSha256 = record.Sha256Checksum,
                    ReceivedBytes = record.TotalBytes,
                    FailureReason = TransferFailureReason.DuplicateSkipped,
                    FailureMessage = "Arquivo já existente mantido conforme política de duplicados."
                },
                CancellationToken.None).ConfigureAwait(false);

            var skipped = record with
            {
                State = TransferState.Completed,
                Stage = TransferStage.Completed,
                TransferredBytes = record.TotalBytes,
                LocalFilePath = resolution.FullPath,
                FailureReason = TransferFailureReason.DuplicateSkipped,
                FailureMessage = "Arquivo duplicado ignorado (já existente).",
                BytesPerSecond = 0,
                CompletedAtUtc = _clock.UtcNow,
                UpdatedAtUtc = _clock.UtcNow
            };
            await PersistAndNotifyAsync(skipped, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var partPathResult = _storage.ResolveSafeStagingPath(record.TransferId, record.SafeFileName, isPartFile: true);
        if (partPathResult.IsFailure)
        {
            await FailTransferAsync(
                record,
                MapFailureReason(partPathResult.Error!),
                partPathResult.Error!.Message,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var partPath = partPathResult.Value;
        var existingPartBytes = _storage.GetExistingFileLength(partPath);
        if (existingPartBytes > record.TotalBytes)
        {
            await _storage.DeleteSafeFileIfExistsAsync(partPath, CancellationToken.None).ConfigureAwait(false);
            existingPartBytes = 0;
        }

        var remainingNeeded = Math.Max(0, record.TotalBytes - existingPartBytes);
        var spaceCheck = _storage.CheckAvailableDiskSpace(
            _storage.ReceivedDirectory,
            remainingNeeded,
            _options.MinimumFreeSpaceSafetyMarginBytes);

        if (spaceCheck.IsFailure)
        {
            await _transport.AcknowledgeTransferAsync(
                new AcknowledgeFileTransferRequest
                {
                    TransferId = record.TransferId,
                    Succeeded = false,
                    FailureReason = TransferFailureReason.InsufficientDiskSpace,
                    FailureMessage = spaceCheck.Error!.Message
                },
                CancellationToken.None).ConfigureAwait(false);

            await FailTransferAsync(
                record,
                TransferFailureReason.InsufficientDiskSpace,
                spaceCheck.Error!.Message,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var lastProgressEmit = DateTimeOffset.MinValue;
        var downloadResult = await _transport.DownloadToPartFileAsync(
            record,
            partPath,
            existingPartBytes,
            (writtenBytes, bytesPerSec) =>
            {
                var now = _clock.UtcNow;
                if ((now - lastProgressEmit).TotalMilliseconds < 120 && writtenBytes < record.TotalBytes)
                {
                    return;
                }

                lastProgressEmit = now;
                if (_records.TryGetValue(record.TransferId, out var latest)
                    && latest.State == TransferState.InProgress)
                {
                    var progressed = latest with
                    {
                        TransferredBytes = Math.Min(writtenBytes, latest.TotalBytes),
                        BytesPerSecond = bytesPerSec,
                        UpdatedAtUtc = now
                    };
                    _records[record.TransferId] = progressed;
                    TransferUpdated?.Invoke(this, progressed);
                }
            },
            ct).ConfigureAwait(false);

        if (downloadResult.IsFailure)
        {
            var latestState = _records.GetValueOrDefault(record.TransferId, record) with
            {
                TransferredBytes = Math.Min(_storage.GetExistingFileLength(partPath), record.TotalBytes),
                BytesPerSecond = 0
            };
            await HandleTransportErrorAsync(latestState, downloadResult.Error!).ConfigureAwait(false);
            return;
        }

        ct.ThrowIfCancellationRequested();

        var verifying = (_records.GetValueOrDefault(record.TransferId, record)) with
        {
            Stage = TransferStage.Verifying,
            TransferredBytes = downloadResult.Value,
            BytesPerSecond = 0,
            UpdatedAtUtc = _clock.UtcNow
        };
        await PersistAndNotifyAsync(verifying, ct).ConfigureAwait(false);

        var finalizedPathResult = await _storage.FinalizeVerifiedFileAsync(
            partPath,
            resolution.FullPath,
            record.Sha256Checksum,
            record.DuplicatePolicy,
            ct).ConfigureAwait(false);

        if (finalizedPathResult.IsFailure)
        {
            var failureReason = MapFailureReason(finalizedPathResult.Error!);
            await _transport.AcknowledgeTransferAsync(
                new AcknowledgeFileTransferRequest
                {
                    TransferId = record.TransferId,
                    Succeeded = false,
                    FailureReason = failureReason,
                    FailureMessage = finalizedPathResult.Error!.Message
                },
                CancellationToken.None).ConfigureAwait(false);

            await FailTransferAsync(
                verifying,
                failureReason,
                finalizedPathResult.Error!.Message,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var safeFinalPath = finalizedPathResult.Value;
        var completed = verifying with
        {
            SafeFileName = Path.GetFileName(safeFinalPath),
            LocalFilePath = safeFinalPath,
            TransferredBytes = record.TotalBytes,
            State = TransferState.Completed,
            Stage = TransferStage.Completed,
            FailureReason = TransferFailureReason.None,
            FailureMessage = null,
            BytesPerSecond = 0,
            AutoExecute = false,
            CompletedAtUtc = _clock.UtcNow,
            UpdatedAtUtc = _clock.UtcNow
        };

        await PersistAndNotifyAsync(completed, CancellationToken.None).ConfigureAwait(false);

        _ = await _transport.AcknowledgeTransferAsync(
            new AcknowledgeFileTransferRequest
            {
                TransferId = record.TransferId,
                Succeeded = true,
                VerifiedSha256 = record.Sha256Checksum,
                ReceivedBytes = record.TotalBytes,
                FailureReason = TransferFailureReason.None
            },
            CancellationToken.None).ConfigureAwait(false);

        await _eventBus.PublishAsync(
            new DiagnosticDomainEvent(
                "NexusTransfer",
                $"Arquivo '{completed.SafeFileName}' recebido e verificado com SHA-256 em segurança.",
                IsError: false),
            CancellationToken.None).ConfigureAwait(false);
    }

    private Task HandleTransportErrorAsync(FileTransferRecord record, NexusError error)
    {
        if (_cancelFlags.ContainsKey(record.TransferId) || _pauseFlags.ContainsKey(record.TransferId))
        {
            return Task.CompletedTask;
        }

        return FailTransferAsync(record, MapFailureReason(error), error.Message, CancellationToken.None);
    }

    private async Task FailTransferAsync(
        FileTransferRecord record,
        TransferFailureReason reason,
        string message,
        CancellationToken ct)
    {
        var failed = record with
        {
            State = TransferState.Failed,
            Stage = TransferStage.Failed,
            FailureReason = reason,
            FailureMessage = message,
            BytesPerSecond = 0,
            UpdatedAtUtc = _clock.UtcNow
        };

        await PersistAndNotifyAsync(failed, ct).ConfigureAwait(false);
        await _eventBus.PublishAsync(
            new DiagnosticDomainEvent("NexusTransfer", $"Falha em '{failed.SafeFileName}': {message}", IsError: true),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistAndNotifyAsync(FileTransferRecord record, CancellationToken ct)
    {
        var safeRecord = record with { AutoExecute = false };
        _records[safeRecord.TransferId] = safeRecord;
        await _repository.UpsertAsync(safeRecord, ct).ConfigureAwait(false);
        TransferUpdated?.Invoke(this, safeRecord);
    }

    private void OnTransferAvailableReceived(object? sender, FileTransferRecord remote)
    {
        _ = Task.Run(async () =>
        {
            var credsResult = await _credentialProvider.GetCredentialsAsync().ConfigureAwait(false);
            if (credsResult.IsFailure || credsResult.Value.DeviceId != remote.TargetDeviceId)
            {
                return;
            }

            await EnqueueReceiveTransferAsync(remote, _options.AutoDownloadIncoming).ConfigureAwait(false);
        });
    }

    private void OnTransferProgressReceived(object? sender, FileTransferProgressEvent progress)
    {
        if (!_records.TryGetValue(progress.TransferId, out var existing))
        {
            return;
        }

        // Ignore remote echoes while our local download worker owns active byte counting.
        if (existing.Direction == TransferDirection.Download
            && _activeWorkers.ContainsKey(existing.TransferId)
            && progress.State is not (TransferState.Completed or TransferState.Cancelled))
        {
            return;
        }

        if (existing.Direction == TransferDirection.Upload)
        {
            var updated = existing with
            {
                State = progress.State,
                Stage = progress.Stage,
                TransferredBytes = progress.Stage == TransferStage.Completed
                    ? existing.TotalBytes
                    : Math.Max(existing.TransferredBytes, progress.TransferredBytes),
                BytesPerSecond = progress.Stage == TransferStage.Completed ? 0 : progress.BytesPerSecond,
                FailureReason = progress.FailureReason,
                FailureMessage = progress.Message,
                CompletedAtUtc = progress.State == TransferState.Completed
                    ? (existing.CompletedAtUtc ?? progress.TimestampUtc)
                    : existing.CompletedAtUtc,
                UpdatedAtUtc = progress.TimestampUtc
            };

            _ = PersistAndNotifyAsync(updated, CancellationToken.None);
        }
    }

    private void OnRealtimeStateChanged(object? sender, ConnectionStateSnapshot snapshot)
    {
        if (snapshot.State != ConnectionState.Connected || _disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await SyncIncomingTransfersAsync().ConfigureAwait(false);
            if (_options.AutoResumeOnReconnect)
            {
                await ResumeInterruptedTransfersAsync().ConfigureAwait(false);
            }
        });
    }

    private static TransferFailureReason MapFailureReason(NexusError error)
    {
        return error.Code switch
        {
            "INSUFFICIENT_DISK_SPACE" => TransferFailureReason.InsufficientDiskSpace,
            "CHECKSUM_MISMATCH" => TransferFailureReason.IntegrityChecksumMismatch,
            "DUPLICATE_SKIPPED" => TransferFailureReason.DuplicateSkipped,
            "PERMISSION_DENIED_FILE_TRANSFER" or "DEVICE_REVOKED" or "REALTIME_AUTH_EXPIRED" => TransferFailureReason.UnauthorizedOrPermissionDenied,
            "PATH_ESCAPE_FORBIDDEN" or "TRANSFER_FILENAME_UNSAFE" or "TRANSFER_FILENAME_EMPTY" or "TRANSFER_FILENAME_INVALID" or "TRANSFER_FILENAME_RESERVED" => TransferFailureReason.ValidationFailed,
            "TRANSFER_NETWORK_INTERRUPTED" => TransferFailureReason.NetworkInterrupted,
            "FILE_NOT_FOUND" or "SOURCE_FILE_NOT_FOUND" or "TRANSFER_NOT_FOUND" => TransferFailureReason.FileNotFound,
            _ when error.Category == ErrorCategory.Network => TransferFailureReason.NetworkInterrupted,
            _ when error.Category == ErrorCategory.Unauthorized => TransferFailureReason.UnauthorizedOrPermissionDenied,
            _ when error.Category == ErrorCategory.Validation => TransferFailureReason.ValidationFailed,
            _ => TransferFailureReason.StorageFailure
        };
    }

    private static string ResolveContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && ExtensionContentTypes.TryGetValue(ext, out var mime)
            ? mime
            : "application/octet-stream";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _realtimeChannel.TransferAvailableReceived -= OnTransferAvailableReceived;
        _realtimeChannel.TransferProgressReceived -= OnTransferProgressReceived;
        _realtimeChannel.StateChanged -= OnRealtimeStateChanged;

        foreach (var kvp in _activeWorkers)
        {
            try
            {
                await kvp.Value.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore during shutdown.
            }
        }

        _schedulerGate.Dispose();
    }
}

