using Nexus.Contracts.Sync;

namespace Nexus.Core.Sync;

public enum NoteMergeOutcomeKind
{
    NoChange = 0,
    AcceptedForward = 1,
    ConflictDetected = 2
}

public sealed record NoteMergeEvaluation(
    NoteMergeOutcomeKind Outcome,
    NexusNoteContract ResultingNote,
    SyncConflictRecord? ConflictRecord,
    IReadOnlyList<NoteRevisionSnapshot> RevisionsToArchive);

/// <summary>
/// Shared, deterministic conflict detection and revision-preservation engine for <see cref="NexusNoteContract"/>
/// synchronization between Windows, Android, and NEXUS Server. Never silently discards concurrent changes.
/// </summary>
public static class NoteConflictMergeEngine
{
    public static NoteMergeEvaluation EvaluateServerPush(
        NexusNoteContract? existingCanonical,
        NexusNoteContract incoming,
        ConflictResolutionPolicy policy,
        DateTimeOffset utcNow)
    {
        if (existingCanonical is null)
        {
            var initial = incoming with
            {
                Version = Math.Max(1, incoming.Version),
                BaseVersion = Math.Max(1, incoming.Version),
                NeedsSync = false,
                HasConflict = false
            };

            return new NoteMergeEvaluation(
                NoteMergeOutcomeKind.AcceptedForward,
                initial,
                null,
                [CreateRevision(initial, utcNow, initial.IsDeleted ? "deleted_initial" : "remote_created")]);
        }

        if (string.Equals(existingCanonical.ContentHash, incoming.ContentHash, StringComparison.Ordinal) &&
            existingCanonical.IsDeleted == incoming.IsDeleted)
        {
            return new NoteMergeEvaluation(
                NoteMergeOutcomeKind.NoChange,
                existingCanonical,
                null,
                Array.Empty<NoteRevisionSnapshot>());
        }

        // Sequential non-conflicting forward advance: client based its edit on current server version
        if (incoming.BaseVersion >= existingCanonical.Version)
        {
            var nextVersion = Math.Max(existingCanonical.Version + 1, incoming.Version);
            var accepted = incoming with
            {
                Version = nextVersion,
                BaseVersion = nextVersion,
                NeedsSync = false,
                HasConflict = false
            };

            return new NoteMergeEvaluation(
                NoteMergeOutcomeKind.AcceptedForward,
                accepted,
                null,
                [CreateRevision(accepted, utcNow, accepted.IsDeleted ? "deleted" : "updated")]);
        }

        // Concurrent edit detected: BaseVersion < existingCanonical.Version and content differs
        return HandleConcurrentConflict(
            localBaseline: existingCanonical,
            remoteIncoming: incoming,
            localDeviceNameForConflict: existingCanonical.LastModifiedByDeviceName,
            remoteDeviceNameForConflict: incoming.LastModifiedByDeviceName,
            isServerCanonicalPerspective: true,
            policy: policy,
            utcNow: utcNow);
    }

    public static NoteMergeEvaluation EvaluateClientPull(
        NexusNoteContract? localNote,
        NexusNoteContract remoteCanonical,
        ConflictResolutionPolicy policy,
        DateTimeOffset utcNow)
    {
        if (localNote is null)
        {
            var accepted = remoteCanonical with
            {
                BaseVersion = remoteCanonical.Version,
                NeedsSync = false,
                HasConflict = false
            };

            return new NoteMergeEvaluation(
                NoteMergeOutcomeKind.AcceptedForward,
                accepted,
                null,
                [CreateRevision(accepted, utcNow, accepted.IsDeleted ? "remote_deleted" : "remote_synced")]);
        }

        if (string.Equals(localNote.ContentHash, remoteCanonical.ContentHash, StringComparison.Ordinal) &&
            localNote.IsDeleted == remoteCanonical.IsDeleted)
        {
            var aligned = localNote with
            {
                Version = Math.Max(localNote.Version, remoteCanonical.Version),
                BaseVersion = Math.Max(localNote.Version, remoteCanonical.Version),
                NeedsSync = false,
                HasConflict = false
            };

            return new NoteMergeEvaluation(
                NoteMergeOutcomeKind.NoChange,
                aligned,
                null,
                Array.Empty<NoteRevisionSnapshot>());
        }

        // If local note has NO unsynced edits, safely fast-forward to remote canonical
        if (!localNote.NeedsSync)
        {
            var fastForwarded = remoteCanonical with
            {
                BaseVersion = remoteCanonical.Version,
                NeedsSync = false,
                HasConflict = false
            };

            return new NoteMergeEvaluation(
                NoteMergeOutcomeKind.AcceptedForward,
                fastForwarded,
                null,
                [CreateRevision(fastForwarded, utcNow, fastForwarded.IsDeleted ? "remote_deleted" : "remote_synced")]);
        }

        // Concurrent offline local modification vs different remote canonical
        return HandleConcurrentConflict(
            localBaseline: localNote,
            remoteIncoming: remoteCanonical,
            localDeviceNameForConflict: localNote.LastModifiedByDeviceName,
            remoteDeviceNameForConflict: remoteCanonical.LastModifiedByDeviceName,
            isServerCanonicalPerspective: false,
            policy: policy,
            utcNow: utcNow);
    }

    private static NoteMergeEvaluation HandleConcurrentConflict(
        NexusNoteContract localBaseline,
        NexusNoteContract remoteIncoming,
        string localDeviceNameForConflict,
        string remoteDeviceNameForConflict,
        bool isServerCanonicalPerspective,
        ConflictResolutionPolicy policy,
        DateTimeOffset utcNow)
    {
        var commonBase = Math.Min(localBaseline.BaseVersion, remoteIncoming.BaseVersion);
        var nextVersion = Math.Max(localBaseline.Version, remoteIncoming.Version) + 1;

        var archivedLocal = CreateRevision(localBaseline, utcNow, "sync_conflict_preserved");
        var archivedRemote = CreateRevision(remoteIncoming, utcNow, "sync_conflict_preserved");

        var conflict = new SyncConflictRecord
        {
            ConflictId = Guid.CreateVersion7(),
            EntityType = "nexus.note",
            NoteId = localBaseline.NoteId,
            CommonBaseVersion = commonBase,
            LocalVersion = localBaseline.Version,
            LocalTitle = localBaseline.Title,
            LocalBody = localBaseline.Body,
            LocalIsDeleted = localBaseline.IsDeleted,
            LocalModifiedAtUtc = localBaseline.ModifiedAtUtc,
            LocalDeviceName = localDeviceNameForConflict,
            RemoteVersion = remoteIncoming.Version,
            RemoteTitle = remoteIncoming.Title,
            RemoteBody = remoteIncoming.Body,
            RemoteIsDeleted = remoteIncoming.IsDeleted,
            RemoteModifiedAtUtc = remoteIncoming.ModifiedAtUtc,
            RemoteDeviceName = remoteDeviceNameForConflict,
            IsResolved = false,
            DetectedAtUtc = utcNow
        };

        if (policy == ConflictResolutionPolicy.Manual)
        {
            // Keep existing local baseline intact until user resolves; never silently overwrite
            var flagged = localBaseline with
            {
                HasConflict = true,
                BaseVersion = isServerCanonicalPerspective ? localBaseline.Version : remoteIncoming.Version,
                NeedsSync = !isServerCanonicalPerspective
            };

            return new NoteMergeEvaluation(
                NoteMergeOutcomeKind.ConflictDetected,
                flagged,
                conflict,
                [archivedLocal, archivedRemote]);
        }

        // Explicit automatic policies (LastWriteWins / PreferLocal / PreferRemote) STILL archive both versions
        var chooseRemote = policy switch
        {
            ConflictResolutionPolicy.PreferRemote => true,
            ConflictResolutionPolicy.PreferLocal => false,
            ConflictResolutionPolicy.LastWriteWins => remoteIncoming.ModifiedAtUtc > localBaseline.ModifiedAtUtc,
            _ => false
        };

        var winner = chooseRemote ? remoteIncoming : localBaseline;
        var resolvedConflict = conflict with
        {
            IsResolved = true,
            ResolutionSummary = $"Auto:{policy} ({(chooseRemote ? remoteDeviceNameForConflict : localDeviceNameForConflict)})",
            ResolvedAtUtc = utcNow
        };

        var mergedNote = winner with
        {
            Version = nextVersion,
            BaseVersion = isServerCanonicalPerspective ? nextVersion : remoteIncoming.Version,
            HasConflict = false,
            NeedsSync = !isServerCanonicalPerspective && !chooseRemote,
            ModifiedAtUtc = utcNow
        };

        return new NoteMergeEvaluation(
            NoteMergeOutcomeKind.ConflictDetected,
            mergedNote,
            resolvedConflict,
            [archivedLocal, archivedRemote, CreateRevision(mergedNote, utcNow, "conflict_auto_resolved")]);
    }

    public static NoteRevisionSnapshot CreateRevision(NexusNoteContract note, DateTimeOffset savedAtUtc, string reason)
        => new()
        {
            RevisionId = Guid.CreateVersion7(),
            NoteId = note.NoteId,
            Version = note.Version,
            Title = note.Title,
            Body = note.Body,
            ContentHash = note.ContentHash,
            ModifiedByDeviceId = note.LastModifiedByDeviceId,
            ModifiedByDeviceName = note.LastModifiedByDeviceName,
            SavedAtUtc = savedAtUtc,
            IsDeleted = note.IsDeleted,
            Reason = reason
        };
}

