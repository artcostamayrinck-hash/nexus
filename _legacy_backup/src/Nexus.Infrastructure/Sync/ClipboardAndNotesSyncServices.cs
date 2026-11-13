using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Events;
using Nexus.Contracts.Sync;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Sync;
using Nexus.Security.Identity;

namespace Nexus.Infrastructure.Sync;

internal static class SyncJsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}

public sealed class ClipboardSyncCoordinator : IClipboardSyncService, IDisposable
{
    private static readonly TimeSpan CycleSuppressionWindow = TimeSpan.FromSeconds(12);

    private readonly ISystemClipboardAdapter _clipboardAdapter;
    private readonly IClipboardRepository _clipboardRepo;
    private readonly ISyncQueueRepository _queueRepo;
    private readonly ISyncPrivacySettingsService _privacySettings;
    private readonly INexusSyncTransport _transport;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly IClock _clock;
    private readonly ILogger<ClipboardSyncCoordinator> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _suppressedHashes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public ClipboardSyncCoordinator(
        ISystemClipboardAdapter clipboardAdapter,
        IClipboardRepository clipboardRepo,
        ISyncQueueRepository queueRepo,
        ISyncPrivacySettingsService privacySettings,
        INexusSyncTransport transport,
        ICryptographicIdentityProvider identityProvider,
        IClock clock,
        ILogger<ClipboardSyncCoordinator> logger)
    {
        _clipboardAdapter = clipboardAdapter;
        _clipboardRepo = clipboardRepo;
        _queueRepo = queueRepo;
        _privacySettings = privacySettings;
        _transport = transport;
        _identityProvider = identityProvider;
        _clock = clock;
        _logger = logger;

        _clipboardAdapter.SystemClipboardTextCaptured += OnSystemClipboardCaptured;
    }

    public bool IsCycleGuardActive
    {
        get
        {
            PruneExpiredSuppressions();
            return !_suppressedHashes.IsEmpty;
        }
    }

    public string PlatformCapabilitySummary => _clipboardAdapter.PlatformRestrictionNotice;

    public event EventHandler<IReadOnlyList<ClipboardEntryContract>>? HistoryChanged;
    public event EventHandler<ClipboardEntryContract>? RemoteClipboardApplied;

    public async Task<Result<ClipboardEntryContract>> CaptureAndSendTextAsync(
        string text,
        bool manualTrigger = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result<ClipboardEntryContract>.Failure(
                NexusError.Validation("Não é possível enviar texto vazio para a área de transferência compartilhada.", code: "CLIPBOARD_TEXT_EMPTY"));
        }

        var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (prefsResult.IsFailure)
        {
            return Result<ClipboardEntryContract>.Failure(prefsResult.Error!);
        }

        var prefs = prefsResult.Value;
        if (!prefs.ClipboardSyncEnabled)
        {
            return Result<ClipboardEntryContract>.Failure(
                NexusError.Forbidden(
                    "Sincronização do NEXUS Clipboard desativada nas preferências de privacidade.",
                    code: "CLIPBOARD_SYNC_DISABLED"));
        }

        var hash = SyncContentHashHelper.ComputeUtf8Sha256(text);
        if (!manualTrigger && IsHashSuppressed(hash))
        {
            _logger.LogDebug("Ignoring OS clipboard capture for hash {Hash} due to active sync cycle guard.", hash[..Math.Min(8, hash.Length)]);
            return Result<ClipboardEntryContract>.Failure(
                new NexusError("CLIPBOARD_CYCLE_SUPPRESSED", "Captura ignorada para evitar ciclo de sincronização.", ErrorCategory.Conflict));
        }

        if (!manualTrigger)
        {
            var latestResult = await _clipboardRepo.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            if (latestResult.IsSuccess &&
                latestResult.Value is { } latest &&
                string.Equals(latest.ContentHash, hash, StringComparison.Ordinal) &&
                (latest.IsRemoteReceived || (_clock.UtcNow - latest.CapturedAtUtc) < TimeSpan.FromSeconds(5)))
            {
                return Result<ClipboardEntryContract>.Failure(
                    new NexusError("CLIPBOARD_DUPLICATE_IGNORED", "Conteúdo idêntico recém-processado ignorado.", ErrorCategory.Conflict));
            }
        }

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<ClipboardEntryContract>.Failure(identityResult.Error!);
        }

        var identity = identityResult.Value;
        var entry = new ClipboardEntryContract
        {
            EntryId = Guid.CreateVersion7(),
            OriginDeviceId = identity.DeviceId,
            OriginDeviceName = identity.CustomName,
            ContentText = text,
            ContentHash = hash,
            SyncOriginToken = Guid.CreateVersion7(),
            CapturedAtUtc = _clock.UtcNow,
            IsRemoteReceived = false
        };

        var addResult = await _clipboardRepo.AddEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        if (addResult.IsFailure)
        {
            return Result<ClipboardEntryContract>.Failure(addResult.Error!);
        }

        await _clipboardRepo.TrimToLimitAsync(prefs.ClipboardMaxHistoryItems, cancellationToken).ConfigureAwait(false);

        if (prefs.ClipboardAutoSync && _transport.IsRealtimeAvailable)
        {
            var pushResult = await _transport.PushClipboardEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            if (pushResult.IsFailure)
            {
                await EnqueueClipboardAsync(entry, pushResult.Error!, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var status = _transport.IsRealtimeAvailable ? SyncQueueStatus.Pending : SyncQueueStatus.DeferredOffline;
            await EnqueueClipboardAsync(entry, status, lastError: null, cancellationToken).ConfigureAwait(false);
        }

        await PublishHistoryChangedAsync(prefs.ClipboardMaxHistoryItems, cancellationToken).ConfigureAwait(false);
        return Result<ClipboardEntryContract>.Success(entry);
    }

    public async Task<Result<ClipboardEntryContract?>> CaptureCurrentOsClipboardAsync(CancellationToken cancellationToken = default)
    {
        var readResult = await _clipboardAdapter.ReadTextAsync(cancellationToken).ConfigureAwait(false);
        if (readResult.IsFailure)
        {
            return Result<ClipboardEntryContract?>.Failure(readResult.Error!);
        }

        if (string.IsNullOrWhiteSpace(readResult.Value))
        {
            return Result<ClipboardEntryContract?>.Success(null);
        }

        var sent = await CaptureAndSendTextAsync(readResult.Value, manualTrigger: true, cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess
            ? Result<ClipboardEntryContract?>.Success(sent.Value)
            : Result<ClipboardEntryContract?>.Failure(sent.Error!);
    }

    public async Task<Result<bool>> ApplyRemoteEntryAsync(
        ClipboardEntryContract entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
            if (prefsResult.IsFailure)
            {
                return Result<bool>.Failure(prefsResult.Error!);
            }

            var prefs = prefsResult.Value;
            if (!prefs.ClipboardSyncEnabled)
            {
                return Result<bool>.Success(false);
            }

            var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (identityResult.IsFailure)
            {
                return Result<bool>.Failure(identityResult.Error!);
            }

            // Ignore echoes originating from this exact installation.
            if (entry.OriginDeviceId == identityResult.Value.DeviceId)
            {
                return Result<bool>.Success(false);
            }

            var latestResult = await _clipboardRepo.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            if (latestResult.IsSuccess &&
                latestResult.Value is { } latest &&
                (latest.EntryId == entry.EntryId ||
                 (string.Equals(latest.ContentHash, entry.ContentHash, StringComparison.Ordinal) &&
                  latest.SyncOriginToken == entry.SyncOriginToken)))
            {
                return Result<bool>.Success(false);
            }

            // Arm cycle guard BEFORE touching OS clipboard so synchronous change hooks are suppressed.
            _suppressedHashes[entry.ContentHash] = _clock.UtcNow.Add(CycleSuppressionWindow);

            var writeResult = await _clipboardAdapter.WriteTextAsync(entry.ContentText, cancellationToken).ConfigureAwait(false);
            if (writeResult.IsFailure)
            {
                _logger.LogWarning("Falha ao escrever texto remoto no clipboard do SO: {Error}", writeResult.Error?.Message);
            }

            var remoteStored = entry with { IsRemoteReceived = true };
            var addResult = await _clipboardRepo.AddEntryAsync(remoteStored, cancellationToken).ConfigureAwait(false);
            if (addResult.IsFailure)
            {
                return Result<bool>.Failure(addResult.Error!);
            }

            await _clipboardRepo.TrimToLimitAsync(prefs.ClipboardMaxHistoryItems, cancellationToken).ConfigureAwait(false);
            await PublishHistoryChangedAsync(prefs.ClipboardMaxHistoryItems, cancellationToken).ConfigureAwait(false);

            RemoteClipboardApplied?.Invoke(this, remoteStored);
            return Result<bool>.Success(true);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    public async Task<Result<IReadOnlyList<ClipboardEntryContract>>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        var prefs = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        var limit = prefs.IsSuccess ? prefs.Value.ClipboardMaxHistoryItems : 50;
        return await _clipboardRepo.ListHistoryAsync(limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        var cleared = await _clipboardRepo.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (cleared.IsSuccess)
        {
            HistoryChanged?.Invoke(this, Array.Empty<ClipboardEntryContract>());
        }

        return cleared;
    }

    public async Task<Result<int>> RefreshFromServerAsync(CancellationToken cancellationToken = default)
    {
        var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (prefsResult.IsFailure)
        {
            return Result<int>.Failure(prefsResult.Error!);
        }

        var prefs = prefsResult.Value;
        if (!prefs.ClipboardSyncEnabled)
        {
            return Result<int>.Success(0);
        }

        var pulled = await _transport.PullClipboardHistoryAsync(prefs.ClipboardMaxHistoryItems, cancellationToken).ConfigureAwait(false);
        if (pulled.IsFailure)
        {
            return Result<int>.Failure(pulled.Error!);
        }

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<int>.Failure(identityResult.Error!);
        }

        var localId = identityResult.Value.DeviceId;
        var applied = 0;

        foreach (var entry in pulled.Value.OrderBy(e => e.CapturedAtUtc))
        {
            var isSelf = entry.OriginDeviceId == localId;
            await _clipboardRepo.AddEntryAsync(entry with { IsRemoteReceived = !isSelf }, cancellationToken).ConfigureAwait(false);
            applied++;
        }

        // Write only the newest remote item to the OS clipboard when pulling catch-up history.
        var newestRemote = pulled.Value
            .Where(e => e.OriginDeviceId != localId)
            .OrderByDescending(e => e.CapturedAtUtc)
            .FirstOrDefault();

        if (newestRemote is not null)
        {
            var latestLocal = await _clipboardRepo.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            if (latestLocal.IsSuccess && latestLocal.Value?.EntryId == newestRemote.EntryId)
            {
                _suppressedHashes[newestRemote.ContentHash] = _clock.UtcNow.Add(CycleSuppressionWindow);
                await _clipboardAdapter.WriteTextAsync(newestRemote.ContentText, cancellationToken).ConfigureAwait(false);
            }
        }

        await _clipboardRepo.TrimToLimitAsync(prefs.ClipboardMaxHistoryItems, cancellationToken).ConfigureAwait(false);
        await PublishHistoryChangedAsync(prefs.ClipboardMaxHistoryItems, cancellationToken).ConfigureAwait(false);
        return Result<int>.Success(applied);
    }

    internal async Task<Result<int>> FlushPendingQueueAsync(CancellationToken cancellationToken)
    {
        var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (prefsResult.IsFailure || !prefsResult.Value.ClipboardSyncEnabled)
        {
            return Result<int>.Success(0);
        }

        var pendingResult = await _queueRepo.ListPendingAsync("clipboard", cancellationToken).ConfigureAwait(false);
        if (pendingResult.IsFailure)
        {
            return Result<int>.Failure(pendingResult.Error!);
        }

        var flushed = 0;
        foreach (var item in pendingResult.Value)
        {
            ClipboardEntryContract? entry;
            try
            {
                entry = JsonSerializer.Deserialize<ClipboardEntryContract>(item.PayloadJson, SyncJsonOptions.Default);
            }
            catch
            {
                await _queueRepo.MarkCompletedAsync(item.QueueId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (entry is null)
            {
                await _queueRepo.MarkCompletedAsync(item.QueueId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var pushed = await _transport.PushClipboardEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            if (pushed.IsSuccess)
            {
                await _queueRepo.MarkCompletedAsync(item.QueueId, cancellationToken).ConfigureAwait(false);
                flushed++;
            }
            else
            {
                var status = pushed.Error?.Category == ErrorCategory.Unauthorized
                    ? SyncQueueStatus.DeferredPermissionDenied
                    : SyncQueueStatus.DeferredOffline;
                await _queueRepo.UpdateAttemptAsync(item.QueueId, status, pushed.Error?.Message, cancellationToken).ConfigureAwait(false);

                if (status == SyncQueueStatus.DeferredPermissionDenied)
                {
                    return Result<int>.Failure(pushed.Error!);
                }

                break;
            }
        }

        return Result<int>.Success(flushed);
    }

    private Task EnqueueClipboardAsync(ClipboardEntryContract entry, NexusError error, CancellationToken ct)
    {
        var status = error.Category == ErrorCategory.Unauthorized
            ? SyncQueueStatus.DeferredPermissionDenied
            : SyncQueueStatus.DeferredOffline;
        return EnqueueClipboardAsync(entry, status, error.Message, ct);
    }

    private Task EnqueueClipboardAsync(
        ClipboardEntryContract entry,
        SyncQueueStatus status,
        string? lastError,
        CancellationToken ct)
    {
        var queueItem = new SyncQueueItemRecord
        {
            QueueId = Guid.CreateVersion7(),
            Scope = "clipboard",
            OperationKind = SyncOperationKind.PushClipboard,
            EntityId = entry.EntryId,
            PayloadJson = JsonSerializer.Serialize(entry, SyncJsonOptions.Default),
            EnqueuedAtUtc = _clock.UtcNow,
            Status = status,
            LastError = lastError
        };

        return _queueRepo.EnqueueAsync(queueItem, ct);
    }

    private async Task PublishHistoryChangedAsync(int limit, CancellationToken ct)
    {
        var list = await _clipboardRepo.ListHistoryAsync(limit, ct).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            HistoryChanged?.Invoke(this, list.Value);
        }
    }

    private bool IsHashSuppressed(string contentHash)
    {
        PruneExpiredSuppressions();
        return _suppressedHashes.TryGetValue(contentHash, out var expiresAt) && expiresAt > _clock.UtcNow;
    }

    private void PruneExpiredSuppressions()
    {
        var now = _clock.UtcNow;
        foreach (var kvp in _suppressedHashes)
        {
            if (kvp.Value <= now)
            {
                _suppressedHashes.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void OnSystemClipboardCaptured(object? sender, string capturedText)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await CaptureAndSendTextAsync(capturedText, manualTrigger: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OS clipboard capture callback skipped.");
            }
        });
    }

    public void Dispose()
    {
        _clipboardAdapter.SystemClipboardTextCaptured -= OnSystemClipboardCaptured;
        _applyLock.Dispose();
    }
}

public sealed class NotesSyncCoordinator : INotesSyncService
{
    private readonly INotesRepository _notesRepo;
    private readonly ISyncQueueRepository _queueRepo;
    private readonly ISyncPrivacySettingsService _privacySettings;
    private readonly INexusSyncTransport _transport;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly IClock _clock;
    private readonly ILogger<NotesSyncCoordinator> _logger;

    public NotesSyncCoordinator(
        INotesRepository notesRepo,
        ISyncQueueRepository queueRepo,
        ISyncPrivacySettingsService privacySettings,
        INexusSyncTransport transport,
        ICryptographicIdentityProvider identityProvider,
        IClock clock,
        ILogger<NotesSyncCoordinator> logger)
    {
        _notesRepo = notesRepo;
        _queueRepo = queueRepo;
        _privacySettings = privacySettings;
        _transport = transport;
        _identityProvider = identityProvider;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler? NotesOrConflictsChanged;

    public async Task<Result<NexusNoteContract>> CreateNoteAsync(
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Result<NexusNoteContract>.Failure(
                NexusError.Validation("Título da nota é obrigatório.", code: "NOTE_TITLE_EMPTY"));
        }

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(identityResult.Error!);
        }

        var identity = identityResult.Value;
        var now = _clock.UtcNow;
        var cleanTitle = title.Trim();
        var cleanBody = body ?? string.Empty;

        var note = new NexusNoteContract
        {
            NoteId = Guid.CreateVersion7(),
            Title = cleanTitle,
            Body = cleanBody,
            Version = 1,
            BaseVersion = 0,
            ContentHash = SyncContentHashHelper.ComputeNoteHash(cleanTitle, cleanBody, isDeleted: false),
            LastModifiedByDeviceId = identity.DeviceId,
            LastModifiedByDeviceName = identity.CustomName,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
            IsDeleted = false,
            HasConflict = false,
            NeedsSync = true
        };

        var saved = await _notesRepo.UpsertNoteAsync(note, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await _notesRepo.AppendRevisionAsync(NoteConflictMergeEngine.CreateRevision(note, now, "created"), cancellationToken).ConfigureAwait(false);
        await EnqueueAndOptionallyAutoSyncAsync(note, SyncOperationKind.UpsertNote, cancellationToken).ConfigureAwait(false);

        NotesOrConflictsChanged?.Invoke(this, EventArgs.Empty);
        var refreshed = await _notesRepo.GetByIdAsync(note.NoteId, cancellationToken).ConfigureAwait(false);
        return Result<NexusNoteContract>.Success(refreshed.IsSuccess && refreshed.Value is not null ? refreshed.Value : saved.Value);
    }

    public async Task<Result<NexusNoteContract>> UpdateNoteAsync(
        Guid noteId,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Result<NexusNoteContract>.Failure(
                NexusError.Validation("Título da nota é obrigatório.", code: "NOTE_TITLE_EMPTY"));
        }

        var existingResult = await _notesRepo.GetByIdAsync(noteId, cancellationToken).ConfigureAwait(false);
        if (existingResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(existingResult.Error!);
        }

        if (existingResult.Value is null)
        {
            return Result<NexusNoteContract>.Failure(
                NexusError.NotFound($"Nota {noteId} não encontrada.", code: "NOTE_NOT_FOUND"));
        }

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(identityResult.Error!);
        }

        var identity = identityResult.Value;
        var existing = existingResult.Value;
        var now = _clock.UtcNow;
        var cleanTitle = title.Trim();
        var cleanBody = body ?? string.Empty;

        var updated = existing with
        {
            Title = cleanTitle,
            Body = cleanBody,
            Version = existing.Version + 1,
            BaseVersion = existing.NeedsSync ? existing.BaseVersion : existing.Version,
            ContentHash = SyncContentHashHelper.ComputeNoteHash(cleanTitle, cleanBody, isDeleted: false),
            LastModifiedByDeviceId = identity.DeviceId,
            LastModifiedByDeviceName = identity.CustomName,
            ModifiedAtUtc = now,
            IsDeleted = false,
            DeletedAtUtc = null,
            HasConflict = false,
            NeedsSync = true
        };

        var saved = await _notesRepo.UpsertNoteAsync(updated, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await _notesRepo.AppendRevisionAsync(NoteConflictMergeEngine.CreateRevision(updated, now, "local_edit"), cancellationToken).ConfigureAwait(false);
        await EnqueueAndOptionallyAutoSyncAsync(updated, SyncOperationKind.UpsertNote, cancellationToken).ConfigureAwait(false);

        NotesOrConflictsChanged?.Invoke(this, EventArgs.Empty);
        var refreshed = await _notesRepo.GetByIdAsync(noteId, cancellationToken).ConfigureAwait(false);
        return Result<NexusNoteContract>.Success(refreshed.IsSuccess && refreshed.Value is not null ? refreshed.Value : saved.Value);
    }

    public async Task<Result<NexusNoteContract>> DeleteNoteAsync(Guid noteId, CancellationToken cancellationToken = default)
    {
        var existingResult = await _notesRepo.GetByIdAsync(noteId, cancellationToken).ConfigureAwait(false);
        if (existingResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(existingResult.Error!);
        }

        if (existingResult.Value is null)
        {
            return Result<NexusNoteContract>.Failure(
                NexusError.NotFound($"Nota {noteId} não encontrada.", code: "NOTE_NOT_FOUND"));
        }

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(identityResult.Error!);
        }

        var existing = existingResult.Value;
        var identity = identityResult.Value;
        var now = _clock.UtcNow;

        var tombstone = existing with
        {
            Version = existing.Version + 1,
            BaseVersion = existing.NeedsSync ? existing.BaseVersion : existing.Version,
            ContentHash = SyncContentHashHelper.ComputeNoteHash(existing.Title, existing.Body, isDeleted: true),
            LastModifiedByDeviceId = identity.DeviceId,
            LastModifiedByDeviceName = identity.CustomName,
            ModifiedAtUtc = now,
            IsDeleted = true,
            DeletedAtUtc = now,
            HasConflict = false,
            NeedsSync = true
        };

        var saved = await _notesRepo.UpsertNoteAsync(tombstone, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await _notesRepo.AppendRevisionAsync(NoteConflictMergeEngine.CreateRevision(tombstone, now, "local_delete"), cancellationToken).ConfigureAwait(false);
        await EnqueueAndOptionallyAutoSyncAsync(tombstone, SyncOperationKind.DeleteNote, cancellationToken).ConfigureAwait(false);

        NotesOrConflictsChanged?.Invoke(this, EventArgs.Empty);
        var refreshed = await _notesRepo.GetByIdAsync(noteId, cancellationToken).ConfigureAwait(false);
        return Result<NexusNoteContract>.Success(refreshed.IsSuccess && refreshed.Value is not null ? refreshed.Value : saved.Value);
    }

    public Task<Result<IReadOnlyList<NexusNoteContract>>> ListNotesAsync(
        string? searchQuery = null,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(searchQuery)
            ? _notesRepo.ListAsync(includeDeleted, cancellationToken)
            : _notesRepo.SearchAsync(searchQuery, includeDeleted, cancellationToken);

    public Task<Result<IReadOnlyList<NoteRevisionSnapshot>>> GetRevisionsAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
        => _notesRepo.GetRevisionsAsync(noteId, cancellationToken);

    public async Task<Result<NexusNoteContract>> RestoreRevisionAsync(
        Guid noteId,
        Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        var revisionsResult = await _notesRepo.GetRevisionsAsync(noteId, cancellationToken).ConfigureAwait(false);
        if (revisionsResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(revisionsResult.Error!);
        }

        var revision = revisionsResult.Value.FirstOrDefault(r => r.RevisionId == revisionId);
        if (revision is null)
        {
            return Result<NexusNoteContract>.Failure(
                NexusError.NotFound($"Revisão {revisionId} não encontrada para a nota {noteId}.", code: "REVISION_NOT_FOUND"));
        }

        var existingResult = await _notesRepo.GetByIdAsync(noteId, cancellationToken).ConfigureAwait(false);
        var existing = existingResult.IsSuccess ? existingResult.Value : null;

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(identityResult.Error!);
        }

        var identity = identityResult.Value;
        var now = _clock.UtcNow;
        var restoredTitle = string.IsNullOrWhiteSpace(revision.Title) ? "Nota Restaurada" : revision.Title;
        var nextVersion = (existing?.Version ?? revision.Version) + 1;
        var baseVersion = existing is null
            ? 0
            : (existing.NeedsSync ? existing.BaseVersion : existing.Version);

        var restored = new NexusNoteContract
        {
            NoteId = noteId,
            Title = restoredTitle,
            Body = revision.Body,
            Version = nextVersion,
            BaseVersion = baseVersion,
            ContentHash = SyncContentHashHelper.ComputeNoteHash(restoredTitle, revision.Body, isDeleted: false),
            LastModifiedByDeviceId = identity.DeviceId,
            LastModifiedByDeviceName = identity.CustomName,
            CreatedAtUtc = existing?.CreatedAtUtc ?? revision.SavedAtUtc,
            ModifiedAtUtc = now,
            IsDeleted = false,
            DeletedAtUtc = null,
            HasConflict = false,
            NeedsSync = true
        };

        var saved = await _notesRepo.UpsertNoteAsync(restored, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await _notesRepo.AppendRevisionAsync(
            NoteConflictMergeEngine.CreateRevision(restored, now, $"restored_from_v{revision.Version}"),
            cancellationToken).ConfigureAwait(false);
        await EnqueueAndOptionallyAutoSyncAsync(restored, SyncOperationKind.UpsertNote, cancellationToken).ConfigureAwait(false);

        NotesOrConflictsChanged?.Invoke(this, EventArgs.Empty);
        var refreshed = await _notesRepo.GetByIdAsync(noteId, cancellationToken).ConfigureAwait(false);
        return Result<NexusNoteContract>.Success(refreshed.IsSuccess && refreshed.Value is not null ? refreshed.Value : saved.Value);
    }

    public Task<Result<IReadOnlyList<SyncConflictRecord>>> ListConflictsAsync(
        bool unresolvedOnly = true,
        CancellationToken cancellationToken = default)
        => _notesRepo.ListConflictsAsync(unresolvedOnly, cancellationToken);

    public async Task<Result<NexusNoteContract>> ResolveConflictAsync(
        ResolveConflictRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conflictResult = await _notesRepo.GetConflictByIdAsync(request.ConflictId, cancellationToken).ConfigureAwait(false);
        if (conflictResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(conflictResult.Error!);
        }

        var conflict = conflictResult.Value;
        if (conflict is null)
        {
            return Result<NexusNoteContract>.Failure(
                NexusError.NotFound($"Conflito {request.ConflictId} não encontrado.", code: "CONFLICT_NOT_FOUND"));
        }

        var localExistingResult = await _notesRepo.GetByIdAsync(conflict.NoteId, cancellationToken).ConfigureAwait(false);
        var localExisting = localExistingResult.IsSuccess ? localExistingResult.Value : null;

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<NexusNoteContract>.Failure(identityResult.Error!);
        }

        var identity = identityResult.Value;
        var now = _clock.UtcNow;

        var (title, body, isDeleted) = request.Choice switch
        {
            ConflictResolutionChoice.KeepLocal => (conflict.LocalTitle, conflict.LocalBody, conflict.LocalIsDeleted),
            ConflictResolutionChoice.AcceptRemote => (conflict.RemoteTitle, conflict.RemoteBody, conflict.RemoteIsDeleted),
            ConflictResolutionChoice.ManualMerge => (
                string.IsNullOrWhiteSpace(request.MergedTitle) ? conflict.LocalTitle : request.MergedTitle.Trim(),
                request.MergedBody ?? conflict.LocalBody,
                false),
            _ => (conflict.LocalTitle, conflict.LocalBody, conflict.LocalIsDeleted)
        };

        var nextVersion = Math.Max(
            Math.Max(conflict.LocalVersion, conflict.RemoteVersion),
            localExisting?.Version ?? 0) + 1;

        // Anchor BaseVersion to the highest known server/remote version so subsequent sync pushes forward cleanly.
        var anchoredBase = Math.Max(conflict.RemoteVersion, localExisting?.BaseVersion ?? 0);

        var resolvedNote = new NexusNoteContract
        {
            NoteId = conflict.NoteId,
            Title = title,
            Body = body,
            Version = nextVersion,
            BaseVersion = anchoredBase,
            ContentHash = SyncContentHashHelper.ComputeNoteHash(title, body, isDeleted),
            LastModifiedByDeviceId = identity.DeviceId,
            LastModifiedByDeviceName = identity.CustomName,
            CreatedAtUtc = localExisting?.CreatedAtUtc ?? conflict.LocalModifiedAtUtc,
            ModifiedAtUtc = now,
            IsDeleted = isDeleted,
            DeletedAtUtc = isDeleted ? now : null,
            HasConflict = false,
            NeedsSync = true
        };

        var saved = await _notesRepo.UpsertNoteAsync(resolvedNote, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await _notesRepo.AppendRevisionAsync(
            NoteConflictMergeEngine.CreateRevision(resolvedNote, now, $"conflict_resolved_{request.Choice}"),
            cancellationToken).ConfigureAwait(false);

        await _notesRepo.MarkConflictResolvedAsync(
            conflict.ConflictId,
            $"Resolvido ({request.Choice}) por {identity.CustomName}",
            now,
            cancellationToken).ConfigureAwait(false);

        await EnqueueAndOptionallyAutoSyncAsync(
            resolvedNote,
            resolvedNote.IsDeleted ? SyncOperationKind.DeleteNote : SyncOperationKind.UpsertNote,
            cancellationToken).ConfigureAwait(false);

        NotesOrConflictsChanged?.Invoke(this, EventArgs.Empty);
        var refreshed = await _notesRepo.GetByIdAsync(conflict.NoteId, cancellationToken).ConfigureAwait(false);
        return Result<NexusNoteContract>.Success(refreshed.IsSuccess && refreshed.Value is not null ? refreshed.Value : saved.Value);
    }

    internal async Task<Result<NoteSyncResponse>> SyncWithServerAsync(CancellationToken cancellationToken)
    {
        var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (prefsResult.IsFailure)
        {
            return Result<NoteSyncResponse>.Failure(prefsResult.Error!);
        }

        var prefs = prefsResult.Value;
        if (!prefs.NotesSyncEnabled)
        {
            return Result<NoteSyncResponse>.Success(new NoteSyncResponse { ServerTimestampUtc = _clock.UtcNow });
        }

        var identityResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identityResult.IsFailure)
        {
            return Result<NoteSyncResponse>.Failure(identityResult.Error!);
        }

        var identity = identityResult.Value;
        var allNotesResult = await _notesRepo.ListAsync(includeDeleted: true, cancellationToken).ConfigureAwait(false);
        if (allNotesResult.IsFailure)
        {
            return Result<NoteSyncResponse>.Failure(allNotesResult.Error!);
        }

        var pendingQueueResult = await _queueRepo.ListPendingAsync("notes", cancellationToken).ConfigureAwait(false);
        var pendingQueue = pendingQueueResult.IsSuccess ? pendingQueueResult.Value : Array.Empty<SyncQueueItemRecord>();

        var unsyncedById = new Dictionary<Guid, NexusNoteContract>();
        foreach (var note in allNotesResult.Value.Where(n => n.NeedsSync))
        {
            unsyncedById[note.NoteId] = note;
        }

        foreach (var q in pendingQueue)
        {
            try
            {
                var queuedNote = JsonSerializer.Deserialize<NexusNoteContract>(q.PayloadJson, SyncJsonOptions.Default);
                if (queuedNote is not null && !unsyncedById.ContainsKey(queuedNote.NoteId))
                {
                    unsyncedById[queuedNote.NoteId] = queuedNote;
                }
            }
            catch
            {
                // Ignore malformed historical queue payload.
            }
        }

        var request = new NoteSyncRequest
        {
            SourceDeviceId = identity.DeviceId,
            SourceDeviceName = identity.CustomName,
            LocalChanges = unsyncedById.Values.ToArray(),
            Policy = prefs.DefaultNoteConflictPolicy
        };

        var responseResult = await _transport.SyncNotesAsync(request, cancellationToken).ConfigureAwait(false);
        if (responseResult.IsFailure)
        {
            var status = responseResult.Error?.Category == ErrorCategory.Unauthorized
                ? SyncQueueStatus.DeferredPermissionDenied
                : SyncQueueStatus.DeferredOffline;

            foreach (var q in pendingQueue)
            {
                await _queueRepo.UpdateAttemptAsync(q.QueueId, status, responseResult.Error?.Message, cancellationToken).ConfigureAwait(false);
            }

            return responseResult;
        }

        var response = responseResult.Value;
        var now = _clock.UtcNow;

        // Server received and evaluated all queued note mutations.
        foreach (var q in pendingQueue)
        {
            await _queueRepo.MarkCompletedAsync(q.QueueId, cancellationToken).ConfigureAwait(false);
        }

        var serverConflictedNoteIds = new HashSet<Guid>();
        foreach (var serverConflict in response.DetectedConflicts)
        {
            serverConflictedNoteIds.Add(serverConflict.NoteId);
            var normalized = NormalizeConflictForLocalDevice(serverConflict, identity.CustomName, unsyncedById);

            // Preserve both versions in local revision history BEFORE saving the conflict record so readers never observe a conflict without its archived branches.
            await _notesRepo.AppendRevisionAsync(new NoteRevisionSnapshot
            {
                NoteId = normalized.NoteId,
                Version = normalized.LocalVersion,
                Title = normalized.LocalTitle,
                Body = normalized.LocalBody,
                ContentHash = SyncContentHashHelper.ComputeNoteHash(normalized.LocalTitle, normalized.LocalBody, normalized.LocalIsDeleted),
                ModifiedByDeviceId = identity.DeviceId,
                ModifiedByDeviceName = normalized.LocalDeviceName,
                SavedAtUtc = normalized.LocalModifiedAtUtc,
                IsDeleted = normalized.LocalIsDeleted,
                Reason = "conflict_local_preserved"
            }, cancellationToken).ConfigureAwait(false);

            await _notesRepo.AppendRevisionAsync(new NoteRevisionSnapshot
            {
                NoteId = normalized.NoteId,
                Version = normalized.RemoteVersion,
                Title = normalized.RemoteTitle,
                Body = normalized.RemoteBody,
                ContentHash = SyncContentHashHelper.ComputeNoteHash(normalized.RemoteTitle, normalized.RemoteBody, normalized.RemoteIsDeleted),
                ModifiedByDeviceId = Guid.Empty,
                ModifiedByDeviceName = normalized.RemoteDeviceName,
                SavedAtUtc = normalized.RemoteModifiedAtUtc,
                IsDeleted = normalized.RemoteIsDeleted,
                Reason = "conflict_remote_preserved"
            }, cancellationToken).ConfigureAwait(false);

            var localCurrent = await _notesRepo.GetByIdAsync(normalized.NoteId, cancellationToken).ConfigureAwait(false);
            if (localCurrent.IsSuccess && localCurrent.Value is { } existing)
            {
                await _notesRepo.UpsertNoteAsync(existing with
                {
                    BaseVersion = normalized.RemoteVersion,
                    HasConflict = !normalized.IsResolved,
                    NeedsSync = false
                }, cancellationToken).ConfigureAwait(false);
            }

            await _notesRepo.SaveConflictAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        foreach (var canonical in response.CanonicalNotes)
        {
            if (serverConflictedNoteIds.Contains(canonical.NoteId))
            {
                continue;
            }

            var localResult = await _notesRepo.GetByIdAsync(canonical.NoteId, cancellationToken).ConfigureAwait(false);
            var localNote = localResult.IsSuccess ? localResult.Value : null;

            var evaluation = NoteConflictMergeEngine.EvaluateClientPull(localNote, canonical, prefs.DefaultNoteConflictPolicy, now);
            await _notesRepo.UpsertNoteAsync(evaluation.ResultingNote, cancellationToken).ConfigureAwait(false);

            foreach (var rev in evaluation.RevisionsToArchive)
            {
                await _notesRepo.AppendRevisionAsync(rev, cancellationToken).ConfigureAwait(false);
            }

            if (evaluation.ConflictRecord is not null)
            {
                await _notesRepo.SaveConflictAsync(evaluation.ConflictRecord, cancellationToken).ConfigureAwait(false);
            }
        }

        NotesOrConflictsChanged?.Invoke(this, EventArgs.Empty);
        return Result<NoteSyncResponse>.Success(response);
    }

    internal async Task<Result> ApplyIncomingRemoteNoteAsync(NexusNoteContract remoteNote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteNote);

        var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (prefsResult.IsFailure || !prefsResult.Value.NotesSyncEnabled)
        {
            return Result.Success();
        }

        var localResult = await _notesRepo.GetByIdAsync(remoteNote.NoteId, cancellationToken).ConfigureAwait(false);
        var localNote = localResult.IsSuccess ? localResult.Value : null;

        var eval = NoteConflictMergeEngine.EvaluateClientPull(
            localNote,
            remoteNote,
            prefsResult.Value.DefaultNoteConflictPolicy,
            _clock.UtcNow);

        var saved = await _notesRepo.UpsertNoteAsync(eval.ResultingNote, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return Result.Failure(saved.Error!);
        }

        foreach (var rev in eval.RevisionsToArchive)
        {
            await _notesRepo.AppendRevisionAsync(rev, cancellationToken).ConfigureAwait(false);
        }

        if (eval.ConflictRecord is not null)
        {
            await _notesRepo.SaveConflictAsync(eval.ConflictRecord, cancellationToken).ConfigureAwait(false);
        }

        NotesOrConflictsChanged?.Invoke(this, EventArgs.Empty);
        return Result.Success();
    }

    private async Task EnqueueAndOptionallyAutoSyncAsync(
        NexusNoteContract note,
        SyncOperationKind kind,
        CancellationToken cancellationToken)
    {
        var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (prefsResult.IsFailure || !prefsResult.Value.NotesSyncEnabled)
        {
            return;
        }

        var queueItem = new SyncQueueItemRecord
        {
            QueueId = Guid.CreateVersion7(),
            Scope = "notes",
            OperationKind = kind,
            EntityId = note.NoteId,
            PayloadJson = JsonSerializer.Serialize(note, SyncJsonOptions.Default),
            EnqueuedAtUtc = _clock.UtcNow,
            Status = _transport.IsRealtimeAvailable ? SyncQueueStatus.Pending : SyncQueueStatus.DeferredOffline
        };

        await _queueRepo.EnqueueAsync(queueItem, cancellationToken).ConfigureAwait(false);

        if (prefsResult.Value.NotesAutoSync && _transport.IsRealtimeAvailable)
        {
            await SyncWithServerAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static SyncConflictRecord NormalizeConflictForLocalDevice(
        SyncConflictRecord serverConflict,
        string localDeviceName,
        IReadOnlyDictionary<Guid, NexusNoteContract> sentLocalChanges)
    {
        // EvaluateServerPush treats server canonical as "Local" and pushing client as "Remote".
        // Swap sides here so the client UI always displays THIS device on the Local side and server/peer on Remote.
        var matchesPushed = sentLocalChanges.TryGetValue(serverConflict.NoteId, out var sentNote) &&
                            sentNote.Version == serverConflict.RemoteVersion &&
                            string.Equals(sentNote.Title, serverConflict.RemoteTitle, StringComparison.Ordinal);

        if (!matchesPushed && !string.Equals(serverConflict.RemoteDeviceName, localDeviceName, StringComparison.Ordinal))
        {
            return serverConflict;
        }

        return serverConflict with
        {
            LocalVersion = serverConflict.RemoteVersion,
            LocalTitle = serverConflict.RemoteTitle,
            LocalBody = serverConflict.RemoteBody,
            LocalIsDeleted = serverConflict.RemoteIsDeleted,
            LocalModifiedAtUtc = serverConflict.RemoteModifiedAtUtc,
            LocalDeviceName = serverConflict.RemoteDeviceName,
            RemoteVersion = serverConflict.LocalVersion,
            RemoteTitle = serverConflict.LocalTitle,
            RemoteBody = serverConflict.LocalBody,
            RemoteIsDeleted = serverConflict.LocalIsDeleted,
            RemoteModifiedAtUtc = serverConflict.LocalModifiedAtUtc,
            RemoteDeviceName = serverConflict.LocalDeviceName
        };
    }
}

public sealed class BasicSyncOrchestrator : ISyncOrchestrator, IDisposable
{
    private readonly ClipboardSyncCoordinator _clipboardService;
    private readonly NotesSyncCoordinator _notesService;
    private readonly IClipboardRepository _clipboardRepo;
    private readonly INotesRepository _notesRepo;
    private readonly ISyncQueueRepository _queueRepo;
    private readonly ISyncPrivacySettingsService _privacySettings;
    private readonly INexusSyncTransport _transport;
    private readonly IRealtimeClientChannel _realtimeChannel;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly ILogger<BasicSyncOrchestrator> _logger;

    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private SyncOverviewSnapshot _currentOverview = new();
    private ConnectionState _lastConnectionState = ConnectionState.Disconnected;

    public BasicSyncOrchestrator(
        IClipboardSyncService clipboardService,
        INotesSyncService notesService,
        IClipboardRepository clipboardRepo,
        INotesRepository notesRepo,
        ISyncQueueRepository queueRepo,
        ISyncPrivacySettingsService privacySettings,
        INexusSyncTransport transport,
        IRealtimeClientChannel realtimeChannel,
        IEventBus eventBus,
        IClock clock,
        ILogger<BasicSyncOrchestrator> logger)
    {
        _clipboardService = (ClipboardSyncCoordinator)clipboardService;
        _notesService = (NotesSyncCoordinator)notesService;
        _clipboardRepo = clipboardRepo;
        _notesRepo = notesRepo;
        _queueRepo = queueRepo;
        _privacySettings = privacySettings;
        _transport = transport;
        _realtimeChannel = realtimeChannel;
        _eventBus = eventBus;
        _clock = clock;
        _logger = logger;

        _lastConnectionState = realtimeChannel.State;
        _realtimeChannel.StateChanged += OnRealtimeStateChanged;
        _transport.RemoteClipboardReceived += OnRemoteClipboardReceived;
        _transport.RemoteNoteChangeReceived += OnRemoteNoteChangeReceived;
        _privacySettings.PreferencesChanged += OnPrivacyChanged;
        _clipboardService.HistoryChanged += OnLocalSyncDataChanged;
        _notesService.NotesOrConflictsChanged += OnLocalNotesChanged;
    }

    public SyncOverviewSnapshot CurrentOverview => _currentOverview;

    public event EventHandler<SyncOverviewSnapshot>? OverviewChanged;

    public async Task<Result<SyncOverviewSnapshot>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var overview = await RefreshOverviewSnapshotAsync(SyncStatus.Idle, diagnostic: null, updateLastSync: false, cancellationToken).ConfigureAwait(false);
        if (_realtimeChannel.IsConnected)
        {
            return await ResumeAfterReconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result<SyncOverviewSnapshot>.Success(overview);
    }

    public Task<Result<SyncOverviewSnapshot>> SyncNowAsync(CancellationToken cancellationToken = default)
        => ExecuteSyncCycleAsync(reason: "manual_sync", cancellationToken);

    public Task<Result<SyncOverviewSnapshot>> ResumeAfterReconnectAsync(CancellationToken cancellationToken = default)
        => ExecuteSyncCycleAsync(reason: "reconnect_resume", cancellationToken);

    private async Task<Result<SyncOverviewSnapshot>> ExecuteSyncCycleAsync(string reason, CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshOverviewSnapshotAsync(SyncStatus.Syncing, $"Sincronizando ({reason})...", updateLastSync: false, cancellationToken).ConfigureAwait(false);

            var prefsResult = await _privacySettings.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
            var prefs = prefsResult.IsSuccess ? prefsResult.Value : new SyncPrivacyPreferences();

            var errors = new List<string>();
            var syncedAnything = false;

            if (prefs.ClipboardSyncEnabled)
            {
                var flushed = await _clipboardService.FlushPendingQueueAsync(cancellationToken).ConfigureAwait(false);
                if (flushed.IsFailure)
                {
                    errors.Add(flushed.Error!.Message);
                }
                else
                {
                    syncedAnything |= flushed.Value > 0;
                    var pulled = await _clipboardService.RefreshFromServerAsync(cancellationToken).ConfigureAwait(false);
                    if (pulled.IsFailure)
                    {
                        errors.Add(pulled.Error!.Message);
                    }
                    else
                    {
                        syncedAnything |= pulled.Value > 0;
                    }
                }
            }

            if (prefs.NotesSyncEnabled)
            {
                var notesSync = await _notesService.SyncWithServerAsync(cancellationToken).ConfigureAwait(false);
                if (notesSync.IsFailure)
                {
                    errors.Add(notesSync.Error!.Message);
                }
                else
                {
                    syncedAnything = true;
                    if (notesSync.Value.DetectedConflicts.Count > 0)
                    {
                        await PublishIntegrationEventSafeAsync(
                            NexusEventTypes.SyncConflictDetected,
                            new { Count = notesSync.Value.DetectedConflicts.Count },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var status = errors.Count > 0
                ? SyncStatus.Failed
                : (_currentOverview.UnresolvedConflictsCount > 0 ? SyncStatus.Conflict : SyncStatus.Completed);

            var diagnostic = errors.Count > 0
                ? string.Join(" | ", errors)
                : (syncedAnything ? "Sincronização concluída." : "Tudo atualizado.");

            var finalOverview = await RefreshOverviewSnapshotAsync(
                status,
                diagnostic,
                updateLastSync: errors.Count == 0,
                cancellationToken).ConfigureAwait(false);

            if (finalOverview.UnresolvedConflictsCount > 0 && status != SyncStatus.Failed)
            {
                finalOverview = await RefreshOverviewSnapshotAsync(
                    SyncStatus.Conflict,
                    $"{finalOverview.UnresolvedConflictsCount} conflito(s) de nota aguardando resolução.",
                    updateLastSync: errors.Count == 0,
                    cancellationToken).ConfigureAwait(false);
            }

            if (errors.Count == 0)
            {
                await PublishIntegrationEventSafeAsync(
                    NexusEventTypes.SyncQueueFlushed,
                    new { Reason = reason, Pending = finalOverview.PendingOperationsCount },
                    cancellationToken).ConfigureAwait(false);
                return Result<SyncOverviewSnapshot>.Success(finalOverview);
            }

            return Result<SyncOverviewSnapshot>.Failure(
                NexusError.Network(diagnostic, code: "SYNC_CYCLE_PARTIAL_FAILURE"));
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<SyncOverviewSnapshot> RefreshOverviewSnapshotAsync(
        SyncStatus status,
        string? diagnostic,
        bool updateLastSync,
        CancellationToken ct)
    {
        var prefs = await _privacySettings.GetPreferencesAsync(ct).ConfigureAwait(false);
        var pending = await _queueRepo.GetPendingCountAsync(ct).ConfigureAwait(false);
        var conflicts = await _notesRepo.ListConflictsAsync(unresolvedOnly: true, ct).ConfigureAwait(false);
        var history = await _clipboardRepo.ListHistoryAsync(500, ct).ConfigureAwait(false);
        var notes = await _notesRepo.ListAsync(includeDeleted: false, ct).ConfigureAwait(false);

        var snapshot = new SyncOverviewSnapshot
        {
            LastSyncAtUtc = updateLastSync ? _clock.UtcNow : _currentOverview.LastSyncAtUtc,
            Status = status,
            PendingOperationsCount = pending.IsSuccess ? pending.Value : 0,
            UnresolvedConflictsCount = conflicts.IsSuccess ? conflicts.Value.Count : 0,
            ClipboardHistoryCount = history.IsSuccess ? history.Value.Count : 0,
            NotesCount = notes.IsSuccess ? notes.Value.Count : 0,
            ClipboardEnabled = prefs.IsSuccess && prefs.Value.ClipboardSyncEnabled,
            NotesEnabled = prefs.IsSuccess && prefs.Value.NotesSyncEnabled,
            LastDiagnostic = diagnostic
        };

        _currentOverview = snapshot;
        OverviewChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private Task PublishIntegrationEventSafeAsync(string eventType, object payload, CancellationToken ct)
    {
        var evt = new DiagnosticDomainEvent(
            "sync.orchestrator",
            $"{eventType}: {JsonSerializer.Serialize(payload, SyncJsonOptions.Default)}",
            IsError: false)
        {
            OccurredAtUtc = _clock.UtcNow
        };
        return _eventBus.PublishAsync(evt, ct);
    }

    private void OnRealtimeStateChanged(object? sender, ConnectionStateSnapshot snapshot)
    {
        var prev = _lastConnectionState;
        _lastConnectionState = snapshot.State;

        if (snapshot.State == ConnectionState.Connected && prev != ConnectionState.Connected)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ResumeAfterReconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Reconnect sync flush encountered an error.");
                }
            });
        }
    }

    private void OnRemoteClipboardReceived(object? sender, ClipboardEntryContract entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _clipboardService.ApplyRemoteEntryAsync(entry, CancellationToken.None).ConfigureAwait(false);
                await RefreshOverviewSnapshotAsync(_currentOverview.Status, "Área de transferência recebida em tempo real.", updateLastSync: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao processar clipboard recebido em tempo real.");
            }
        });
    }

    private void OnRemoteNoteChangeReceived(object? sender, NexusNoteContract note)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _notesService.ApplyIncomingRemoteNoteAsync(note, CancellationToken.None).ConfigureAwait(false);
                await RefreshOverviewSnapshotAsync(_currentOverview.Status, $"Nota '{note.Title}' atualizada em tempo real.", updateLastSync: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao processar nota recebida em tempo real.");
            }
        });
    }

    private void OnPrivacyChanged(object? sender, SyncPrivacyPreferences prefs)
        => _ = RefreshOverviewSnapshotAsync(_currentOverview.Status, "Preferências de privacidade atualizadas.", updateLastSync: false, CancellationToken.None);

    private void OnLocalSyncDataChanged(object? sender, IReadOnlyList<ClipboardEntryContract> entries)
        => _ = RefreshOverviewSnapshotAsync(_currentOverview.Status, _currentOverview.LastDiagnostic, updateLastSync: false, CancellationToken.None);

    private void OnLocalNotesChanged(object? sender, EventArgs args)
        => _ = RefreshOverviewSnapshotAsync(_currentOverview.Status, _currentOverview.LastDiagnostic, updateLastSync: false, CancellationToken.None);

    public void Dispose()
    {
        _realtimeChannel.StateChanged -= OnRealtimeStateChanged;
        _transport.RemoteClipboardReceived -= OnRemoteClipboardReceived;
        _transport.RemoteNoteChangeReceived -= OnRemoteNoteChangeReceived;
        _privacySettings.PreferencesChanged -= OnPrivacyChanged;
        _clipboardService.HistoryChanged -= OnLocalSyncDataChanged;
        _notesService.NotesOrConflictsChanged -= OnLocalNotesChanged;
        _syncGate.Dispose();
    }
}
