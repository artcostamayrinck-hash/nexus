using System.Collections.Concurrent;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;

namespace Nexus.Security.Replay;

public interface INonceReplayProtector
{
    Result ValidateAndRecord(string scope, Guid deviceId, DateTimeOffset timestampUtc, string nonce);
}

/// <summary>
/// Prevents request replay attacks using bounded timestamp skew and single-use nonces.
/// </summary>
public sealed class InMemoryNonceReplayProtector : INonceReplayProtector
{
    private readonly IClock _clock;
    private readonly TimeSpan _allowedSkew;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenNonces = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCleanupUtc = DateTimeOffset.MinValue;

    public InMemoryNonceReplayProtector(IClock clock, TimeSpan? allowedSkew = null)
    {
        _clock = clock;
        _allowedSkew = allowedSkew ?? TimeSpan.FromMinutes(5);
    }

    public Result ValidateAndRecord(string scope, Guid deviceId, DateTimeOffset timestampUtc, string nonce)
    {
        if (string.IsNullOrWhiteSpace(scope) || deviceId == Guid.Empty)
        {
            return Result.Failure(NexusError.Validation("Scope and DeviceId are required for replay validation.", code: "REPLAY_INPUT_INVALID"));
        }

        if (string.IsNullOrWhiteSpace(nonce) || nonce.Trim().Length < 16)
        {
            return Result.Failure(NexusError.Validation("Nonce must contain at least 16 characters.", code: "NONCE_TOO_SHORT"));
        }

        var now = _clock.UtcNow;
        var delta = (now - timestampUtc).Duration();
        if (delta > _allowedSkew)
        {
            return Result.Failure(NexusError.Unauthorized(
                $"Request timestamp outside allowed window ({_allowedSkew.TotalMinutes:F0} min).",
                "REPLAY_TIMESTAMP_SKEW"));
        }

        PruneExpiredIfNeeded(now);

        var compositeKey = $"{scope.Trim()}:{deviceId:D}:{nonce.Trim()}";
        var expiresAt = now.Add(_allowedSkew).AddMinutes(1);
        if (!_seenNonces.TryAdd(compositeKey, expiresAt))
        {
            return Result.Failure(NexusError.Unauthorized("Replay detected: nonce has already been used.", "REPLAY_NONCE_DETECTED"));
        }

        return Result.Success();
    }

    private void PruneExpiredIfNeeded(DateTimeOffset now)
    {
        if ((now - _lastCleanupUtc) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCleanupUtc = now;
        foreach (var entry in _seenNonces)
        {
            if (entry.Value <= now)
            {
                _seenNonces.TryRemove(entry.Key, out _);
            }
        }
    }
}

