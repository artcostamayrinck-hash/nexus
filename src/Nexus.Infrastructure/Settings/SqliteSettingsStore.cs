using System.Globalization;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Settings;

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;
    private readonly IJsonSerializer _serializer;
    private readonly IClock _clock;

    public SqliteSettingsStore(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler,
        IJsonSerializer serializer,
        IClock clock)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
        _serializer = serializer;
        _clock = clock;
    }

    public async Task<Result<T?>> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Result<T?>.Failure(NexusError.Validation("Setting key cannot be empty.", code: "SETTING_KEY_EMPTY"));
        }

        return await _failureHandler.ExecuteAsync<T?>("Settings.Get", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ValueJson FROM LocalSettings WHERE Key = $key;";
            cmd.Parameters.AddWithValue("$key", key.Trim());

            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            return scalar is null ? default : _serializer.Deserialize<T>(scalar);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<T>> GetOrDefaultAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result<T>.Failure(result.Error!);
        }

        return Result<T>.Success(result.Value is null ? defaultValue : result.Value);
    }

    public Task<Result> SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Task.FromResult(Result.Failure(NexusError.Validation("Setting key cannot be empty.", code: "SETTING_KEY_EMPTY")));
        }

        return _failureHandler.ExecuteAsync("Settings.Set", async ct =>
        {
            var json = _serializer.Serialize(value);
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO LocalSettings (Key, ValueJson, UpdatedAtUtc)
                VALUES ($key, $value, $updated)
                ON CONFLICT(Key) DO UPDATE SET
                    ValueJson = excluded.ValueJson,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$key", key.Trim());
            cmd.Parameters.AddWithValue("$value", json);
            cmd.Parameters.AddWithValue("$updated", _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Task.FromResult(Result<bool>.Failure(NexusError.Validation("Setting key cannot be empty.", code: "SETTING_KEY_EMPTY")));
        }

        return _failureHandler.ExecuteAsync("Settings.Remove", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM LocalSettings WHERE Key = $key;";
            cmd.Parameters.AddWithValue("$key", key.Trim());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyDictionary<string, string>>> GetAllRawAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyDictionary<string, string>>("Settings.GetAll", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Key, ValueJson FROM LocalSettings ORDER BY Key ASC;";

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                dict[reader.GetString(0)] = reader.GetString(1);
            }

            return dict;
        }, cancellationToken);
    }
}

