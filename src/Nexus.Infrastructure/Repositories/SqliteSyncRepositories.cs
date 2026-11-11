using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Common;
using Nexus.Contracts.Sync;
using Nexus.Core.Errors;
using Nexus.Core.Sync;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

public sealed class SqliteClipboardRepository : IClipboardRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteClipboardRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result> AddEntryAsync(ClipboardEntryContract entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return _failureHandler.ExecuteAsync("Clipboard.AddEntry", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO ClipboardEntries
                (EntryId, OriginDeviceId, OriginDeviceName, ContentText, ContentHash, SyncOriginToken, CapturedAtUtc, IsRemoteReceived, ContractVersion)
                VALUES ($id, $originId, $originName, $text, $hash, $token, $capturedAt, $isRemote, $contract);
                """;
            cmd.Parameters.AddWithValue("$id", entry.EntryId.ToString("D"));
            cmd.Parameters.AddWithValue("$originId", entry.OriginDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$originName", entry.OriginDeviceName);
            cmd.Parameters.AddWithValue("$text", entry.ContentText);
            cmd.Parameters.AddWithValue("$hash", entry.ContentHash);
            cmd.Parameters.AddWithValue("$token", entry.SyncOriginToken.ToString("D"));
            cmd.Parameters.AddWithValue("$capturedAt", entry.CapturedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$isRemote", entry.IsRemoteReceived ? 1 : 0);
            cmd.Parameters.AddWithValue("$contract", entry.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<ClipboardEntryContract>>> ListHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);

        return _failureHandler.ExecuteAsync<IReadOnlyList<ClipboardEntryContract>>("Clipboard.ListHistory", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EntryId, OriginDeviceId, OriginDeviceName, ContentText, ContentHash, SyncOriginToken, CapturedAtUtc, IsRemoteReceived, ContractVersion
                FROM ClipboardEntries
                ORDER BY CapturedAtUtc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            var items = new List<ClipboardEntryContract>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapClipboardEntry(reader));
            }

            return items;
        }, cancellationToken);
    }

    public Task<Result<ClipboardEntryContract?>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<ClipboardEntryContract?>("Clipboard.GetLatest", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EntryId, OriginDeviceId, OriginDeviceName, ContentText, ContentHash, SyncOriginToken, CapturedAtUtc, IsRemoteReceived, ContractVersion
                FROM ClipboardEntries
                ORDER BY CapturedAtUtc DESC
                LIMIT 1;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapClipboardEntry(reader) : null;
        }, cancellationToken);
    }

    public Task<Result<bool>> ContainsRecentHashAsync(
        string contentHash,
        TimeSpan window,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Clipboard.ContainsRecentHash", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1)
                FROM ClipboardEntries
                WHERE ContentHash = $hash AND CapturedAtUtc >= $cutoff;
                """;
            cmd.Parameters.AddWithValue("$hash", contentHash);
            cmd.Parameters.AddWithValue("$cutoff", utcNow.Subtract(window).ToUniversalTime().ToString("O"));

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            return count > 0;
        }, cancellationToken);
    }

    public Task<Result<int>> TrimToLimitAsync(int maxEntries, CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(maxEntries, 1, 500);

        return _failureHandler.ExecuteAsync("Clipboard.TrimToLimit", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM ClipboardEntries
                WHERE EntryId NOT IN (
                    SELECT EntryId
                    FROM ClipboardEntries
                    ORDER BY CapturedAtUtc DESC
                    LIMIT $maxEntries
                );
                """;
            cmd.Parameters.AddWithValue("$maxEntries", bounded);

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Clipboard.ClearHistory", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ClipboardEntries;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static ClipboardEntryContract MapClipboardEntry(SqliteDataReader reader) => new()
    {
        EntryId = Guid.Parse(reader.GetString(0)),
        OriginDeviceId = Guid.Parse(reader.GetString(1)),
        OriginDeviceName = reader.GetString(2),
        ContentText = reader.GetString(3),
        ContentHash = reader.GetString(4),
        SyncOriginToken = Guid.Parse(reader.GetString(5)),
        CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        IsRemoteReceived = reader.GetInt32(7) == 1,
        ContractVersion = reader.GetString(8)
    };
}

public sealed class SqliteNotesRepository : INotesRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteNotesRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result<NexusNoteContract?>> GetByIdAsync(Guid noteId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<NexusNoteContract?>("Notes.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT NoteId, Title, Body, Version, BaseVersion, ContentHash, LastModifiedByDeviceId, LastModifiedByDeviceName,
                       CreatedAtUtc, ModifiedAtUtc, IsDeleted, DeletedAtUtc, HasConflict, NeedsSync, ContractVersion
                FROM Notes
                WHERE NoteId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", noteId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapNote(reader) : null;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<NexusNoteContract>>> ListAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<NexusNoteContract>>("Notes.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = includeDeleted
                ? """
                  SELECT NoteId, Title, Body, Version, BaseVersion, ContentHash, LastModifiedByDeviceId, LastModifiedByDeviceName,
                         CreatedAtUtc, ModifiedAtUtc, IsDeleted, DeletedAtUtc, HasConflict, NeedsSync, ContractVersion
                  FROM Notes
                  ORDER BY ModifiedAtUtc DESC;
                  """
                : """
                  SELECT NoteId, Title, Body, Version, BaseVersion, ContentHash, LastModifiedByDeviceId, LastModifiedByDeviceName,
                         CreatedAtUtc, ModifiedAtUtc, IsDeleted, DeletedAtUtc, HasConflict, NeedsSync, ContractVersion
                  FROM Notes
                  WHERE IsDeleted = 0
                  ORDER BY ModifiedAtUtc DESC;
                  """;

            var notes = new List<NexusNoteContract>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                notes.Add(MapNote(reader));
            }

            return notes;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<NexusNoteContract>>> SearchAsync(
        string query,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ListAsync(includeDeleted, cancellationToken);
        }

        var trimmed = query.Trim();
        return _failureHandler.ExecuteAsync<IReadOnlyList<NexusNoteContract>>("Notes.Search", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = includeDeleted
                ? """
                  SELECT NoteId, Title, Body, Version, BaseVersion, ContentHash, LastModifiedByDeviceId, LastModifiedByDeviceName,
                         CreatedAtUtc, ModifiedAtUtc, IsDeleted, DeletedAtUtc, HasConflict, NeedsSync, ContractVersion
                  FROM Notes
                  WHERE INSTR(LOWER(Title), LOWER($q)) > 0 OR INSTR(LOWER(Body), LOWER($q)) > 0
                  ORDER BY ModifiedAtUtc DESC;
                  """
                : """
                  SELECT NoteId, Title, Body, Version, BaseVersion, ContentHash, LastModifiedByDeviceId, LastModifiedByDeviceName,
                         CreatedAtUtc, ModifiedAtUtc, IsDeleted, DeletedAtUtc, HasConflict, NeedsSync, ContractVersion
                  FROM Notes
                  WHERE IsDeleted = 0 AND (INSTR(LOWER(Title), LOWER($q)) > 0 OR INSTR(LOWER(Body), LOWER($q)) > 0)
                  ORDER BY ModifiedAtUtc DESC;
                  """;
            cmd.Parameters.AddWithValue("$q", trimmed);

            var notes = new List<NexusNoteContract>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                notes.Add(MapNote(reader));
            }

            return notes;
        }, cancellationToken);
    }

    public Task<Result<NexusNoteContract>> UpsertNoteAsync(NexusNoteContract note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        return _failureHandler.ExecuteAsync("Notes.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Notes (
                    NoteId, Title, Body, Version, BaseVersion, ContentHash, LastModifiedByDeviceId, LastModifiedByDeviceName,
                    CreatedAtUtc, ModifiedAtUtc, IsDeleted, DeletedAtUtc, HasConflict, NeedsSync, ContractVersion)
                VALUES (
                    $id, $title, $body, $version, $baseVersion, $hash, $modId, $modName,
                    $createdAt, $modifiedAt, $isDeleted, $deletedAt, $hasConflict, $needsSync, $contract)
                ON CONFLICT(NoteId) DO UPDATE SET
                    Title = excluded.Title,
                    Body = excluded.Body,
                    Version = excluded.Version,
                    BaseVersion = excluded.BaseVersion,
                    ContentHash = excluded.ContentHash,
                    LastModifiedByDeviceId = excluded.LastModifiedByDeviceId,
                    LastModifiedByDeviceName = excluded.LastModifiedByDeviceName,
                    ModifiedAtUtc = excluded.ModifiedAtUtc,
                    IsDeleted = excluded.IsDeleted,
                    DeletedAtUtc = excluded.DeletedAtUtc,
                    HasConflict = excluded.HasConflict,
                    NeedsSync = excluded.NeedsSync,
                    ContractVersion = excluded.ContractVersion;
                """;

            cmd.Parameters.AddWithValue("$id", note.NoteId.ToString("D"));
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$body", note.Body);
            cmd.Parameters.AddWithValue("$version", note.Version);
            cmd.Parameters.AddWithValue("$baseVersion", note.BaseVersion);
            cmd.Parameters.AddWithValue("$hash", note.ContentHash);
            cmd.Parameters.AddWithValue("$modId", note.LastModifiedByDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$modName", note.LastModifiedByDeviceName);
            cmd.Parameters.AddWithValue("$createdAt", note.CreatedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$modifiedAt", note.ModifiedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$isDeleted", note.IsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$deletedAt", note.DeletedAtUtc is { } deleted ? deleted.ToUniversalTime().ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$hasConflict", note.HasConflict ? 1 : 0);
            cmd.Parameters.AddWithValue("$needsSync", note.NeedsSync ? 1 : 0);
            cmd.Parameters.AddWithValue("$contract", note.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return note;
        }, cancellationToken);
    }

    public Task<Result> AppendRevisionAsync(NoteRevisionSnapshot revision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);

        return _failureHandler.ExecuteAsync("Notes.AppendRevision", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO NoteRevisions (
                    RevisionId, NoteId, Version, Title, Body, ContentHash, ModifiedByDeviceId,
                    ModifiedByDeviceName, SavedAtUtc, IsDeleted, Reason, ContractVersion)
                VALUES (
                    $revId, $noteId, $version, $title, $body, $hash, $modId,
                    $modName, $savedAt, $isDeleted, $reason, $contract);
                """;
            cmd.Parameters.AddWithValue("$revId", revision.RevisionId.ToString("D"));
            cmd.Parameters.AddWithValue("$noteId", revision.NoteId.ToString("D"));
            cmd.Parameters.AddWithValue("$version", revision.Version);
            cmd.Parameters.AddWithValue("$title", revision.Title);
            cmd.Parameters.AddWithValue("$body", revision.Body);
            cmd.Parameters.AddWithValue("$hash", revision.ContentHash);
            cmd.Parameters.AddWithValue("$modId", revision.ModifiedByDeviceId.ToString("D"));
            cmd.Parameters.AddWithValue("$modName", revision.ModifiedByDeviceName);
            cmd.Parameters.AddWithValue("$savedAt", revision.SavedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$isDeleted", revision.IsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$reason", revision.Reason);
            cmd.Parameters.AddWithValue("$contract", revision.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<NoteRevisionSnapshot>>> GetRevisionsAsync(Guid noteId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<NoteRevisionSnapshot>>("Notes.GetRevisions", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT RevisionId, NoteId, Version, Title, Body, ContentHash, ModifiedByDeviceId,
                       ModifiedByDeviceName, SavedAtUtc, IsDeleted, Reason, ContractVersion
                FROM NoteRevisions
                WHERE NoteId = $noteId
                ORDER BY Version DESC, SavedAtUtc DESC;
                """;
            cmd.Parameters.AddWithValue("$noteId", noteId.ToString("D"));

            var list = new List<NoteRevisionSnapshot>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new NoteRevisionSnapshot
                {
                    RevisionId = Guid.Parse(reader.GetString(0)),
                    NoteId = Guid.Parse(reader.GetString(1)),
                    Version = reader.GetInt64(2),
                    Title = reader.GetString(3),
                    Body = reader.GetString(4),
                    ContentHash = reader.GetString(5),
                    ModifiedByDeviceId = Guid.Parse(reader.GetString(6)),
                    ModifiedByDeviceName = reader.GetString(7),
                    SavedAtUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    IsDeleted = reader.GetInt32(9) == 1,
                    Reason = reader.GetString(10),
                    ContractVersion = reader.GetString(11)
                });
            }

            return list;
        }, cancellationToken);
    }

    public Task<Result> SaveConflictAsync(SyncConflictRecord conflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        return _failureHandler.ExecuteAsync("Notes.SaveConflict", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

            // Prevent duplicate unresolved conflicts for the exact same divergent versions.
            await using (var dupCmd = conn.CreateCommand())
            {
                dupCmd.CommandText = """
                    SELECT COUNT(1)
                    FROM SyncConflicts
                    WHERE NoteId = $noteId
                      AND IsResolved = 0
                      AND LocalVersion = $localVer
                      AND RemoteVersion = $remoteVer
                      AND ConflictId != $conflictId;
                    """;
                dupCmd.Parameters.AddWithValue("$noteId", conflict.NoteId.ToString("D"));
                dupCmd.Parameters.AddWithValue("$localVer", conflict.LocalVersion);
                dupCmd.Parameters.AddWithValue("$remoteVer", conflict.RemoteVersion);
                dupCmd.Parameters.AddWithValue("$conflictId", conflict.ConflictId.ToString("D"));

                var existingCount = Convert.ToInt32(await dupCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (existingCount > 0)
                {
                    return;
                }
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO SyncConflicts (
                    ConflictId, EntityType, NoteId, CommonBaseVersion,
                    LocalVersion, LocalTitle, LocalBody, LocalIsDeleted, LocalModifiedAtUtc, LocalDeviceName,
                    RemoteVersion, RemoteTitle, RemoteBody, RemoteIsDeleted, RemoteModifiedAtUtc, RemoteDeviceName,
                    IsResolved, ResolutionSummary, DetectedAtUtc, ResolvedAtUtc, ContractVersion)
                VALUES (
                    $id, $entityType, $noteId, $baseVer,
                    $lVer, $lTitle, $lBody, $lDel, $lMod, $lDev,
                    $rVer, $rTitle, $rBody, $rDel, $rMod, $rDev,
                    $isRes, $resSummary, $detAt, $resAt, $contract);
                """;
            cmd.Parameters.AddWithValue("$id", conflict.ConflictId.ToString("D"));
            cmd.Parameters.AddWithValue("$entityType", conflict.EntityType);
            cmd.Parameters.AddWithValue("$noteId", conflict.NoteId.ToString("D"));
            cmd.Parameters.AddWithValue("$baseVer", conflict.CommonBaseVersion);
            cmd.Parameters.AddWithValue("$lVer", conflict.LocalVersion);
            cmd.Parameters.AddWithValue("$lTitle", conflict.LocalTitle);
            cmd.Parameters.AddWithValue("$lBody", conflict.LocalBody);
            cmd.Parameters.AddWithValue("$lDel", conflict.LocalIsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$lMod", conflict.LocalModifiedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$lDev", conflict.LocalDeviceName);
            cmd.Parameters.AddWithValue("$rVer", conflict.RemoteVersion);
            cmd.Parameters.AddWithValue("$rTitle", conflict.RemoteTitle);
            cmd.Parameters.AddWithValue("$rBody", conflict.RemoteBody);
            cmd.Parameters.AddWithValue("$rDel", conflict.RemoteIsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$rMod", conflict.RemoteModifiedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$rDev", conflict.RemoteDeviceName);
            cmd.Parameters.AddWithValue("$isRes", conflict.IsResolved ? 1 : 0);
            cmd.Parameters.AddWithValue("$resSummary", (object?)conflict.ResolutionSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detAt", conflict.DetectedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$resAt", conflict.ResolvedAtUtc is { } res ? res.ToUniversalTime().ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$contract", conflict.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<SyncConflictRecord?>> GetConflictByIdAsync(Guid conflictId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<SyncConflictRecord?>("Notes.GetConflictById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ConflictId, EntityType, NoteId, CommonBaseVersion,
                       LocalVersion, LocalTitle, LocalBody, LocalIsDeleted, LocalModifiedAtUtc, LocalDeviceName,
                       RemoteVersion, RemoteTitle, RemoteBody, RemoteIsDeleted, RemoteModifiedAtUtc, RemoteDeviceName,
                       IsResolved, ResolutionSummary, DetectedAtUtc, ResolvedAtUtc, ContractVersion
                FROM SyncConflicts
                WHERE ConflictId = $id;
                """;
            cmd.Parameters.AddWithValue("$id", conflictId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapConflict(reader) : null;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<SyncConflictRecord>>> ListConflictsAsync(
        bool unresolvedOnly = true,
        CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<SyncConflictRecord>>("Notes.ListConflicts", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = unresolvedOnly
                ? """
                  SELECT ConflictId, EntityType, NoteId, CommonBaseVersion,
                         LocalVersion, LocalTitle, LocalBody, LocalIsDeleted, LocalModifiedAtUtc, LocalDeviceName,
                         RemoteVersion, RemoteTitle, RemoteBody, RemoteIsDeleted, RemoteModifiedAtUtc, RemoteDeviceName,
                         IsResolved, ResolutionSummary, DetectedAtUtc, ResolvedAtUtc, ContractVersion
                  FROM SyncConflicts
                  WHERE IsResolved = 0
                  ORDER BY DetectedAtUtc DESC;
                  """
                : """
                  SELECT ConflictId, EntityType, NoteId, CommonBaseVersion,
                         LocalVersion, LocalTitle, LocalBody, LocalIsDeleted, LocalModifiedAtUtc, LocalDeviceName,
                         RemoteVersion, RemoteTitle, RemoteBody, RemoteIsDeleted, RemoteModifiedAtUtc, RemoteDeviceName,
                         IsResolved, ResolutionSummary, DetectedAtUtc, ResolvedAtUtc, ContractVersion
                  FROM SyncConflicts
                  ORDER BY DetectedAtUtc DESC;
                  """;

            var list = new List<SyncConflictRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(MapConflict(reader));
            }

            return list;
        }, cancellationToken);
    }

    public Task<Result> MarkConflictResolvedAsync(
        Guid conflictId,
        string resolutionSummary,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("Notes.MarkConflictResolved", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SyncConflicts
                SET IsResolved = 1, ResolutionSummary = $summary, ResolvedAtUtc = $resolvedAt
                WHERE ConflictId = $id;
                """;
            cmd.Parameters.AddWithValue("$summary", resolutionSummary);
            cmd.Parameters.AddWithValue("$resolvedAt", resolvedAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$id", conflictId.ToString("D"));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static NexusNoteContract MapNote(SqliteDataReader reader) => new()
    {
        NoteId = Guid.Parse(reader.GetString(0)),
        Title = reader.GetString(1),
        Body = reader.GetString(2),
        Version = reader.GetInt64(3),
        BaseVersion = reader.GetInt64(4),
        ContentHash = reader.GetString(5),
        LastModifiedByDeviceId = Guid.Parse(reader.GetString(6)),
        LastModifiedByDeviceName = reader.GetString(7),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ModifiedAtUtc = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        IsDeleted = reader.GetInt32(10) == 1,
        DeletedAtUtc = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        HasConflict = reader.GetInt32(12) == 1,
        NeedsSync = reader.GetInt32(13) == 1,
        ContractVersion = reader.GetString(14)
    };

    private static SyncConflictRecord MapConflict(SqliteDataReader reader) => new()
    {
        ConflictId = Guid.Parse(reader.GetString(0)),
        EntityType = reader.GetString(1),
        NoteId = Guid.Parse(reader.GetString(2)),
        CommonBaseVersion = reader.GetInt64(3),
        LocalVersion = reader.GetInt64(4),
        LocalTitle = reader.GetString(5),
        LocalBody = reader.GetString(6),
        LocalIsDeleted = reader.GetInt32(7) == 1,
        LocalModifiedAtUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        LocalDeviceName = reader.GetString(9),
        RemoteVersion = reader.GetInt64(10),
        RemoteTitle = reader.GetString(11),
        RemoteBody = reader.GetString(12),
        RemoteIsDeleted = reader.GetInt32(13) == 1,
        RemoteModifiedAtUtc = DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        RemoteDeviceName = reader.GetString(15),
        IsResolved = reader.GetInt32(16) == 1,
        ResolutionSummary = reader.IsDBNull(17) ? null : reader.GetString(17),
        DetectedAtUtc = DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ResolvedAtUtc = reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ContractVersion = reader.GetString(20)
    };
}

public sealed class SqliteSyncQueueRepository : ISyncQueueRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteSyncQueueRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result> EnqueueAsync(SyncQueueItemRecord item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        return _failureHandler.ExecuteAsync("SyncQueue.Enqueue", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Coalesce uncompleted note operations for the same NoteId so the latest snapshot supersedes older queued mutations.
            if (string.Equals(item.Scope, "notes", StringComparison.OrdinalIgnoreCase))
            {
                await using var deleteOld = conn.CreateCommand();
                deleteOld.Transaction = (SqliteTransaction)tx;
                deleteOld.CommandText = """
                    DELETE FROM SyncOperationQueue
                    WHERE Scope = 'notes' AND EntityId = $entityId AND Status != $completed;
                    """;
                deleteOld.Parameters.AddWithValue("$entityId", item.EntityId.ToString("D"));
                deleteOld.Parameters.AddWithValue("$completed", nameof(SyncQueueStatus.Completed));
                await deleteOld.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO SyncOperationQueue
                    (QueueId, Scope, OperationKind, EntityId, PayloadJson, EnqueuedAtUtc, AttemptCount, Status, LastError, ContractVersion)
                    VALUES ($queueId, $scope, $kind, $entityId, $payload, $enqueuedAt, $attempts, $status, $lastError, $contract);
                    """;
                cmd.Parameters.AddWithValue("$queueId", item.QueueId.ToString("D"));
                cmd.Parameters.AddWithValue("$scope", item.Scope);
                cmd.Parameters.AddWithValue("$kind", item.OperationKind.ToString());
                cmd.Parameters.AddWithValue("$entityId", item.EntityId.ToString("D"));
                cmd.Parameters.AddWithValue("$payload", item.PayloadJson);
                cmd.Parameters.AddWithValue("$enqueuedAt", item.EnqueuedAtUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$attempts", item.AttemptCount);
                cmd.Parameters.AddWithValue("$status", item.Status.ToString());
                cmd.Parameters.AddWithValue("$lastError", (object?)item.LastError ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$contract", item.ContractVersion);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<SyncQueueItemRecord>>> ListPendingAsync(string? scope = null, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<SyncQueueItemRecord>>("SyncQueue.ListPending", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(scope)
                ? """
                  SELECT QueueId, Scope, OperationKind, EntityId, PayloadJson, EnqueuedAtUtc, AttemptCount, Status, LastError, ContractVersion
                  FROM SyncOperationQueue
                  WHERE Status != $completed
                  ORDER BY EnqueuedAtUtc ASC;
                  """
                : """
                  SELECT QueueId, Scope, OperationKind, EntityId, PayloadJson, EnqueuedAtUtc, AttemptCount, Status, LastError, ContractVersion
                  FROM SyncOperationQueue
                  WHERE Status != $completed AND Scope = $scope
                  ORDER BY EnqueuedAtUtc ASC;
                  """;
            cmd.Parameters.AddWithValue("$completed", nameof(SyncQueueStatus.Completed));
            if (!string.IsNullOrWhiteSpace(scope))
            {
                cmd.Parameters.AddWithValue("$scope", scope);
            }

            var list = new List<SyncQueueItemRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new SyncQueueItemRecord
                {
                    QueueId = Guid.Parse(reader.GetString(0)),
                    Scope = reader.GetString(1),
                    OperationKind = Enum.Parse<SyncOperationKind>(reader.GetString(2)),
                    EntityId = Guid.Parse(reader.GetString(3)),
                    PayloadJson = reader.GetString(4),
                    EnqueuedAtUtc = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    AttemptCount = reader.GetInt32(6),
                    Status = Enum.Parse<SyncQueueStatus>(reader.GetString(7)),
                    LastError = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ContractVersion = reader.GetString(9)
                });
            }

            return list;
        }, cancellationToken);
    }

    public Task<Result<int>> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("SyncQueue.GetPendingCount", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1)
                FROM SyncOperationQueue
                WHERE Status != $completed;
                """;
            cmd.Parameters.AddWithValue("$completed", nameof(SyncQueueStatus.Completed));

            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public Task<Result> MarkCompletedAsync(Guid queueId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("SyncQueue.MarkCompleted", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SyncOperationQueue
                SET Status = $completed, LastError = NULL
                WHERE QueueId = $id;
                """;
            cmd.Parameters.AddWithValue("$completed", nameof(SyncQueueStatus.Completed));
            cmd.Parameters.AddWithValue("$id", queueId.ToString("D"));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> UpdateAttemptAsync(
        Guid queueId,
        SyncQueueStatus status,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("SyncQueue.UpdateAttempt", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SyncOperationQueue
                SET Status = $status, AttemptCount = AttemptCount + 1, LastError = $lastError
                WHERE QueueId = $id;
                """;
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", queueId.ToString("D"));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result> ClearCompletedAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("SyncQueue.ClearCompleted", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM SyncOperationQueue WHERE Status = $completed;";
            cmd.Parameters.AddWithValue("$completed", nameof(SyncQueueStatus.Completed));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}

