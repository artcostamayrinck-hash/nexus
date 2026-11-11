using System.Security.Cryptography;
using Nexus.Contracts.Transfers;
using Nexus.Core.Errors;
using Nexus.Core.Transfers;
using Nexus.Infrastructure.Persistence;
using Nexus.Security.Validation;

namespace Nexus.Infrastructure.Transfers;

public sealed class TransferStoragePolicyOptions
{
    public Func<string, long>? AvailableFreeSpaceProvider { get; set; }
}

/// <summary>
/// Manages safe staging/received directories, filename sanitization, disk space checks,
/// chunked .part offset writes, duplicate policies, and SHA-256 verification.
/// </summary>
public sealed class SafeTransferStorageProvider : ITransferStorageProvider
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly NexusStorageOptions _storageOptions;
    private readonly TransferStoragePolicyOptions _policyOptions;

    public SafeTransferStorageProvider(
        NexusStorageOptions storageOptions,
        TransferStoragePolicyOptions? policyOptions = null)
    {
        _storageOptions = storageOptions;
        _policyOptions = policyOptions ?? new TransferStoragePolicyOptions();
    }

    public string StagingDirectory => Path.GetFullPath(Path.Combine(_storageOptions.RootDirectory, "transfers", "staging"));
    public string ReceivedDirectory => Path.GetFullPath(Path.Combine(_storageOptions.RootDirectory, "transfers", "received"));

    public Result<string> SanitizeFileName(string rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return Result<string>.Failure(
                NexusError.Validation("Nome do arquivo não pode estar vazio.", code: "TRANSFER_FILENAME_EMPTY"));
        }

        var trimmed = rawFileName.Trim();
        if (trimmed.IndexOfAny(['/', '\\', '\0', ':']) >= 0 || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return Result<string>.Failure(
                NexusError.Validation("Nome do arquivo contém separadores de caminho ou sequência inválida.", code: "TRANSFER_FILENAME_UNSAFE"));
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        if (trimmed.IndexOfAny(invalidChars) >= 0)
        {
            return Result<string>.Failure(
                NexusError.Validation("Nome do arquivo contém caracteres proibidos pelo sistema de arquivos.", code: "TRANSFER_FILENAME_INVALID"));
        }

        var stem = Path.GetFileNameWithoutExtension(trimmed);
        if (ReservedDeviceNames.Contains(trimmed) || ReservedDeviceNames.Contains(stem))
        {
            return Result<string>.Failure(
                NexusError.Validation("Nome do arquivo reservado pelo sistema operacional não é permitido.", code: "TRANSFER_FILENAME_RESERVED"));
        }

        var relativeCheck = InputSanitizer.SanitizeRelativePath(trimmed);
        if (relativeCheck.IsFailure)
        {
            return Result<string>.Failure(relativeCheck.Error!);
        }

        var fileNameOnly = Path.GetFileName(relativeCheck.Value);
        if (!string.Equals(fileNameOnly, trimmed, StringComparison.Ordinal))
        {
            return Result<string>.Failure(
                NexusError.Validation("Caminho relativo aninhado não é permitido como nome de arquivo.", code: "TRANSFER_FILENAME_UNSAFE"));
        }

        return Result<string>.Success(fileNameOnly);
    }

    public Result<string> ResolveSafeStagingPath(Guid transferId, string safeFileName, bool isPartFile)
    {
        if (transferId == Guid.Empty)
        {
            return Result<string>.Failure(
                NexusError.Validation("TransferId inválido.", code: "TRANSFER_ID_EMPTY"));
        }

        var nameResult = SanitizeFileName(safeFileName);
        if (nameResult.IsFailure)
        {
            return Result<string>.Failure(nameResult.Error!);
        }

        var suffix = isPartFile ? ".part" : string.Empty;
        var stagedName = $"{transferId:N}_{nameResult.Value}{suffix}";
        return EnsureContainedPath(StagingDirectory, stagedName);
    }

    public Result<DuplicateResolutionResult> ResolveSafeReceivedDestination(
        string safeFileName,
        TransferDuplicatePolicy duplicatePolicy)
    {
        var nameResult = SanitizeFileName(safeFileName);
        if (nameResult.IsFailure)
        {
            return Result<DuplicateResolutionResult>.Failure(nameResult.Error!);
        }

        var sanitized = nameResult.Value;
        Directory.CreateDirectory(ReceivedDirectory);

        var initialPathResult = EnsureContainedPath(ReceivedDirectory, sanitized);
        if (initialPathResult.IsFailure)
        {
            return Result<DuplicateResolutionResult>.Failure(initialPathResult.Error!);
        }

        var candidatePath = initialPathResult.Value;
        if (!File.Exists(candidatePath))
        {
            return Result<DuplicateResolutionResult>.Success(
                new DuplicateResolutionResult(sanitized, candidatePath, ShouldSkip: false));
        }

        switch (duplicatePolicy)
        {
            case TransferDuplicatePolicy.Skip:
                return Result<DuplicateResolutionResult>.Success(
                    new DuplicateResolutionResult(sanitized, candidatePath, ShouldSkip: true));

            case TransferDuplicatePolicy.Overwrite:
                return Result<DuplicateResolutionResult>.Success(
                    new DuplicateResolutionResult(sanitized, candidatePath, ShouldSkip: false));

            case TransferDuplicatePolicy.Rename:
            default:
                var stem = Path.GetFileNameWithoutExtension(sanitized);
                var ext = Path.GetExtension(sanitized);
                for (var index = 1; index <= 9999; index++)
                {
                    var numberedName = $"{stem} ({index}){ext}";
                    var numberedPathResult = EnsureContainedPath(ReceivedDirectory, numberedName);
                    if (numberedPathResult.IsFailure)
                    {
                        return Result<DuplicateResolutionResult>.Failure(numberedPathResult.Error!);
                    }

                    if (!File.Exists(numberedPathResult.Value))
                    {
                        return Result<DuplicateResolutionResult>.Success(
                            new DuplicateResolutionResult(numberedName, numberedPathResult.Value, ShouldSkip: false));
                    }
                }

                return Result<DuplicateResolutionResult>.Failure(
                    NexusError.Conflict("Limite máximo de cópias duplicadas atingido para este arquivo.", code: "TRANSFER_DUPLICATE_LIMIT"));
        }
    }

    public Result<long> CheckAvailableDiskSpace(string targetDirectory, long requiredBytes, long safetyMarginBytes)
    {
        if (requiredBytes < 0 || safetyMarginBytes < 0)
        {
            return Result<long>.Failure(
                NexusError.Validation("Tamanho de espaço requerido inválido.", code: "DISK_SPACE_INVALID_ARGUMENT"));
        }

        try
        {
            var fullDir = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(fullDir);

            long availableBytes;
            if (_policyOptions.AvailableFreeSpaceProvider is not null)
            {
                availableBytes = _policyOptions.AvailableFreeSpaceProvider(fullDir);
            }
            else
            {
                var root = Path.GetPathRoot(fullDir);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return Result<long>.Failure(
                        NexusError.Storage("Não foi possível identificar o volume de destino para verificar espaço em disco.", code: "DISK_VOLUME_NOT_FOUND"));
                }

                var drive = new DriveInfo(root);
                availableBytes = drive.AvailableFreeSpace;
            }

            if (availableBytes < safetyMarginBytes || (availableBytes - safetyMarginBytes) < requiredBytes)
            {
                return Result<long>.Failure(
                    NexusError.Storage(
                        $"Espaço insuficiente em disco em '{fullDir}'. Disponível: {availableBytes} bytes; necessário: {requiredBytes} bytes (margem {safetyMarginBytes} bytes).",
                        code: "INSUFFICIENT_DISK_SPACE"));
            }

            return Result<long>.Success(availableBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotFoundException or ArgumentException)
        {
            return Result<long>.Failure(
                NexusError.Storage($"Falha ao verificar espaço disponível em disco: {ex.Message}", code: "DISK_SPACE_CHECK_FAILED"));
        }
    }

    public async Task<Result<string>> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Result<string>.Failure(
                    NexusError.NotFound($"Arquivo '{filePath}' não encontrado para verificação de integridade.", code: "FILE_NOT_FOUND"));
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Result<string>.Success(Convert.ToHexStringLower(hashBytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(
                NexusError.Storage($"Erro ao calcular SHA-256 de '{filePath}': {ex.Message}", code: "CHECKSUM_CALC_FAILED"));
        }
    }

    public async Task<Result<long>> WriteChunkAtOffsetAsync(
        string partFilePath,
        long offset,
        Stream dataStream,
        long? expectedTotalBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataStream);

        if (offset < 0)
        {
            return Result<long>.Failure(
                NexusError.Validation("Offset do chunk não pode ser negativo.", code: "TRANSFER_OFFSET_INVALID"));
        }

        try
        {
            var fullPath = Path.GetFullPath(partFilePath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using var fileStream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous);

            if (offset > fileStream.Length)
            {
                return Result<long>.Failure(
                    NexusError.Validation(
                        $"Offset {offset} excede o tamanho atual gravado ({fileStream.Length}).",
                        code: "TRANSFER_OFFSET_OUT_OF_ORDER"));
            }

            fileStream.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[64 * 1024];
            long writtenFromOffset = 0;
            while (true)
            {
                var read = await dataStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (expectedTotalBytes.HasValue && (offset + writtenFromOffset + read) > expectedTotalBytes.Value)
                {
                    return Result<long>.Failure(
                        NexusError.Validation(
                            "Dados recebidos excedem o tamanho total declarado do arquivo.",
                            code: "TRANSFER_SIZE_EXCEEDED"));
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                writtenFromOffset += read;
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Result<long>.Success(fileStream.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<long>.Failure(
                NexusError.Storage($"Falha ao gravar chunk em arquivo temporário: {ex.Message}", code: "TRANSFER_CHUNK_WRITE_FAILED"));
        }
    }

    public async Task<Result<string>> FinalizeVerifiedFileAsync(
        string partFilePath,
        string finalDestinationPath,
        string expectedSha256Hex,
        TransferDuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPart = Path.GetFullPath(partFilePath);
            if (!File.Exists(fullPart))
            {
                return Result<string>.Failure(
                    NexusError.NotFound("Arquivo temporário (.part) não encontrado para finalização.", code: "TRANSFER_PART_MISSING"));
            }

            var hashResult = await ComputeSha256HexAsync(fullPart, cancellationToken).ConfigureAwait(false);
            if (hashResult.IsFailure)
            {
                return Result<string>.Failure(hashResult.Error!);
            }

            if (!string.Equals(hashResult.Value, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(fullPart);
                return Result<string>.Failure(
                    NexusError.Conflict(
                        $"Verificação SHA-256 falhou. Esperado: {expectedSha256Hex}; obtido: {hashResult.Value}.",
                        code: "CHECKSUM_MISMATCH"));
            }

            var targetName = Path.GetFileName(finalDestinationPath);
            var targetDir = Path.GetDirectoryName(Path.GetFullPath(finalDestinationPath));
            var safePath = finalDestinationPath;

            if (string.Equals(targetDir, ReceivedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var resolution = ResolveSafeReceivedDestination(targetName, duplicatePolicy);
                if (resolution.IsFailure)
                {
                    return Result<string>.Failure(resolution.Error!);
                }

                if (resolution.Value.ShouldSkip)
                {
                    TryDeleteFile(fullPart);
                    return Result<string>.Failure(
                        NexusError.Conflict($"Arquivo '{targetName}' já existe e foi ignorado pela política de duplicados.", code: "DUPLICATE_SKIPPED"));
                }

                safePath = resolution.Value.FullPath;
            }
            else if (string.Equals(targetDir, StagingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var contained = EnsureContainedPath(StagingDirectory, targetName);
                if (contained.IsFailure)
                {
                    return Result<string>.Failure(contained.Error!);
                }

                safePath = contained.Value;
            }
            else
            {
                return Result<string>.Failure(
                    NexusError.Forbidden("Destino fora dos diretórios seguros de transferência é proibido.", code: "PATH_ESCAPE_FORBIDDEN"));
            }

            var parentDir = Path.GetDirectoryName(safePath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            File.Move(fullPart, safePath, overwrite: true);
            return Result<string>.Success(safePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(
                NexusError.Storage($"Falha ao finalizar arquivo transferido: {ex.Message}", code: "TRANSFER_FINALIZE_FAILED"));
        }
    }

    public Task<Result> DeleteSafeFileIfExistsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.FromResult(Result.Success());
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!IsInsideDirectory(fullPath, StagingDirectory) && !IsInsideDirectory(fullPath, ReceivedDirectory))
            {
                return Task.FromResult(Result.Failure(
                    NexusError.Forbidden("Exclusão fora dos diretórios de transferência é proibida.", code: "PATH_ESCAPE_FORBIDDEN")));
            }

            TryDeleteFile(fullPath);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(
                NexusError.Storage($"Não foi possível remover arquivo '{filePath}': {ex.Message}", code: "TRANSFER_DELETE_FAILED")));
        }
    }

    public long GetExistingFileLength(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return 0;
        }

        return new FileInfo(filePath).Length;
    }

    private static Result<string> EnsureContainedPath(string baseDirectory, string safeFileName)
    {
        var baseFull = Path.GetFullPath(baseDirectory);
        var combined = Path.GetFullPath(Path.Combine(baseFull, safeFileName));
        if (!IsInsideDirectory(combined, baseFull))
        {
            return Result<string>.Failure(
                NexusError.Forbidden("Escapamento de caminho detectado fora do diretório permitido.", code: "PATH_ESCAPE_FORBIDDEN"));
        }

        return Result<string>.Success(combined);
    }

    private static bool IsInsideDirectory(string fullPath, string baseDirectory)
    {
        var baseFull = Path.GetFullPath(baseDirectory);
        var prefix = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Melhor esforço na limpeza de .part corrompido ou cancelado.
        }
    }
}

