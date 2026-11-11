using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Sync;

public enum SyncOperationKind
{
    PushClipboard = 0,
    UpsertNote = 1,
    DeleteNote = 2
}

public enum SyncQueueStatus
{
    Pending = 0,
    Completed = 1,
    DeferredOffline = 2,
    DeferredPermissionDenied = 3
}

public enum ConflictResolutionChoice
{
    KeepLocal = 0,
    AcceptRemote = 1,
    ManualMerge = 2
}

public static class SyncContentHashHelper
{
    public static string ComputeUtf8Sha256(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string ComputeNoteHash(string title, string body, bool isDeleted)
        => ComputeUtf8Sha256($"{title}\n---\n{body}\n---\n{(isDeleted ? "deleted" : "active")}");
}

/// <summary>
/// Per-feature privacy and synchronization settings stored locally per installation.
/// </summary>
public sealed record SyncPrivacyPreferences : IVersionedContract, IValidatableContract
{
    public bool ClipboardSyncEnabled { get; init; } = true;
    public bool ClipboardAutoSync { get; init; } = true;

    [Range(1, 500)]
    public int ClipboardMaxHistoryItems { get; init; } = 50;

    public bool NotesSyncEnabled { get; init; } = true;
    public bool NotesAutoSync { get; init; } = true;
    public ConflictResolutionPolicy DefaultNoteConflictPolicy { get; init; } = ConflictResolutionPolicy.Manual;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (ClipboardMaxHistoryItems is < 1 or > 500)
        {
            yield return new ContractValidationError(nameof(ClipboardMaxHistoryItems), "HISTORY_LIMIT_OUT_OF_RANGE", "Clipboard history limit must be between 1 and 500.");
        }
    }
}

public sealed record ClipboardEntryContract : IVersionedContract, IValidatableContract
{
    public Guid EntryId { get; init; } = Guid.CreateVersion7();
    public Guid OriginDeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string OriginDeviceName { get; init; } = "NEXUS Device";

    [Required]
    [StringLength(131072, MinimumLength = 1)]
    public string ContentText { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 16)]
    public string ContentHash { get; init; } = string.Empty;

    public Guid SyncOriginToken { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsRemoteReceived { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (EntryId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(EntryId), "ENTRY_ID_EMPTY", "EntryId is required.");
        }

        if (OriginDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(OriginDeviceId), "ORIGIN_DEVICE_EMPTY", "OriginDeviceId is required.");
        }

        if (string.IsNullOrEmpty(ContentText))
        {
            yield return new ContractValidationError(nameof(ContentText), "CLIPBOARD_TEXT_EMPTY", "ContentText cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(ContentHash))
        {
            yield return new ContractValidationError(nameof(ContentHash), "CLIPBOARD_HASH_EMPTY", "ContentHash is required.");
        }
    }
}

public sealed record NexusNoteContract : IVersionedContract, IValidatableContract
{
    public Guid NoteId { get; init; } = Guid.CreateVersion7();

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Title { get; init; } = string.Empty;

    [StringLength(262144)]
    public string Body { get; init; } = string.Empty;

    [Range(1, long.MaxValue)]
    public long Version { get; init; } = 1;

    [Range(0, long.MaxValue)]
    public long BaseVersion { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 16)]
    public string ContentHash { get; init; } = string.Empty;

    public Guid LastModifiedByDeviceId { get; init; }

    [StringLength(120)]
    public string LastModifiedByDeviceName { get; init; } = "NEXUS Device";

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAtUtc { get; init; }
    public bool HasConflict { get; init; }
    public bool NeedsSync { get; init; } = true;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (NoteId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(NoteId), "NOTE_ID_EMPTY", "NoteId cannot be empty.");
        }

        if (!IsDeleted && string.IsNullOrWhiteSpace(Title))
        {
            yield return new ContractValidationError(nameof(Title), "NOTE_TITLE_EMPTY", "Note title is required.");
        }

        if (Version < 1)
        {
            yield return new ContractValidationError(nameof(Version), "NOTE_VERSION_INVALID", "Version must be >= 1.");
        }

        if (LastModifiedByDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(LastModifiedByDeviceId), "MODIFIED_BY_EMPTY", "LastModifiedByDeviceId is required.");
        }
    }
}

public sealed record NoteRevisionSnapshot : IVersionedContract
{
    public Guid RevisionId { get; init; } = Guid.CreateVersion7();
    public Guid NoteId { get; init; }
    public long Version { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public Guid ModifiedByDeviceId { get; init; }
    public string ModifiedByDeviceName { get; init; } = string.Empty;
    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; init; }
    public string Reason { get; init; } = "local_edit";
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record SyncConflictRecord : IVersionedContract
{
    public Guid ConflictId { get; init; } = Guid.CreateVersion7();
    public string EntityType { get; init; } = "nexus.note";
    public Guid NoteId { get; init; }
    public long CommonBaseVersion { get; init; }
    public long LocalVersion { get; init; }
    public string LocalTitle { get; init; } = string.Empty;
    public string LocalBody { get; init; } = string.Empty;
    public bool LocalIsDeleted { get; init; }
    public DateTimeOffset LocalModifiedAtUtc { get; init; }
    public string LocalDeviceName { get; init; } = string.Empty;
    public long RemoteVersion { get; init; }
    public string RemoteTitle { get; init; } = string.Empty;
    public string RemoteBody { get; init; } = string.Empty;
    public bool RemoteIsDeleted { get; init; }
    public DateTimeOffset RemoteModifiedAtUtc { get; init; }
    public string RemoteDeviceName { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
    public string? ResolutionSummary { get; init; }
    public DateTimeOffset DetectedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record SyncQueueItemRecord : IVersionedContract
{
    public Guid QueueId { get; init; } = Guid.CreateVersion7();
    public string Scope { get; init; } = "notes";
    public SyncOperationKind OperationKind { get; init; }
    public Guid EntityId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public DateTimeOffset EnqueuedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; init; }
    public SyncQueueStatus Status { get; init; } = SyncQueueStatus.Pending;
    public string? LastError { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record NoteSyncRequest : IVersionedContract
{
    public Guid SourceDeviceId { get; init; }
    public string SourceDeviceName { get; init; } = string.Empty;
    public DateTimeOffset? SinceUtc { get; init; }
    public IReadOnlyList<NexusNoteContract> LocalChanges { get; init; } = Array.Empty<NexusNoteContract>();
    public ConflictResolutionPolicy Policy { get; init; } = ConflictResolutionPolicy.Manual;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record NoteSyncResponse : IVersionedContract
{
    public DateTimeOffset ServerTimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<NexusNoteContract> CanonicalNotes { get; init; } = Array.Empty<NexusNoteContract>();
    public IReadOnlyList<SyncConflictRecord> DetectedConflicts { get; init; } = Array.Empty<SyncConflictRecord>();
    public IReadOnlyList<Guid> AcceptedNoteIds { get; init; } = Array.Empty<Guid>();
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record ResolveConflictRequest : IVersionedContract
{
    public Guid ConflictId { get; init; }
    public ConflictResolutionChoice Choice { get; init; }
    public string? MergedTitle { get; init; }
    public string? MergedBody { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record SyncOverviewSnapshot : IVersionedContract
{
    public DateTimeOffset? LastSyncAtUtc { get; init; }
    public SyncStatus Status { get; init; } = SyncStatus.Idle;
    public int PendingOperationsCount { get; init; }
    public int UnresolvedConflictsCount { get; init; }
    public int ClipboardHistoryCount { get; init; }
    public int NotesCount { get; init; }
    public bool ClipboardEnabled { get; init; }
    public bool NotesEnabled { get; init; }
    public string? LastDiagnostic { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

