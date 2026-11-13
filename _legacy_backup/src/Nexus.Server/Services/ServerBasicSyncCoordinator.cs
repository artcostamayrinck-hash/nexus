using Microsoft.AspNetCore.SignalR;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Sync;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Core.Sync;
using Nexus.Server.Hubs;

namespace Nexus.Server.Services;

public sealed class ServerBasicSyncCoordinator
{
    private readonly IClipboardRepository _clipboardRepo;
    private readonly INotesRepository _notesRepo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INexusPresenceTracker _presenceTracker;
    private readonly IHubContext<NexusHub, INexusHubClient> _hubContext;
    private readonly IClock _clock;
    private readonly ILogger<ServerBasicSyncCoordinator> _logger;
    private readonly SemaphoreSlim _notesSyncLock = new(1, 1);

    public ServerBasicSyncCoordinator(
        IClipboardRepository clipboardRepo,
        INotesRepository notesRepo,
        IServiceScopeFactory scopeFactory,
        INexusPresenceTracker presenceTracker,
        IHubContext<NexusHub, INexusHubClient> hubContext,
        IClock clock,
        ILogger<ServerBasicSyncCoordinator> logger)
    {
        _clipboardRepo = clipboardRepo;
        _notesRepo = notesRepo;
        _scopeFactory = scopeFactory;
        _presenceTracker = presenceTracker;
        _hubContext = hubContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<TrustedDeviceRecord>> AuthorizeCallerAsync(
        Guid callerDeviceId,
        NexusPermission requiredPermission,
        CancellationToken cancellationToken)
    {
        if (callerDeviceId == Guid.Empty)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Identidade do dispositivo ausente.", code: "DEVICE_IDENTITY_MISSING"));
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var deviceResult = await trustedRepo.GetByIdAsync(callerDeviceId, cancellationToken).ConfigureAwait(false);
        if (deviceResult.IsFailure || deviceResult.Value is null)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Dispositivo não está autorizado neste servidor.", code: "DEVICE_NOT_TRUSTED"));
        }

        var device = deviceResult.Value;
        if (device.TrustStatus != DeviceTrustStatus.Authorized)
        {
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Unauthorized("Acesso do dispositivo foi revogado.", code: "DEVICE_REVOKED"));
        }

        if (!device.Permissions.HasFlag(requiredPermission))
        {
            var code = requiredPermission == NexusPermission.Clipboard
                ? "PERMISSION_DENIED_CLIPBOARD"
                : (requiredPermission == NexusPermission.Notes ? "PERMISSION_DENIED_NOTES" : "PERMISSION_DENIED");
            return Result<TrustedDeviceRecord>.Failure(
                NexusError.Forbidden($"Dispositivo '{device.CustomName}' não possui permissão {requiredPermission}.", code: code));
        }

        return Result<TrustedDeviceRecord>.Success(device);
    }

    public async Task<Result<ClipboardEntryContract>> IngestClipboardAsync(
        TrustedDeviceRecord caller,
        ClipboardEntryContract entry,
        CancellationToken cancellationToken)
    {
        var canonical = entry with
        {
            OriginDeviceId = caller.DeviceId,
            OriginDeviceName = string.IsNullOrWhiteSpace(entry.OriginDeviceName) ? caller.CustomName : entry.OriginDeviceName,
            IsRemoteReceived = false
        };

        var errors = canonical.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Result<ClipboardEntryContract>.Failure(
                NexusError.Validation(errors[0].Message, code: errors[0].Code));
        }

        var saved = await _clipboardRepo.AddEntryAsync(canonical, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return Result<ClipboardEntryContract>.Failure(saved.Error!);
        }

        await _clipboardRepo.TrimToLimitAsync(500, cancellationToken).ConfigureAwait(false);
        await BroadcastClipboardToAuthorizedPeersAsync(caller.DeviceId, canonical, cancellationToken).ConfigureAwait(false);

        return Result<ClipboardEntryContract>.Success(canonical);
    }

    public Task<Result<IReadOnlyList<ClipboardEntryContract>>> ListClipboardHistoryAsync(
        int limit,
        CancellationToken cancellationToken)
        => _clipboardRepo.ListHistoryAsync(Math.Clamp(limit, 1, 500), cancellationToken);

    public async Task<Result<NoteSyncResponse>> SynchronizeNotesAsync(
        TrustedDeviceRecord caller,
        NoteSyncRequest request,
        CancellationToken cancellationToken)
    {
        await _notesSyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            var acceptedIds = new List<Guid>();
            var detectedConflicts = new List<SyncConflictRecord>();
            var broadcastUpdates = new List<NexusNoteContract>();

            foreach (var rawIncoming in request.LocalChanges)
            {
                var incoming = rawIncoming with
                {
                    LastModifiedByDeviceId = caller.DeviceId,
                    LastModifiedByDeviceName = string.IsNullOrWhiteSpace(rawIncoming.LastModifiedByDeviceName)
                        ? caller.CustomName
                        : rawIncoming.LastModifiedByDeviceName
                };

                var existingResult = await _notesRepo.GetByIdAsync(incoming.NoteId, cancellationToken).ConfigureAwait(false);
                if (existingResult.IsFailure)
                {
                    return Result<NoteSyncResponse>.Failure(existingResult.Error!);
                }

                var existing = existingResult.Value;
                var eval = NoteConflictMergeEngine.EvaluateServerPush(existing, incoming, request.Policy, now);

                if (eval.Outcome == NoteMergeOutcomeKind.NoChange)
                {
                    // If client resolved a previously flagged conflict using AcceptRemote (same content hash),
                    // clear the server conflict flag and mark any stale conflict records as resolved.
                    if (existing is not null &&
                        existing.HasConflict &&
                        !incoming.HasConflict &&
                        incoming.BaseVersion >= existing.Version)
                    {
                        var cleared = existing with
                        {
                            Version = Math.Max(existing.Version + 1, incoming.Version),
                            BaseVersion = Math.Max(existing.Version + 1, incoming.Version),
                            HasConflict = false,
                            NeedsSync = false,
                            ModifiedAtUtc = now
                        };

                        await _notesRepo.UpsertNoteAsync(cleared, cancellationToken).ConfigureAwait(false);
                        await ResolveOpenConflictsForNoteAsync(cleared.NoteId, $"Resolvido pelo dispositivo {caller.CustomName}", now, cancellationToken).ConfigureAwait(false);
                        broadcastUpdates.Add(cleared);
                    }

                    acceptedIds.Add(incoming.NoteId);
                    continue;
                }

                if (eval.Outcome == NoteMergeOutcomeKind.AcceptedForward)
                {
                    await _notesRepo.UpsertNoteAsync(eval.ResultingNote, cancellationToken).ConfigureAwait(false);
                    foreach (var rev in eval.RevisionsToArchive)
                    {
                        await _notesRepo.AppendRevisionAsync(rev, cancellationToken).ConfigureAwait(false);
                    }

                    await ResolveOpenConflictsForNoteAsync(
                        eval.ResultingNote.NoteId,
                        $"Resolvido pela versão v{eval.ResultingNote.Version} ({caller.CustomName})",
                        now,
                        cancellationToken).ConfigureAwait(false);

                    acceptedIds.Add(incoming.NoteId);
                    broadcastUpdates.Add(eval.ResultingNote);
                    continue;
                }

                // ConflictDetected
                await _notesRepo.UpsertNoteAsync(eval.ResultingNote, cancellationToken).ConfigureAwait(false);
                foreach (var rev in eval.RevisionsToArchive)
                {
                    await _notesRepo.AppendRevisionAsync(rev, cancellationToken).ConfigureAwait(false);
                }

                if (eval.ConflictRecord is not null)
                {
                    await _notesRepo.SaveConflictAsync(eval.ConflictRecord, cancellationToken).ConfigureAwait(false);
                    detectedConflicts.Add(eval.ConflictRecord);

                    if (eval.ConflictRecord.IsResolved)
                    {
                        acceptedIds.Add(incoming.NoteId);
                        broadcastUpdates.Add(eval.ResultingNote);
                    }
                }
            }

            var canonicalResult = await _notesRepo.ListAsync(includeDeleted: true, cancellationToken).ConfigureAwait(false);
            if (canonicalResult.IsFailure)
            {
                return Result<NoteSyncResponse>.Failure(canonicalResult.Error!);
            }

            foreach (var noteToBroadcast in broadcastUpdates)
            {
                await BroadcastNoteToAuthorizedPeersAsync(caller.DeviceId, noteToBroadcast, cancellationToken).ConfigureAwait(false);
            }

            return Result<NoteSyncResponse>.Success(new NoteSyncResponse
            {
                ServerTimestampUtc = now,
                CanonicalNotes = canonicalResult.Value,
                DetectedConflicts = detectedConflicts,
                AcceptedNoteIds = acceptedIds
            });
        }
        finally
        {
            _notesSyncLock.Release();
        }
    }

    public Task<Result<IReadOnlyList<NexusNoteContract>>> ListNotesAsync(string? query, bool includeDeleted, CancellationToken ct)
        => string.IsNullOrWhiteSpace(query)
            ? _notesRepo.ListAsync(includeDeleted, ct)
            : _notesRepo.SearchAsync(query, includeDeleted, ct);

    public Task<Result<IReadOnlyList<NoteRevisionSnapshot>>> GetRevisionsAsync(Guid noteId, CancellationToken ct)
        => _notesRepo.GetRevisionsAsync(noteId, ct);

    public async Task<Result<NexusNoteContract>> RestoreRevisionAsync(
        TrustedDeviceRecord caller,
        Guid noteId,
        Guid revisionId,
        CancellationToken ct)
    {
        await _notesSyncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var revsResult = await _notesRepo.GetRevisionsAsync(noteId, ct).ConfigureAwait(false);
            if (revsResult.IsFailure)
            {
                return Result<NexusNoteContract>.Failure(revsResult.Error!);
            }

            var rev = revsResult.Value.FirstOrDefault(r => r.RevisionId == revisionId);
            if (rev is null)
            {
                return Result<NexusNoteContract>.Failure(
                    NexusError.NotFound($"Revisão {revisionId} não encontrada.", code: "REVISION_NOT_FOUND"));
            }

            var current = (await _notesRepo.GetByIdAsync(noteId, ct).ConfigureAwait(false)).Value;
            var now = _clock.UtcNow;
            var nextVersion = (current?.Version ?? rev.Version) + 1;
            var restoredTitle = string.IsNullOrWhiteSpace(rev.Title) ? "Nota Restaurada" : rev.Title;

            var restored = new NexusNoteContract
            {
                NoteId = noteId,
                Title = restoredTitle,
                Body = rev.Body,
                Version = nextVersion,
                BaseVersion = nextVersion,
                ContentHash = SyncContentHashHelper.ComputeNoteHash(restoredTitle, rev.Body, isDeleted: false),
                LastModifiedByDeviceId = caller.DeviceId,
                LastModifiedByDeviceName = caller.CustomName,
                CreatedAtUtc = current?.CreatedAtUtc ?? rev.SavedAtUtc,
                ModifiedAtUtc = now,
                IsDeleted = false,
                HasConflict = false,
                NeedsSync = false
            };

            await _notesRepo.UpsertNoteAsync(restored, ct).ConfigureAwait(false);
            await _notesRepo.AppendRevisionAsync(NoteConflictMergeEngine.CreateRevision(restored, now, $"restored_v{rev.Version}"), ct).ConfigureAwait(false);
            await ResolveOpenConflictsForNoteAsync(noteId, $"Revisão v{rev.Version} restaurada por {caller.CustomName}", now, ct).ConfigureAwait(false);
            await BroadcastNoteToAuthorizedPeersAsync(caller.DeviceId, restored, ct).ConfigureAwait(false);

            return Result<NexusNoteContract>.Success(restored);
        }
        finally
        {
            _notesSyncLock.Release();
        }
    }

    public Task<Result<IReadOnlyList<SyncConflictRecord>>> ListConflictsAsync(bool unresolvedOnly, CancellationToken ct)
        => _notesRepo.ListConflictsAsync(unresolvedOnly, ct);

    public async Task<Result<NexusNoteContract>> ResolveConflictAsync(
        TrustedDeviceRecord caller,
        Guid conflictId,
        ResolveConflictRequest request,
        CancellationToken ct)
    {
        await _notesSyncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conflictResult = await _notesRepo.GetConflictByIdAsync(conflictId, ct).ConfigureAwait(false);
            if (conflictResult.IsFailure)
            {
                return Result<NexusNoteContract>.Failure(conflictResult.Error!);
            }

            if (conflictResult.Value is null)
            {
                return Result<NexusNoteContract>.Failure(
                    NexusError.NotFound($"Conflito {conflictId} não encontrado.", code: "CONFLICT_NOT_FOUND"));
            }

            var conflict = conflictResult.Value;
            var current = (await _notesRepo.GetByIdAsync(conflict.NoteId, ct).ConfigureAwait(false)).Value;
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

            var nextVersion = Math.Max(Math.Max(conflict.LocalVersion, conflict.RemoteVersion), current?.Version ?? 0) + 1;
            var resolvedNote = new NexusNoteContract
            {
                NoteId = conflict.NoteId,
                Title = title,
                Body = body,
                Version = nextVersion,
                BaseVersion = nextVersion,
                ContentHash = SyncContentHashHelper.ComputeNoteHash(title, body, isDeleted),
                LastModifiedByDeviceId = caller.DeviceId,
                LastModifiedByDeviceName = caller.CustomName,
                CreatedAtUtc = current?.CreatedAtUtc ?? conflict.LocalModifiedAtUtc,
                ModifiedAtUtc = now,
                IsDeleted = isDeleted,
                DeletedAtUtc = isDeleted ? now : null,
                HasConflict = false,
                NeedsSync = false
            };

            await _notesRepo.UpsertNoteAsync(resolvedNote, ct).ConfigureAwait(false);
            await _notesRepo.AppendRevisionAsync(NoteConflictMergeEngine.CreateRevision(resolvedNote, now, $"conflict_resolved_{request.Choice}"), ct).ConfigureAwait(false);
            await _notesRepo.MarkConflictResolvedAsync(conflictId, $"Resolvido ({request.Choice}) por {caller.CustomName}", now, ct).ConfigureAwait(false);
            await BroadcastNoteToAuthorizedPeersAsync(caller.DeviceId, resolvedNote, ct).ConfigureAwait(false);

            return Result<NexusNoteContract>.Success(resolvedNote);
        }
        finally
        {
            _notesSyncLock.Release();
        }
    }

    private async Task ResolveOpenConflictsForNoteAsync(
        Guid noteId,
        string summary,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var openResult = await _notesRepo.ListConflictsAsync(unresolvedOnly: true, ct).ConfigureAwait(false);
        if (openResult.IsFailure)
        {
            return;
        }

        foreach (var c in openResult.Value.Where(x => x.NoteId == noteId))
        {
            await _notesRepo.MarkConflictResolvedAsync(c.ConflictId, summary, now, ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastClipboardToAuthorizedPeersAsync(
        Guid sourceDeviceId,
        ClipboardEntryContract entry,
        CancellationToken ct)
    {
        var connections = await GetAuthorizedConnectionsAsync(sourceDeviceId, NexusPermission.Clipboard, ct).ConfigureAwait(false);
        foreach (var connectionId in connections)
        {
            try
            {
                await _hubContext.Clients.Client(connectionId).ReceiveClipboardEntry(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao transmitir clipboard para conexão {ConnectionId}.", connectionId);
            }
        }
    }

    private async Task BroadcastNoteToAuthorizedPeersAsync(
        Guid sourceDeviceId,
        NexusNoteContract note,
        CancellationToken ct)
    {
        var connections = await GetAuthorizedConnectionsAsync(sourceDeviceId, NexusPermission.Notes, ct).ConfigureAwait(false);
        foreach (var connectionId in connections)
        {
            try
            {
                await _hubContext.Clients.Client(connectionId).ReceiveNoteUpdated(note).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao transmitir nota para conexão {ConnectionId}.", connectionId);
            }
        }
    }

    private async Task<IReadOnlyList<string>> GetAuthorizedConnectionsAsync(
        Guid excludeDeviceId,
        NexusPermission requiredPermission,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var trustedResult = await trustedRepo.ListAuthorizedAsync(ct).ConfigureAwait(false);
        if (trustedResult.IsFailure)
        {
            return Array.Empty<string>();
        }

        var permittedDeviceIds = trustedResult.Value
            .Where(d => d.DeviceId != excludeDeviceId &&
                        d.TrustStatus == DeviceTrustStatus.Authorized &&
                        d.Permissions.HasFlag(requiredPermission))
            .Select(d => d.DeviceId)
            .ToHashSet();

        if (permittedDeviceIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        return _presenceTracker.GetConnectedSessions()
            .Where(s => permittedDeviceIds.Contains(s.DeviceId))
            .Select(s => s.ConnectionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
