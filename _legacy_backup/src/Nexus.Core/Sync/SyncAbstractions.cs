using Nexus.Contracts.Sync;
using Nexus.Core.Errors;

namespace Nexus.Core.Sync;

/// <summary>
/// Platform-specific OS clipboard bridge respecting system background restrictions.
/// Windows Desktop supports continuous polling/monitoring; Android restricts background access
/// so capture operates on explicit user action or foreground focus.
/// </summary>
public interface ISystemClipboardAdapter
{
    bool SupportsContinuousBackgroundMonitor { get; }
    string PlatformRestrictionNotice { get; }

    event EventHandler<string>? SystemClipboardTextCaptured;

    Task<Result<string?>> ReadTextAsync(CancellationToken cancellationToken = default);
    Task<Result> WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

public interface IClipboardRepository
{
    Task<Result> AddEntryAsync(ClipboardEntryContract entry, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<ClipboardEntryContract>>> ListHistoryAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<Result<ClipboardEntryContract?>> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<Result<bool>> ContainsRecentHashAsync(string contentHash, TimeSpan window, DateTimeOffset utcNow, CancellationToken cancellationToken = default);
    Task<Result<int>> TrimToLimitAsync(int maxEntries, CancellationToken cancellationToken = default);
    Task<Result> ClearHistoryAsync(CancellationToken cancellationToken = default);
}

public interface INotesRepository
{
    Task<Result<NexusNoteContract?>> GetByIdAsync(Guid noteId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<NexusNoteContract>>> ListAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<NexusNoteContract>>> SearchAsync(string query, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Result<NexusNoteContract>> UpsertNoteAsync(NexusNoteContract note, CancellationToken cancellationToken = default);
    Task<Result> AppendRevisionAsync(NoteRevisionSnapshot revision, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<NoteRevisionSnapshot>>> GetRevisionsAsync(Guid noteId, CancellationToken cancellationToken = default);
    Task<Result> SaveConflictAsync(SyncConflictRecord conflict, CancellationToken cancellationToken = default);
    Task<Result<SyncConflictRecord?>> GetConflictByIdAsync(Guid conflictId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SyncConflictRecord>>> ListConflictsAsync(bool unresolvedOnly = true, CancellationToken cancellationToken = default);
    Task<Result> MarkConflictResolvedAsync(Guid conflictId, string resolutionSummary, DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken = default);
}

public interface ISyncQueueRepository
{
    Task<Result> EnqueueAsync(SyncQueueItemRecord item, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SyncQueueItemRecord>>> ListPendingAsync(string? scope = null, CancellationToken cancellationToken = default);
    Task<Result<int>> GetPendingCountAsync(CancellationToken cancellationToken = default);
    Task<Result> MarkCompletedAsync(Guid queueId, CancellationToken cancellationToken = default);
    Task<Result> UpdateAttemptAsync(Guid queueId, SyncQueueStatus status, string? lastError, CancellationToken cancellationToken = default);
    Task<Result> ClearCompletedAsync(CancellationToken cancellationToken = default);
}

public interface ISyncPrivacySettingsService
{
    event EventHandler<SyncPrivacyPreferences>? PreferencesChanged;
    Task<Result<SyncPrivacyPreferences>> GetPreferencesAsync(CancellationToken cancellationToken = default);
    Task<Result<SyncPrivacyPreferences>> SavePreferencesAsync(SyncPrivacyPreferences preferences, CancellationToken cancellationToken = default);
}

/// <summary>
/// Transport abstraction communicating clipboard pushes, note batch sync, and remote notifications
/// over SignalR/HTTP to authorized endpoints only.
/// </summary>
public interface INexusSyncTransport
{
    bool IsRealtimeAvailable { get; }

    event EventHandler<ClipboardEntryContract>? RemoteClipboardReceived;
    event EventHandler<NexusNoteContract>? RemoteNoteChangeReceived;

    Task<Result> PushClipboardEntryAsync(ClipboardEntryContract entry, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<ClipboardEntryContract>>> PullClipboardHistoryAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<Result<NoteSyncResponse>> SyncNotesAsync(NoteSyncRequest request, CancellationToken cancellationToken = default);
}

public interface IClipboardSyncService
{
    bool IsCycleGuardActive { get; }
    string PlatformCapabilitySummary { get; }

    event EventHandler<IReadOnlyList<ClipboardEntryContract>>? HistoryChanged;
    event EventHandler<ClipboardEntryContract>? RemoteClipboardApplied;

    Task<Result<ClipboardEntryContract>> CaptureAndSendTextAsync(
        string text,
        bool manualTrigger = true,
        CancellationToken cancellationToken = default);

    Task<Result<ClipboardEntryContract?>> CaptureCurrentOsClipboardAsync(CancellationToken cancellationToken = default);
    Task<Result<bool>> ApplyRemoteEntryAsync(ClipboardEntryContract entry, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<ClipboardEntryContract>>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task<Result> ClearHistoryAsync(CancellationToken cancellationToken = default);
    Task<Result<int>> RefreshFromServerAsync(CancellationToken cancellationToken = default);
}

public interface INotesSyncService
{
    event EventHandler? NotesOrConflictsChanged;

    Task<Result<NexusNoteContract>> CreateNoteAsync(string title, string body, CancellationToken cancellationToken = default);
    Task<Result<NexusNoteContract>> UpdateNoteAsync(Guid noteId, string title, string body, CancellationToken cancellationToken = default);
    Task<Result<NexusNoteContract>> DeleteNoteAsync(Guid noteId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<NexusNoteContract>>> ListNotesAsync(string? searchQuery = null, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<NoteRevisionSnapshot>>> GetRevisionsAsync(Guid noteId, CancellationToken cancellationToken = default);
    Task<Result<NexusNoteContract>> RestoreRevisionAsync(Guid noteId, Guid revisionId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SyncConflictRecord>>> ListConflictsAsync(bool unresolvedOnly = true, CancellationToken cancellationToken = default);
    Task<Result<NexusNoteContract>> ResolveConflictAsync(ResolveConflictRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Unified Fase 06 orchestrator managing offline queues, reconnect flush, manual sync,
/// privacy toggles, and real-time sync status indicator.
/// </summary>
public interface ISyncOrchestrator
{
    SyncOverviewSnapshot CurrentOverview { get; }
    event EventHandler<SyncOverviewSnapshot>? OverviewChanged;

    Task<Result<SyncOverviewSnapshot>> InitializeAsync(CancellationToken cancellationToken = default);
    Task<Result<SyncOverviewSnapshot>> SyncNowAsync(CancellationToken cancellationToken = default);
    Task<Result<SyncOverviewSnapshot>> ResumeAfterReconnectAsync(CancellationToken cancellationToken = default);
}

