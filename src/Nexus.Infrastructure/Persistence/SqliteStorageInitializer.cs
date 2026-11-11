using Microsoft.Extensions.Logging;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.State;
using Nexus.Core.Storage;

namespace Nexus.Infrastructure.Persistence;

/// <summary>
/// Creates and verifies the local SQLite schema idempotently at startup.
/// </summary>
public sealed class SqliteStorageInitializer : IStorageInitializer
{
    private const int CurrentSchemaVersion = 4;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;
    private readonly IStateManager<NexusRuntimeState> _stateManager;
    private readonly IClock _clock;
    private readonly ILogger<SqliteStorageInitializer> _logger;

    public SqliteStorageInitializer(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler,
        IStateManager<NexusRuntimeState> stateManager,
        IClock clock,
        ILogger<SqliteStorageInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
        _stateManager = stateManager;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await _failureHandler.ExecuteAsync("InitializeSchema", async ct =>
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS SchemaMigrations (
                        Version INTEGER PRIMARY KEY,
                        AppliedAtUtc TEXT NOT NULL,
                        Description TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS Devices (
                        Id TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Platform TEXT NOT NULL,
                        Type TEXT NOT NULL,
                        OperatingSystemVersion TEXT NOT NULL,
                        AppVersion TEXT NOT NULL,
                        HardwareFingerprint TEXT NULL,
                        Capabilities INTEGER NOT NULL,
                        Status TEXT NOT NULL,
                        RegisteredAtUtc TEXT NOT NULL,
                        LastSeenAtUtc TEXT NOT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_Devices_Platform ON Devices (Platform);

                    CREATE TABLE IF NOT EXISTS UserSessions (
                        SessionId TEXT PRIMARY KEY,
                        UserId TEXT NOT NULL,
                        DeviceId TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        IssuedAtUtc TEXT NOT NULL,
                        ExpiresAtUtc TEXT NOT NULL,
                        ClientAddress TEXT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_UserSessions_DeviceId ON UserSessions (DeviceId);

                    CREATE TABLE IF NOT EXISTS LocalSettings (
                        Key TEXT PRIMARY KEY,
                        ValueJson TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS Notifications (
                        NotificationId TEXT PRIMARY KEY,
                        SourceDeviceId TEXT NOT NULL,
                        TargetDeviceId TEXT NULL,
                        AppName TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Body TEXT NOT NULL,
                        Urgency TEXT NOT NULL,
                        IsDismissed INTEGER NOT NULL,
                        ActionsJson TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        ExpiresAtUtc TEXT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_Notifications_Pending ON Notifications (IsDismissed, CreatedAtUtc DESC);

                    CREATE TABLE IF NOT EXISTS SyncCheckpoints (
                        Scope TEXT NOT NULL,
                        TargetDeviceId TEXT NOT NULL,
                        Revision INTEGER NOT NULL,
                        Status TEXT NOT NULL,
                        ItemsJson TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL,
                        PRIMARY KEY (Scope, TargetDeviceId)
                    );

                    CREATE TABLE IF NOT EXISTS TrustedDevices (
                        DeviceId TEXT PRIMARY KEY,
                        CustomName TEXT NOT NULL,
                        Platform TEXT NOT NULL,
                        DeviceType TEXT NOT NULL,
                        OperatingSystemVersion TEXT NOT NULL,
                        AppVersion TEXT NOT NULL,
                        PublicKeySpkiBase64 TEXT NOT NULL,
                        PublicKeyFingerprint TEXT NOT NULL,
                        Permissions INTEGER NOT NULL,
                        TrustStatus TEXT NOT NULL,
                        PairedAtUtc TEXT NOT NULL,
                        LastAccessAtUtc TEXT NOT NULL,
                        RevokedAtUtc TEXT NULL,
                        RevocationReason TEXT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_TrustedDevices_Status ON TrustedDevices (TrustStatus);

                    CREATE TABLE IF NOT EXISTS PairingSessions (
                        PairingId TEXT PRIMARY KEY,
                        HostDeviceId TEXT NOT NULL,
                        HostDeviceName TEXT NOT NULL,
                        HostPublicKeySpkiBase64 TEXT NOT NULL,
                        HostPublicKeyFingerprint TEXT NOT NULL,
                        CandidateDeviceId TEXT NULL,
                        CandidateDeviceName TEXT NULL,
                        CandidatePlatform TEXT NULL,
                        CandidateDeviceType TEXT NULL,
                        CandidateAppVersion TEXT NULL,
                        CandidateOperatingSystemVersion TEXT NULL,
                        CandidatePublicKeySpkiBase64 TEXT NULL,
                        CandidatePublicKeyFingerprint TEXT NULL,
                        VerificationCode TEXT NULL,
                        OneTimeSecretHash TEXT NOT NULL,
                        ChallengeNonce TEXT NOT NULL,
                        State TEXT NOT NULL,
                        QrPayloadJson TEXT NULL,
                        FailureReason TEXT NULL,
                        IssuedAtUtc TEXT NOT NULL,
                        ExpiresAtUtc TEXT NOT NULL,
                        CompletedAtUtc TEXT NULL,
                        RequestedPermissions INTEGER NOT NULL,
                        GrantedPermissions INTEGER NOT NULL,
                        SecretConsumed INTEGER NOT NULL,
                        TokensFinalized INTEGER NOT NULL,
                        PendingAccessToken TEXT NULL,
                        PendingRefreshToken TEXT NULL,
                        PendingAccessExpiresAtUtc TEXT NULL,
                        PendingRefreshExpiresAtUtc TEXT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS RefreshTokens (
                        TokenHash TEXT PRIMARY KEY,
                        DeviceId TEXT NOT NULL,
                        IssuedAtUtc TEXT NOT NULL,
                        ExpiresAtUtc TEXT NOT NULL,
                        IsRevoked INTEGER NOT NULL,
                        ReplacedByHash TEXT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_RefreshTokens_DeviceId ON RefreshTokens (DeviceId);

                    CREATE TABLE IF NOT EXISTS DeviceEndpoints (
                        EndpointId TEXT PRIMARY KEY,
                        DeviceId TEXT NOT NULL,
                        DeviceName TEXT NOT NULL,
                        Label TEXT NOT NULL,
                        RouteKind TEXT NOT NULL,
                        Transport TEXT NOT NULL,
                        EndpointUrl TEXT NOT NULL,
                        Priority INTEGER NOT NULL,
                        ExpectedTlsThumbprint TEXT NULL,
                        ExpectedHostKeyFingerprint TEXT NULL,
                        Origin TEXT NOT NULL,
                        IsEnabled INTEGER NOT NULL,
                        LastLatencyMs INTEGER NULL,
                        LastVerifiedAtUtc TEXT NULL,
                        LastConnectedAtUtc TEXT NULL,
                        LastError TEXT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_DeviceEndpoints_DeviceId ON DeviceEndpoints (DeviceId);
                    CREATE UNIQUE INDEX IF NOT EXISTS UX_DeviceEndpoints_DeviceUrl ON DeviceEndpoints (DeviceId, EndpointUrl);

                    CREATE TABLE IF NOT EXISTS ClipboardEntries (
                        EntryId TEXT PRIMARY KEY,
                        OriginDeviceId TEXT NOT NULL,
                        OriginDeviceName TEXT NOT NULL,
                        ContentText TEXT NOT NULL,
                        ContentHash TEXT NOT NULL,
                        SyncOriginToken TEXT NOT NULL,
                        CapturedAtUtc TEXT NOT NULL,
                        IsRemoteReceived INTEGER NOT NULL DEFAULT 0,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_ClipboardEntries_CapturedAt ON ClipboardEntries (CapturedAtUtc DESC);
                    CREATE INDEX IF NOT EXISTS IX_ClipboardEntries_ContentHash ON ClipboardEntries (ContentHash, CapturedAtUtc DESC);

                    CREATE TABLE IF NOT EXISTS Notes (
                        NoteId TEXT PRIMARY KEY,
                        Title TEXT NOT NULL,
                        Body TEXT NOT NULL,
                        Version INTEGER NOT NULL,
                        BaseVersion INTEGER NOT NULL,
                        ContentHash TEXT NOT NULL,
                        LastModifiedByDeviceId TEXT NOT NULL,
                        LastModifiedByDeviceName TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        ModifiedAtUtc TEXT NOT NULL,
                        IsDeleted INTEGER NOT NULL DEFAULT 0,
                        DeletedAtUtc TEXT NULL,
                        HasConflict INTEGER NOT NULL DEFAULT 0,
                        NeedsSync INTEGER NOT NULL DEFAULT 0,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_Notes_ModifiedAt ON Notes (ModifiedAtUtc DESC);
                    CREATE INDEX IF NOT EXISTS IX_Notes_IsDeleted ON Notes (IsDeleted, ModifiedAtUtc DESC);

                    CREATE TABLE IF NOT EXISTS NoteRevisions (
                        RevisionId TEXT PRIMARY KEY,
                        NoteId TEXT NOT NULL,
                        Version INTEGER NOT NULL,
                        Title TEXT NOT NULL,
                        Body TEXT NOT NULL,
                        ContentHash TEXT NOT NULL,
                        ModifiedByDeviceId TEXT NOT NULL,
                        ModifiedByDeviceName TEXT NOT NULL,
                        SavedAtUtc TEXT NOT NULL,
                        IsDeleted INTEGER NOT NULL DEFAULT 0,
                        Reason TEXT NOT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_NoteRevisions_NoteId_Saved ON NoteRevisions (NoteId, SavedAtUtc DESC);

                    CREATE TABLE IF NOT EXISTS SyncConflicts (
                        ConflictId TEXT PRIMARY KEY,
                        EntityType TEXT NOT NULL,
                        NoteId TEXT NOT NULL,
                        CommonBaseVersion INTEGER NOT NULL,
                        LocalVersion INTEGER NOT NULL,
                        LocalTitle TEXT NOT NULL,
                        LocalBody TEXT NOT NULL,
                        LocalIsDeleted INTEGER NOT NULL,
                        LocalModifiedAtUtc TEXT NOT NULL,
                        LocalDeviceName TEXT NOT NULL,
                        RemoteVersion INTEGER NOT NULL,
                        RemoteTitle TEXT NOT NULL,
                        RemoteBody TEXT NOT NULL,
                        RemoteIsDeleted INTEGER NOT NULL,
                        RemoteModifiedAtUtc TEXT NOT NULL,
                        RemoteDeviceName TEXT NOT NULL,
                        IsResolved INTEGER NOT NULL DEFAULT 0,
                        ResolutionSummary TEXT NULL,
                        DetectedAtUtc TEXT NOT NULL,
                        ResolvedAtUtc TEXT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_SyncConflicts_NoteId_Resolved ON SyncConflicts (NoteId, IsResolved, DetectedAtUtc DESC);

                    CREATE TABLE IF NOT EXISTS SyncOperationQueue (
                        QueueId TEXT PRIMARY KEY,
                        Scope TEXT NOT NULL,
                        OperationKind TEXT NOT NULL,
                        EntityId TEXT NOT NULL,
                        PayloadJson TEXT NOT NULL,
                        EnqueuedAtUtc TEXT NOT NULL,
                        AttemptCount INTEGER NOT NULL DEFAULT 0,
                        Status TEXT NOT NULL,
                        LastError TEXT NULL,
                        ContractVersion TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_SyncOperationQueue_Status_Enqueued ON SyncOperationQueue (Status, Scope, EnqueuedAtUtc ASC);

                    INSERT OR IGNORE INTO SchemaMigrations (Version, AppliedAtUtc, Description)
                    VALUES ($version, $appliedAt, $description);
                    """;

                cmd.Parameters.AddWithValue("$version", CurrentSchemaVersion);
                cmd.Parameters.AddWithValue("$appliedAt", _clock.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$description", "NEXUS Foundation, Core, Security/Pairing, Connectivity & Basic Sync SQLite schema");
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _stateManager.Update(s => s with
            {
                IsStorageInitialized = true,
                LastDiagnosticMessage = "SQLite storage initialized",
                UpdatedAtUtc = _clock.UtcNow
            });
            _logger.LogInformation("SQLite storage initialized at {DatabasePath}", _connectionFactory.DatabasePath);
        }

        return result;
    }
}

