using Microsoft.Extensions.Logging;
using Nexus.Contracts.Sync;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Core.Sync;

namespace Nexus.Infrastructure.Sync;

public sealed class SyncPrivacySettingsService : ISyncPrivacySettingsService
{
    public const string SettingsKey = "nexus.sync.privacy.v1";

    private readonly ISettingsStore _settingsStore;
    private readonly IClock _clock;
    private readonly ILogger<SyncPrivacySettingsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private SyncPrivacyPreferences? _cached;

    public SyncPrivacySettingsService(
        ISettingsStore settingsStore,
        IClock clock,
        ILogger<SyncPrivacySettingsService> logger)
    {
        _settingsStore = settingsStore;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<SyncPrivacyPreferences>? PreferencesChanged;

    public async Task<Result<SyncPrivacyPreferences>> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return Result<SyncPrivacyPreferences>.Success(_cached);
            }

            var stored = await _settingsStore
                .GetOrDefaultAsync(SettingsKey, new SyncPrivacyPreferences(), cancellationToken)
                .ConfigureAwait(false);

            if (stored.IsFailure)
            {
                return stored;
            }

            _cached = stored.Value;
            return Result<SyncPrivacyPreferences>.Success(_cached);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<SyncPrivacyPreferences>> SavePreferencesAsync(
        SyncPrivacyPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var errors = preferences.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Result<SyncPrivacyPreferences>.Failure(
                NexusError.Validation(errors[0].Message, code: errors[0].Code));
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        SyncPrivacyPreferences updated;
        try
        {
            updated = preferences with
            {
                ClipboardMaxHistoryItems = Math.Clamp(preferences.ClipboardMaxHistoryItems, 1, 500),
                UpdatedAtUtc = _clock.UtcNow
            };

            var persistResult = await _settingsStore
                .SetAsync(SettingsKey, updated, cancellationToken)
                .ConfigureAwait(false);

            if (persistResult.IsFailure)
            {
                return Result<SyncPrivacyPreferences>.Failure(persistResult.Error!);
            }

            _cached = updated;
            _logger.LogInformation(
                "Saved sync privacy preferences: ClipboardEnabled={ClipboardEnabled}, NotesEnabled={NotesEnabled}, HistoryLimit={HistoryLimit}",
                updated.ClipboardSyncEnabled,
                updated.NotesSyncEnabled,
                updated.ClipboardMaxHistoryItems);
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, updated);
        return Result<SyncPrivacyPreferences>.Success(updated);
    }
}

