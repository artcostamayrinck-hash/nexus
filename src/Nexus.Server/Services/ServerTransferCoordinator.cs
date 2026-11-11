using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Common;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;
using Nexus.Server.Hubs;

namespace Nexus.Server.Services;

/// <summary>
/// Server coordinator for authenticated HTTP streaming file transfers with SHA-256 verification,
/// chunked resume, disk-space checks, permission validation, and SignalR control notifications.
/// </summary>
public sealed class ServerTransferCoordinator
{
    private readonly ITransferRepository _repository;
    private readonly ITransferStorageProvider _storage;
    private readonly TransferQueueOptions _queueOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INexusPresenceTracker _presenceTracker;
    private readonly IHubContext<NexusHub, INexusHubClient> _hubContext;
    private readonly IClock _clock;
    private readonly ILogger<ServerTransferCoordinator> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _transferLocks = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastProgressPushUtc = new();

    public ServerTransferCoordinator(
        ITransferRepository repository,
        ITransferStorageProvider storage,
        TransferQueueOptions queueOptions,
        IServiceScopeFactory scopeFactory,
        INexusPresenceTracker presenceTracker,
        IHubContext<NexusHub, INexusHubClient> hubContext,
        IClock clock,
        ILogger<ServerTransferCoordinator> logger)
    {
        _repository = repository;
        _storage = storage;
        _queueOptions = queueOptions;
        _scopeFactory = scopeFactory;
        _presenceTracker = presenceTracker;
        _hubContext = hubContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<TrustedDeviceRecord>> AuthorizeDeviceAsync(
        Guid deviceId,
        NexusPermission requiredPermission,
        CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Identidade do dispositivo ausente.", code: "DEVICE_IDENTITY_MISSING"));
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var deviceResult = await trustedRepo.GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (deviceResult.IsFailure || deviceResult.Value is null)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Dispositivo não está autorizado neste servidor.", code: "DEVICE_NOT_TRUSTED"));
        }

        var device = deviceResult.Value;
        if (device.TrustStatus != DeviceTrustStatus.Authorized)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Acesso do dispositivo foi revogado.", code: "DEVICE_REVOKED"));
        }

        if (!device.Permissions.HasFlag(requiredPermission))
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Forbidden(
                    $"Dispositivo '{device.CustomName}' não possui permissão {requiredPermission}.",
                    code: "PERMISSION_DENIED_FILE_TRANSFER"));
        }

        return Result<TrustedDeviceRecord>.Success(device);
    }

    public async Task<Result<InitiateFileTransferResponse>> InitiateTransferAsync(
        TrustedDeviceRecord sender,
        InitiateFileTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(request);

        var validationErrors = request.Validate().ToArray();
        if (validationErrors.Length > 0)
        {
            return Result<InitiateFileTransferResponse>.Failure(
                NexusError.Validation(validationErrors[0].Message, code: validationErrors[0].Code));
        }

        if (request.SourceDeviceId != sender.DeviceId)
        {
            return Result<InitiateFileTransferResponse>.Failure(
                NexusError.Forbidden("SourceDeviceId diverge do dispositivo autenticado.", code: "DEVICE_IDENTITY_MISMATCH"));
        }

        var targetAuth = await AuthorizeDeviceAsync(request.TargetDeviceId, NexusPermission.FileTransfer, cancellationToken)
            .ConfigureAwait(false);
        if (targetAuth.IsFailure)
        {
            var err = targetAuth.Error!;
            return Result<InitiateFileTransferResponse>.Failure(
                NexusError.Forbidden($"Destinatário inválido ou sem permissão FileTransfer: {err.Message}", code: "PERMISSION_DENIED_FILE_TRANSFER"));
        }

        var sanitizedResult = _storage.SanitizeFileName(request.FileName);
        if (sanitizedResult.IsFailure)
        {
            return Result<InitiateFileTransferResponse>.Failure(sanitizedResult.Error!);
        }

        var safeFileName = sanitizedResult.Value;
        var transferLock = _transferLocks.GetOrAdd(request.TransferId, _ => new SemaphoreSlim(1, 1));
        await transferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingResult = await _repository.GetByIdAsync(request.TransferId, cancellationToken).ConfigureAwait(false);
            if (existingResult.IsFailure)
            {
                return Result<InitiateFileTransferResponse>.Failure(existingResult.Error!);
            }

            if (existingResult.Value is { } existing)
            {
                if (existing.SourceDeviceId != sender.DeviceId)
                {
                    return Result<InitiateFileTransferResponse>.Failure(
                        NexusError.Forbidden("TransferId já pertence a outro dispositivo remetente.", code: "TRANSFER_CONFLICT"));
                }

                if (existing.State == TransferState.Cancelled)
                {
                    return Result<InitiateFileTransferResponse>.Failure(
                        NexusError.Conflict("Transferência foi cancelada e não pode ser reiniciada com o mesmo ID.", code: "TRANSFER_CANCELLED"));
                }

                if (existing.Stage is TransferStage.ReadyForDownload or TransferStage.Downloading or TransferStage.Completed)
                {
                    return Result<InitiateFileTransferResponse>.Success(new InitiateFileTransferResponse
                    {
                        TransferId = existing.TransferId,
                        SafeFileName = existing.SafeFileName,
                        AcceptedOffsetBytes = existing.TotalBytes,
                        ChunkSizeBytes = existing.ChunkSizeBytes,
                        State = existing.State,
                        Stage = existing.Stage,
                        AlreadyUploadedOnServer = true,
                        ServerAvailableFreeBytes = GetSafeFreeBytes()
                    });
                }

                var resumePartResult = _storage.ResolveSafeStagingPath(existing.TransferId, existing.SafeFileName, isPartFile: true);
                if (resumePartResult.IsFailure)
                {
                    return Result<InitiateFileTransferResponse>.Failure(resumePartResult.Error!);
                }

                var currentPartLen = Math.Min(_storage.GetExistingFileLength(resumePartResult.Value), request.TotalBytes);
                var remainingBytes = Math.Max(0, request.TotalBytes - currentPartLen);
                var spaceCheck = _storage.CheckAvailableDiskSpace(
                    _storage.StagingDirectory,
                    remainingBytes,
                    _queueOptions.MinimumFreeSpaceSafetyMarginBytes);

                if (spaceCheck.IsFailure)
                {
                    return Result<InitiateFileTransferResponse>.Failure(spaceCheck.Error!);
                }

                var resumed = existing with
                {
                    State = TransferState.InProgress,
                    Stage = TransferStage.Uploading,
                    ServerUploadedBytes = currentPartLen,
                    TransferredBytes = currentPartLen,
                    FailureReason = TransferFailureReason.None,
                    FailureMessage = null,
                    UpdatedAtUtc = _clock.UtcNow
                };
                await _repository.UpsertAsync(resumed, cancellationToken).ConfigureAwait(false);

                return Result<InitiateFileTransferResponse>.Success(new InitiateFileTransferResponse
                {
                    TransferId = resumed.TransferId,
                    SafeFileName = resumed.SafeFileName,
                    AcceptedOffsetBytes = currentPartLen,
                    ChunkSizeBytes = resumed.ChunkSizeBytes,
                    State = resumed.State,
                    Stage = resumed.Stage,
                    AlreadyUploadedOnServer = false,
                    ServerAvailableFreeBytes = spaceCheck.Value
                });
            }

            var initialSpaceCheck = _storage.CheckAvailableDiskSpace(
                _storage.StagingDirectory,
                request.TotalBytes,
                _queueOptions.MinimumFreeSpaceSafetyMarginBytes);

            if (initialSpaceCheck.IsFailure)
            {
                return Result<InitiateFileTransferResponse>.Failure(initialSpaceCheck.Error!);
            }

            var now = _clock.UtcNow;
            var record = new FileTransferRecord
            {
                TransferId = request.TransferId,
                CorrelationId = request.CorrelationId,
                SourceDeviceId = sender.DeviceId,
                SourceDeviceName = string.IsNullOrWhiteSpace(request.SourceDeviceName) ? sender.CustomName : request.SourceDeviceName.Trim(),
                TargetDeviceId = targetAuth.Value.DeviceId,
                TargetDeviceName = targetAuth.Value.CustomName,
                OriginalFileName = request.FileName,
                SafeFileName = safeFileName,
                ContentType = request.ContentType,
                TotalBytes = request.TotalBytes,
                TransferredBytes = 0,
                ServerUploadedBytes = 0,
                Sha256Checksum = request.Sha256Checksum,
                Direction = TransferDirection.Upload,
                State = TransferState.InProgress,
                Stage = TransferStage.Uploading,
                DuplicatePolicy = request.DuplicatePolicy,
                FailureReason = TransferFailureReason.None,
                ChunkSizeBytes = Math.Clamp(request.ChunkSizeBytes, 4096, 8 * 1024 * 1024),
                AutoExecute = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            await BroadcastProgressAsync(record, message: "Upload iniciado", force: true, cancellationToken).ConfigureAwait(false);

            return Result<InitiateFileTransferResponse>.Success(new InitiateFileTransferResponse
            {
                TransferId = record.TransferId,
                SafeFileName = record.SafeFileName,
                AcceptedOffsetBytes = 0,
                ChunkSizeBytes = record.ChunkSizeBytes,
                State = record.State,
                Stage = record.Stage,
                AlreadyUploadedOnServer = false,
                ServerAvailableFreeBytes = initialSpaceCheck.Value
            });
        }
        finally
        {
            transferLock.Release();
        }
    }

    public async Task<Result<ChunkUploadResponse>> ReceiveChunkAsync(
        TrustedDeviceRecord sender,
        Guid transferId,
        long offset,
        Stream bodyStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(bodyStream);

        var transferLock = _transferLocks.GetOrAdd(transferId, _ => new SemaphoreSlim(1, 1));
        await transferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recordResult = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
            if (recordResult.IsFailure)
            {
                return Result<ChunkUploadResponse>.Failure(recordResult.Error!);
            }

            if (recordResult.Value is null)
            {
                return Result<ChunkUploadResponse>.Failure(
                    NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
            }

            var record = recordResult.Value;
            if (record.SourceDeviceId != sender.DeviceId)
            {
                return Result<ChunkUploadResponse>.Failure(
                    NexusError.Forbidden("Apenas o remetente autenticado pode enviar chunks desta transferência.", code: "PERMISSION_DENIED_FILE_TRANSFER"));
            }

            if (record.State == TransferState.Cancelled)
            {
                return Result<ChunkUploadResponse>.Failure(
                    NexusError.Conflict("Transferência foi cancelada.", code: "TRANSFER_CANCELLED"));
            }

            var partPathResult = _storage.ResolveSafeStagingPath(transferId, record.SafeFileName, isPartFile: true);
            if (partPathResult.IsFailure)
            {
                return Result<ChunkUploadResponse>.Failure(partPathResult.Error!);
            }

            var writeResult = await _storage
                .WriteChunkAtOffsetAsync(partPathResult.Value, offset, bodyStream, record.TotalBytes, cancellationToken)
                .ConfigureAwait(false);

            if (writeResult.IsFailure)
            {
                return Result<ChunkUploadResponse>.Failure(writeResult.Error!);
            }

            var uploadedBytes = writeResult.Value;
            var updated = record with
            {
                State = TransferState.InProgress,
                Stage = TransferStage.Uploading,
                ServerUploadedBytes = uploadedBytes,
                TransferredBytes = uploadedBytes,
                UpdatedAtUtc = _clock.UtcNow
            };

            await _repository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            await BroadcastProgressAsync(updated, message: null, force: uploadedBytes >= updated.TotalBytes, cancellationToken)
                .ConfigureAwait(false);

            return Result<ChunkUploadResponse>.Success(new ChunkUploadResponse
            {
                TransferId = transferId,
                UploadedBytes = uploadedBytes,
                TotalBytes = updated.TotalBytes,
                PercentComplete = updated.PercentComplete,
                Stage = updated.Stage
            });
        }
        finally
        {
            transferLock.Release();
        }
    }

    public async Task<Result<FileTransferRecord>> FinalizeUploadAsync(
        TrustedDeviceRecord sender,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);

        var transferLock = _transferLocks.GetOrAdd(transferId, _ => new SemaphoreSlim(1, 1));
        await transferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recordResult = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
            if (recordResult.IsFailure)
            {
                return Result<FileTransferRecord>.Failure(recordResult.Error!);
            }

            if (recordResult.Value is null)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
            }

            var record = recordResult.Value;
            if (record.SourceDeviceId != sender.DeviceId)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.Forbidden("Apenas o remetente autenticado pode finalizar este upload.", code: "PERMISSION_DENIED_FILE_TRANSFER"));
            }

            if (record.Stage is TransferStage.ReadyForDownload or TransferStage.Downloading or TransferStage.Completed)
            {
                return Result<FileTransferRecord>.Success(record);
            }

            var partPathResult = _storage.ResolveSafeStagingPath(transferId, record.SafeFileName, isPartFile: true);
            var stagedPathResult = _storage.ResolveSafeStagingPath(transferId, record.SafeFileName, isPartFile: false);
            if (partPathResult.IsFailure || stagedPathResult.IsFailure)
            {
                return Result<FileTransferRecord>.Failure(partPathResult.Error ?? stagedPathResult.Error!);
            }

            var partLen = _storage.GetExistingFileLength(partPathResult.Value);
            if (record.TotalBytes == 0 && !File.Exists(partPathResult.Value))
            {
                var dir = Path.GetDirectoryName(partPathResult.Value);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.WriteAllBytesAsync(partPathResult.Value, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                partLen = 0;
            }

            if (partLen != record.TotalBytes)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.Validation(
                        $"Upload incompleto: recebido {partLen} de {record.TotalBytes} bytes.",
                        code: "TRANSFER_INCOMPLETE"));
            }

            var finalized = await _storage.FinalizeVerifiedFileAsync(
                partPathResult.Value,
                stagedPathResult.Value,
                record.Sha256Checksum,
                TransferDuplicatePolicy.Overwrite,
                cancellationToken).ConfigureAwait(false);

            if (finalized.IsFailure)
            {
                var failed = record with
                {
                    State = TransferState.Failed,
                    Stage = TransferStage.Failed,
                    FailureReason = finalized.Error!.Code == "CHECKSUM_MISMATCH"
                        ? TransferFailureReason.IntegrityChecksumMismatch
                        : TransferFailureReason.StorageFailure,
                    FailureMessage = finalized.Error!.Message,
                    UpdatedAtUtc = _clock.UtcNow
                };
                await _repository.UpsertAsync(failed, cancellationToken).ConfigureAwait(false);
                await BroadcastProgressAsync(failed, finalized.Error!.Message, force: true, cancellationToken).ConfigureAwait(false);
                return Result<FileTransferRecord>.Failure(finalized.Error!);
            }

            var ready = record with
            {
                LocalFilePath = finalized.Value,
                ServerUploadedBytes = record.TotalBytes,
                TransferredBytes = record.TotalBytes,
                State = TransferState.InProgress,
                Stage = TransferStage.ReadyForDownload,
                FailureReason = TransferFailureReason.None,
                FailureMessage = null,
                UpdatedAtUtc = _clock.UtcNow
            };

            await _repository.UpsertAsync(ready, cancellationToken).ConfigureAwait(false);
            await NotifyTransferAvailableAsync(ready, cancellationToken).ConfigureAwait(false);
            await BroadcastProgressAsync(ready, "Disponível para download no destinatário", force: true, cancellationToken).ConfigureAwait(false);

            return Result<FileTransferRecord>.Success(ready);
        }
        finally
        {
            transferLock.Release();
        }
    }

    public async Task<Result<(FileTransferRecord Record, string StagedFilePath)>> ResolveDownloadAsync(
        TrustedDeviceRecord recipient,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        var recordResult = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
        if (recordResult.IsFailure)
        {
            return Result<(FileTransferRecord, string)>.Failure(recordResult.Error!);
        }

        if (recordResult.Value is null)
        {
            return Result<(FileTransferRecord, string)>.Failure(
                NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
        }

        var record = recordResult.Value;
        if (record.TargetDeviceId != recipient.DeviceId)
        {
            return Result<(FileTransferRecord, string)>.Failure(
                NexusError.Forbidden("Apenas o dispositivo destinatário autorizado pode baixar este arquivo.", code: "PERMISSION_DENIED_FILE_TRANSFER"));
        }

        if (record.State == TransferState.Cancelled)
        {
            return Result<(FileTransferRecord, string)>.Failure(
                NexusError.Conflict("Transferência foi cancelada.", code: "TRANSFER_CANCELLED"));
        }

        var stagedResult = _storage.ResolveSafeStagingPath(transferId, record.SafeFileName, isPartFile: false);
        if (stagedResult.IsFailure)
        {
            return Result<(FileTransferRecord, string)>.Failure(stagedResult.Error!);
        }

        if (!File.Exists(stagedResult.Value))
        {
            return Result<(FileTransferRecord, string)>.Failure(
                NexusError.NotFound("Arquivo ainda não disponível ou expirado no staging do servidor.", code: "TRANSFER_FILE_NOT_READY"));
        }

        if (record.Stage == TransferStage.ReadyForDownload)
        {
            var downloading = record with
            {
                Stage = TransferStage.Downloading,
                State = TransferState.InProgress,
                UpdatedAtUtc = _clock.UtcNow
            };
            await _repository.UpsertAsync(downloading, cancellationToken).ConfigureAwait(false);
            await BroadcastProgressAsync(downloading, "Destinatário iniciou o download", force: true, cancellationToken).ConfigureAwait(false);
            record = downloading;
        }

        return Result<(FileTransferRecord, string)>.Success((record, stagedResult.Value));
    }

    public async Task<Result<FileTransferRecord>> AcknowledgeAsync(
        TrustedDeviceRecord recipient,
        Guid transferId,
        AcknowledgeFileTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(request);

        var transferLock = _transferLocks.GetOrAdd(transferId, _ => new SemaphoreSlim(1, 1));
        await transferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recordResult = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
            if (recordResult.IsFailure)
            {
                return Result<FileTransferRecord>.Failure(recordResult.Error!);
            }

            if (recordResult.Value is null)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
            }

            var record = recordResult.Value;
            if (record.TargetDeviceId != recipient.DeviceId)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.Forbidden("Apenas o dispositivo destinatário pode confirmar esta transferência.", code: "PERMISSION_DENIED_FILE_TRANSFER"));
            }

            var now = _clock.UtcNow;
            FileTransferRecord updated;
            if (request.Succeeded)
            {
                updated = record with
                {
                    State = TransferState.Completed,
                    Stage = TransferStage.Completed,
                    TransferredBytes = record.TotalBytes,
                    FailureReason = request.FailureReason,
                    FailureMessage = request.FailureMessage,
                    BytesPerSecond = 0,
                    CompletedAtUtc = now,
                    UpdatedAtUtc = now
                };

                var stagedPath = _storage.ResolveSafeStagingPath(transferId, record.SafeFileName, isPartFile: false);
                if (stagedPath.IsSuccess)
                {
                    await _storage.DeleteSafeFileIfExistsAsync(stagedPath.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                updated = record with
                {
                    State = TransferState.Failed,
                    Stage = TransferStage.Failed,
                    FailureReason = request.FailureReason == TransferFailureReason.None
                        ? TransferFailureReason.StorageFailure
                        : request.FailureReason,
                    FailureMessage = request.FailureMessage ?? "Falha reportada pelo destinatário.",
                    BytesPerSecond = 0,
                    UpdatedAtUtc = now
                };
            }

            await _repository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            await BroadcastProgressAsync(updated, updated.FailureMessage ?? "Transferência concluída", force: true, cancellationToken)
                .ConfigureAwait(false);

            return Result<FileTransferRecord>.Success(updated);
        }
        finally
        {
            transferLock.Release();
        }
    }

    public async Task<Result<FileTransferRecord>> PauseAsync(
        TrustedDeviceRecord caller,
        Guid transferId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var recordResult = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
        if (recordResult.IsFailure)
        {
            return Result<FileTransferRecord>.Failure(recordResult.Error!);
        }

        if (recordResult.Value is null)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
        }

        var record = recordResult.Value;
        if (record.SourceDeviceId != caller.DeviceId && record.TargetDeviceId != caller.DeviceId)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.Forbidden("Dispositivo não participa desta transferência.", code: "PERMISSION_DENIED_FILE_TRANSFER"));
        }

        if (record.State is TransferState.Completed or TransferState.Cancelled)
        {
            return Result<FileTransferRecord>.Success(record);
        }

        var paused = record with
        {
            State = TransferState.Paused,
            Stage = TransferStage.Paused,
            FailureMessage = string.IsNullOrWhiteSpace(reason) ? "Pausado." : reason.Trim(),
            BytesPerSecond = 0,
            UpdatedAtUtc = _clock.UtcNow
        };

        await _repository.UpsertAsync(paused, cancellationToken).ConfigureAwait(false);
        await BroadcastProgressAsync(paused, paused.FailureMessage, force: true, cancellationToken).ConfigureAwait(false);
        return Result<FileTransferRecord>.Success(paused);
    }

    public async Task<Result<FileTransferRecord>> CancelAsync(
        TrustedDeviceRecord caller,
        Guid transferId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var transferLock = _transferLocks.GetOrAdd(transferId, _ => new SemaphoreSlim(1, 1));
        await transferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recordResult = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
            if (recordResult.IsFailure)
            {
                return Result<FileTransferRecord>.Failure(recordResult.Error!);
            }

            if (recordResult.Value is null)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
            }

            var record = recordResult.Value;
            if (record.SourceDeviceId != caller.DeviceId && record.TargetDeviceId != caller.DeviceId)
            {
                return Result<FileTransferRecord>.Failure(
                    NexusError.Forbidden("Dispositivo não participa desta transferência.", code: "PERMISSION_DENIED_FILE_TRANSFER"));
            }

            var partPath = _storage.ResolveSafeStagingPath(transferId, record.SafeFileName, isPartFile: true);
            if (partPath.IsSuccess)
            {
                await _storage.DeleteSafeFileIfExistsAsync(partPath.Value, cancellationToken).ConfigureAwait(false);
            }

            var stagedPath = _storage.ResolveSafeStagingPath(transferId, record.SafeFileName, isPartFile: false);
            if (stagedPath.IsSuccess)
            {
                await _storage.DeleteSafeFileIfExistsAsync(stagedPath.Value, cancellationToken).ConfigureAwait(false);
            }

            var cancelled = record with
            {
                State = TransferState.Cancelled,
                Stage = TransferStage.Cancelled,
                FailureReason = TransferFailureReason.CancelledByUser,
                FailureMessage = string.IsNullOrWhiteSpace(reason) ? "Cancelado pelo usuário." : reason.Trim(),
                BytesPerSecond = 0,
                CompletedAtUtc = _clock.UtcNow,
                UpdatedAtUtc = _clock.UtcNow
            };

            await _repository.UpsertAsync(cancelled, cancellationToken).ConfigureAwait(false);
            await BroadcastProgressAsync(cancelled, cancelled.FailureMessage, force: true, cancellationToken).ConfigureAwait(false);
            return Result<FileTransferRecord>.Success(cancelled);
        }
        finally
        {
            transferLock.Release();
        }
    }

    public async Task<Result<FileTransferRecord>> GetStatusAsync(
        TrustedDeviceRecord caller,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        var recordResult = await _repository.GetByIdAsync(transferId, cancellationToken).ConfigureAwait(false);
        if (recordResult.IsFailure)
        {
            return Result<FileTransferRecord>.Failure(recordResult.Error!);
        }

        if (recordResult.Value is null)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.NotFound($"Transferência '{transferId}' não encontrada.", code: "TRANSFER_NOT_FOUND"));
        }

        var record = recordResult.Value;
        if (record.SourceDeviceId != caller.DeviceId && record.TargetDeviceId != caller.DeviceId)
        {
            return Result<FileTransferRecord>.Failure(
                NexusError.Forbidden("Dispositivo não tem acesso a esta transferência.", code: "PERMISSION_DENIED_FILE_TRANSFER"));
        }

        return Result<FileTransferRecord>.Success(record);
    }

    public async Task<Result<IncomingTransfersResponse>> ListIncomingAsync(
        TrustedDeviceRecord recipient,
        CancellationToken cancellationToken)
    {
        var listResult = await _repository.ListAsync(limit: 200, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (listResult.IsFailure)
        {
            return Result<IncomingTransfersResponse>.Failure(listResult.Error!);
        }

        var incoming = listResult.Value
            .Where(r => r.TargetDeviceId == recipient.DeviceId
                && r.State is not (TransferState.Completed or TransferState.Cancelled)
                && r.Stage is TransferStage.ReadyForDownload or TransferStage.Downloading or TransferStage.Paused or TransferStage.Failed)
            .Select(r => r with { LocalFilePath = null, AutoExecute = false })
            .ToArray();

        return Result<IncomingTransfersResponse>.Success(new IncomingTransfersResponse
        {
            Transfers = incoming,
            ServerUtc = _clock.UtcNow
        });
    }

    private async Task NotifyTransferAvailableAsync(FileTransferRecord record, CancellationToken ct)
    {
        var targetConnections = _presenceTracker.GetConnectionIdsForDevice(record.TargetDeviceId);
        if (targetConnections.Count == 0)
        {
            return;
        }

        // Send only lightweight control metadata over SignalR; file bytes transfer solely via HTTP streaming.
        var sanitizedMetadata = record with { LocalFilePath = null, AutoExecute = false };
        await _hubContext.Clients
            .Clients(targetConnections)
            .ReceiveTransferAvailable(sanitizedMetadata)
            .ConfigureAwait(false);
    }

    private async Task BroadcastProgressAsync(
        FileTransferRecord record,
        string? message,
        bool force,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (!force && _lastProgressPushUtc.TryGetValue(record.TransferId, out var lastPush)
            && (now - lastPush).TotalMilliseconds < 150)
        {
            return;
        }

        _lastProgressPushUtc[record.TransferId] = now;

        var connectionIds = _presenceTracker
            .GetConnectionIdsForDevice(record.SourceDeviceId)
            .Concat(_presenceTracker.GetConnectionIdsForDevice(record.TargetDeviceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (connectionIds.Length == 0)
        {
            return;
        }

        var progressEvent = new FileTransferProgressEvent
        {
            TransferId = record.TransferId,
            CorrelationId = record.CorrelationId,
            SourceDeviceId = record.SourceDeviceId,
            TargetDeviceId = record.TargetDeviceId,
            FileName = record.SafeFileName,
            TotalBytes = record.TotalBytes,
            TransferredBytes = record.TransferredBytes,
            BytesPerSecond = record.BytesPerSecond,
            PercentComplete = record.PercentComplete,
            State = record.State,
            Stage = record.Stage,
            FailureReason = record.FailureReason,
            Message = message,
            TimestampUtc = now
        };

        try
        {
            await _hubContext.Clients
                .Clients(connectionIds)
                .ReceiveTransferProgress(progressEvent)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha não crítica ao notificar progresso via SignalR para {TransferId}.", record.TransferId);
        }
    }

    private long GetSafeFreeBytes()
    {
        var res = _storage.CheckAvailableDiskSpace(_storage.StagingDirectory, 0, 0);
        return res.IsSuccess ? res.Value : 0L;
    }
}

