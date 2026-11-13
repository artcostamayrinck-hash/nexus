using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Nexus.Core.Errors;

namespace Nexus.Infrastructure.Persistence;

public sealed class NexusStorageOptions
{
    public string RootDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXUS");

    public string DatabaseFileName { get; set; } = "nexus.db";
    public bool EnableWalMode { get; set; } = true;
    public bool EnablePooling { get; set; } = true;
    public int BusyTimeoutMs { get; set; } = 5000;

    public string DatabasePath => Path.Combine(RootDirectory, DatabaseFileName);
}

public interface ISqliteConnectionFactory
{
    string DatabasePath { get; }
    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly NexusStorageOptions _options;

    public SqliteConnectionFactory(NexusStorageOptions options)
    {
        _options = options;
    }

    public string DatabasePath => _options.DatabasePath;

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = _options.EnablePooling,
            DefaultTimeout = Math.Max(1, _options.BusyTimeoutMs / 1000)
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = _options.EnableWalMode
            ? $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {_options.BusyTimeoutMs}; PRAGMA journal_mode = WAL;"
            : $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {_options.BusyTimeoutMs};";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}

/// <summary>
/// Centralizes SQLite and filesystem exception translation with transient busy retries.
/// </summary>
public sealed class StorageFailureHandler
{
    private readonly ILogger<StorageFailureHandler> _logger;

    public StorageFailureHandler(ILogger<StorageFailureHandler> logger)
    {
        _logger = logger;
    }

    public async Task<Result<T>> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var value = await operation(cancellationToken).ConfigureAwait(false);
                return Result<T>.Success(value);
            }
            catch (OperationCanceledException)
            {
                return Result<T>.Failure(NexusError.Cancelled($"Storage operation '{operationName}' was cancelled."));
            }
            catch (SqliteException ex) when (attempt < maxAttempts && ex.SqliteErrorCode is 5 or 6)
            {
                _logger.LogWarning(ex, "Transient SQLite lock during {Operation} (attempt {Attempt}/{Max})", operationName, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                var error = MapSqliteException(operationName, ex);
                _logger.LogError(ex, "SQLite failure during {Operation}: {Code} - {Message}", operationName, error.Code, error.Message);
                return Result<T>.Failure(error);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Permission denied during storage operation {Operation}", operationName);
                return Result<T>.Failure(NexusError.Storage(
                    $"Access denied during '{operationName}'.",
                    "STORAGE_ACCESS_DENIED",
                    ex.Message,
                    ex));
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "I/O failure during storage operation {Operation}", operationName);
                return Result<T>.Failure(NexusError.Storage(
                    $"Filesystem I/O error during '{operationName}'.",
                    "STORAGE_IO_ERROR",
                    ex.Message,
                    ex));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure during storage operation {Operation}", operationName);
                return Result<T>.Failure(NexusError.Storage(
                    $"Unexpected storage failure during '{operationName}'.",
                    "STORAGE_UNEXPECTED_ERROR",
                    ex.Message,
                    ex));
            }
        }

        return Result<T>.Failure(NexusError.Storage($"Storage operation '{operationName}' failed after retries.", "STORAGE_RETRY_EXHAUSTED"));
    }

    public async Task<Result> ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(operationName, async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!);
    }

    private static NexusError MapSqliteException(string operationName, SqliteException ex)
    {
        var (code, message) = ex.SqliteErrorCode switch
        {
            5 or 6 => ("SQLITE_BUSY", $"SQLite database is locked during '{operationName}'."),
            8 => ("SQLITE_READONLY", $"SQLite database is read-only during '{operationName}'."),
            11 => ("SQLITE_CORRUPT", $"SQLite database integrity error detected during '{operationName}'."),
            13 => ("SQLITE_DISK_FULL", $"Insufficient disk space for '{operationName}'."),
            14 => ("SQLITE_CANTOPEN", $"Unable to open SQLite database file during '{operationName}'."),
            19 => ("SQLITE_CONSTRAINT", $"Database constraint violation during '{operationName}'."),
            _ => ("SQLITE_ERROR", $"SQLite error ({ex.SqliteErrorCode}) during '{operationName}'.")
        };

        return NexusError.Storage(message, code, ex.Message, ex);
    }
}

