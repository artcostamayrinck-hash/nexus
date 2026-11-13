using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Persistence;
using Nexus.Security.Validation;

namespace Nexus.Infrastructure.Files;

/// <summary>
/// Manages internal NEXUS storage buckets safely with path-traversal protection and atomic writes.
/// </summary>
public sealed class LocalFileStorageService : IFileStorageService
{
    private readonly NexusStorageOptions _options;
    private readonly StorageFailureHandler _failureHandler;

    public LocalFileStorageService(NexusStorageOptions options, StorageFailureHandler failureHandler)
    {
        _options = options;
        _failureHandler = failureHandler;
    }

    public string RootDirectory => Path.GetFullPath(_options.RootDirectory);

    public Result<string> ResolveSafePath(string bucket, string relativePath)
    {
        var bucketCheck = InputSanitizer.SanitizeRelativePath(bucket);
        if (bucketCheck.IsFailure)
        {
            return Result<string>.Failure(bucketCheck.Error!);
        }

        var pathCheck = InputSanitizer.SanitizeRelativePath(relativePath);
        if (pathCheck.IsFailure)
        {
            return Result<string>.Failure(pathCheck.Error!);
        }

        var baseRoot = Path.GetFullPath(Path.Combine(RootDirectory, "files"));
        var combined = Path.GetFullPath(Path.Combine(baseRoot, bucketCheck.Value, pathCheck.Value));

        var prefix = baseRoot.EndsWith(Path.DirectorySeparatorChar)
            ? baseRoot
            : baseRoot + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Failure(NexusError.Validation("Resolved path escapes storage root.", code: "PATH_ESCAPE_FORBIDDEN"));
        }

        return Result<string>.Success(combined);
    }

    public Task<Result<string>> WriteBytesAsync(
        string bucket,
        string relativePath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var pathResult = ResolveSafePath(bucket, relativePath);
        if (pathResult.IsFailure)
        {
            return Task.FromResult(Result<string>.Failure(pathResult.Error!));
        }

        return _failureHandler.ExecuteAsync("Files.WriteBytes", async ct =>
        {
            var targetPath = pathResult.Value;
            var directory = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(directory, $".tmp-{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllBytesAsync(tempPath, content, ct).ConfigureAwait(false);
                File.Move(tempPath, targetPath, overwrite: true);
                return targetPath;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }, cancellationToken);
    }

    public Task<Result<byte[]>> ReadBytesAsync(string bucket, string relativePath, CancellationToken cancellationToken = default)
    {
        var pathResult = ResolveSafePath(bucket, relativePath);
        if (pathResult.IsFailure)
        {
            return Task.FromResult(Result<byte[]>.Failure(pathResult.Error!));
        }

        if (!File.Exists(pathResult.Value))
        {
            return Task.FromResult(Result<byte[]>.Failure(NexusError.NotFound($"Internal file '{bucket}/{relativePath}' does not exist.", "FILE_NOT_FOUND")));
        }

        return _failureHandler.ExecuteAsync("Files.ReadBytes", ct => File.ReadAllBytesAsync(pathResult.Value, ct), cancellationToken);
    }

    public Task<Result<bool>> ExistsAsync(string bucket, string relativePath, CancellationToken cancellationToken = default)
    {
        var pathResult = ResolveSafePath(bucket, relativePath);
        if (pathResult.IsFailure)
        {
            return Task.FromResult(Result<bool>.Failure(pathResult.Error!));
        }

        return Task.FromResult(Result<bool>.Success(File.Exists(pathResult.Value)));
    }

    public Task<Result<bool>> DeleteAsync(string bucket, string relativePath, CancellationToken cancellationToken = default)
    {
        var pathResult = ResolveSafePath(bucket, relativePath);
        if (pathResult.IsFailure)
        {
            return Task.FromResult(Result<bool>.Failure(pathResult.Error!));
        }

        return _failureHandler.ExecuteAsync("Files.Delete", _ =>
        {
            if (!File.Exists(pathResult.Value))
            {
                return Task.FromResult(false);
            }

            File.Delete(pathResult.Value);
            return Task.FromResult(true);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<string>>> ListFilesAsync(string bucket, CancellationToken cancellationToken = default)
    {
        var bucketCheck = InputSanitizer.SanitizeRelativePath(bucket);
        if (bucketCheck.IsFailure)
        {
            return Task.FromResult(Result<IReadOnlyList<string>>.Failure(bucketCheck.Error!));
        }

        return _failureHandler.ExecuteAsync<IReadOnlyList<string>>("Files.List", _ =>
        {
            var bucketDir = Path.Combine(RootDirectory, "files", bucketCheck.Value);
            if (!Directory.Exists(bucketDir))
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var files = Directory.EnumerateFiles(bucketDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(bucketDir, f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult<IReadOnlyList<string>>(files);
        }, cancellationToken);
    }

    public Task<Result<StorageStats>> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Files.Stats", _ =>
        {
            Directory.CreateDirectory(RootDirectory);
            var filesDir = Path.Combine(RootDirectory, "files");
            var dbSize = File.Exists(_options.DatabasePath) ? new FileInfo(_options.DatabasePath).Length : 0L;

            var fileCount = 0;
            var totalBytes = 0L;
            if (Directory.Exists(filesDir))
            {
                foreach (var file in Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    totalBytes += new FileInfo(file).Length;
                }
            }

            return Task.FromResult(new StorageStats(
                RootDirectory,
                dbSize,
                fileCount,
                totalBytes,
                IsAvailable: Directory.Exists(RootDirectory)));
        }, cancellationToken);
    }
}

