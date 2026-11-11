using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Common;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Pairing;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Repositories;
using Nexus.Security.Identity;
using Nexus.Security.Replay;
using Nexus.Security.Tokens;

namespace Nexus.Infrastructure.Security;

/// <summary>
/// Implements the 7-step QR code pairing protocol with single-use secrets, ECDSA P-256 verification, and replay protection.
/// </summary>
public sealed class PairingCoordinatorService : IPairingService
{
    private readonly IPairingSessionRepository _pairingRepo;
    private readonly ITrustedDeviceRepository _trustedRepo;
    private readonly IDeviceRepository _deviceRepo;
    private readonly IRefreshTokenRepository _refreshRepo;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly IDeviceTokenService _tokenService;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly INonceReplayProtector _replayProtector;
    private readonly IJsonSerializer _serializer;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly ILogger<PairingCoordinatorService> _logger;

    public PairingCoordinatorService(
        IPairingSessionRepository pairingRepo,
        ITrustedDeviceRepository trustedRepo,
        IDeviceRepository deviceRepo,
        IRefreshTokenRepository refreshRepo,
        ICryptographicIdentityProvider identityProvider,
        IDeviceTokenService tokenService,
        ITokenGenerator tokenGenerator,
        INonceReplayProtector replayProtector,
        IJsonSerializer serializer,
        IEventBus eventBus,
        IClock clock,
        ILogger<PairingCoordinatorService> logger)
    {
        _pairingRepo = pairingRepo;
        _trustedRepo = trustedRepo;
        _deviceRepo = deviceRepo;
        _refreshRepo = refreshRepo;
        _identityProvider = identityProvider;
        _tokenService = tokenService;
        _tokenGenerator = tokenGenerator;
        _replayProtector = replayProtector;
        _serializer = serializer;
        _eventBus = eventBus;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<PairingSessionSnapshot>> CreateSessionAsync(
        CreatePairingRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Validation(validation));
        }

        var replayCheck = _replayProtector.ValidateAndRecord("pairing.create", request.HostDeviceId, request.TimestampUtc, request.Nonce);
        if (replayCheck.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(replayCheck.Error!);
        }

        if (!VerifyFingerprintMatch(request.HostPublicKeySpkiBase64, request.HostPublicKeyFingerprint))
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Host public key fingerprint mismatch.", "HOST_FINGERPRINT_MISMATCH"));
        }

        var hostSigCheck = _identityProvider.VerifySignature(
            request.HostPublicKeySpkiBase64,
            PairingSignaturePayloads.ForCreatePairing(request.HostDeviceId, request.ServerEndpoint, request.TimestampUtc, request.Nonce),
            request.HostSignatureBase64);

        if (hostSigCheck.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(hostSigCheck.Error!);
        }

        var serverIdentity = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (serverIdentity.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(serverIdentity.Error!);
        }

        var now = _clock.UtcNow;
        var expires = now.AddSeconds(Math.Clamp(request.LifetimeSeconds, 15, 600));
        var pairingId = Guid.CreateVersion7();
        var challengeNonce = _tokenGenerator.GenerateToken(24);
        var oneTimeSecret = _tokenGenerator.GenerateToken(24);
        var secretHash = _tokenGenerator.HashToken(oneTimeSecret);

        var unsignedQr = new PairingQrPayload
        {
            PairingId = pairingId,
            HostDeviceId = request.HostDeviceId,
            HostDeviceName = request.HostDeviceName.Trim(),
            HostPlatform = request.HostPlatform,
            HostDeviceType = request.HostDeviceType,
            HostAppVersion = request.HostAppVersion,
            HostPublicKeySpkiBase64 = request.HostPublicKeySpkiBase64,
            HostPublicKeyFingerprint = request.HostPublicKeyFingerprint,
            ServerPublicKeySpkiBase64 = serverIdentity.Value.PublicKeySpkiBase64,
            ServerEndpoint = request.ServerEndpoint.Trim().TrimEnd('/'),
            TlsCertThumbprint = request.TlsCertThumbprint,
            ChallengeNonce = challengeNonce,
            OneTimeSecret = oneTimeSecret,
            IssuedAtUtc = now,
            ExpiresAtUtc = expires,
            HostSignatureBase64 = "pending"
        };

        var qrSig = await _identityProvider.SignStringAsync(
            PairingSignaturePayloads.ForQrPayload(unsignedQr),
            cancellationToken).ConfigureAwait(false);

        if (qrSig.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(qrSig.Error!);
        }

        var signedQr = unsignedQr with { HostSignatureBase64 = qrSig.Value };
        var qrJson = _serializer.Serialize(signedQr);

        var snapshot = new PairingSessionSnapshot
        {
            PairingId = pairingId,
            HostDeviceId = request.HostDeviceId,
            HostDeviceName = request.HostDeviceName.Trim(),
            HostPublicKeyFingerprint = request.HostPublicKeyFingerprint,
            State = PairingSessionState.AwaitingScan,
            QrPayloadJson = qrJson,
            IssuedAtUtc = now,
            ExpiresAtUtc = expires
        };

        var stored = new StoredPairingSession
        {
            Snapshot = snapshot,
            OneTimeSecretHash = secretHash,
            ChallengeNonce = challengeNonce
        };

        Result saveResult = _pairingRepo is SqlitePairingSessionRepository sqliteRepo
            ? await sqliteRepo.UpsertWithHostKeyAsync(stored, request.HostPublicKeySpkiBase64, cancellationToken).ConfigureAwait(false)
            : await _pairingRepo.UpsertAsync(stored, cancellationToken).ConfigureAwait(false);

        if (saveResult.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(saveResult.Error!);
        }

        await _eventBus.PublishAsync(new PairingStateChangedDomainEvent(pairingId, PairingSessionState.AwaitingScan, "QR gerado e aguardando leitura"), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Pairing session {PairingId} created by host {HostDeviceId}, expires at {ExpiresAt}", pairingId, request.HostDeviceId, expires);

        return Result<PairingSessionSnapshot>.Success(snapshot);
    }

    public async Task<Result<PairingSessionSnapshot>> GetSessionAsync(Guid pairingId, CancellationToken cancellationToken = default)
    {
        if (pairingId == Guid.Empty)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Validation("PairingId cannot be empty.", code: "PAIRING_ID_EMPTY"));
        }

        var found = await _pairingRepo.GetByIdAsync(pairingId, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(found.Error!);
        }

        if (found.Value is null)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.NotFound($"Pairing session '{pairingId}' not found.", "PAIRING_NOT_FOUND"));
        }

        var current = found.Value;
        if (current.Snapshot.State is PairingSessionState.AwaitingScan or PairingSessionState.Validating or PairingSessionState.AwaitingConfirmation
            && _clock.UtcNow > current.Snapshot.ExpiresAtUtc)
        {
            var expiredSnapshot = current.Snapshot with
            {
                State = PairingSessionState.Expired,
                FailureReason = "O código QR expirou."
            };
            await _pairingRepo.UpsertAsync(current with { Snapshot = expiredSnapshot }, cancellationToken).ConfigureAwait(false);
            return Result<PairingSessionSnapshot>.Success(expiredSnapshot);
        }

        return Result<PairingSessionSnapshot>.Success(current.Snapshot);
    }

    public async Task<Result<PairingSessionSnapshot>> SubmitClaimAsync(
        SubmitPairingClaimRequest claim,
        CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(claim);
        if (!validation.IsValid)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Validation(validation));
        }

        var replayCheck = _replayProtector.ValidateAndRecord("pairing.claim", claim.ClientDeviceId, claim.ClientTimestampUtc, claim.ClientNonce);
        if (replayCheck.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(replayCheck.Error!);
        }

        if (!VerifyFingerprintMatch(claim.ClientPublicKeySpkiBase64, claim.ClientPublicKeyFingerprint))
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Client public key fingerprint mismatch.", "CLIENT_FINGERPRINT_MISMATCH"));
        }

        var clientSig = _identityProvider.VerifySignature(
            claim.ClientPublicKeySpkiBase64,
            PairingSignaturePayloads.ForPairingClaim(claim),
            claim.ClientSignatureBase64);

        if (clientSig.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(clientSig.Error!);
        }

        var loaded = await _pairingRepo.GetByIdAsync(claim.PairingId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(loaded.Error!);
        }

        if (loaded.Value is null)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.NotFound("Pairing session not found.", "PAIRING_NOT_FOUND"));
        }

        var session = loaded.Value;
        var now = _clock.UtcNow;

        if (now > session.Snapshot.ExpiresAtUtc || session.Snapshot.State == PairingSessionState.Expired)
        {
            var expired = session.Snapshot with { State = PairingSessionState.Expired, FailureReason = "QR Code expirado." };
            await _pairingRepo.UpsertAsync(session with { Snapshot = expired }, cancellationToken).ConfigureAwait(false);
            await _eventBus.PublishAsync(new PairingStateChangedDomainEvent(claim.PairingId, PairingSessionState.Expired, "Expirado"), cancellationToken).ConfigureAwait(false);
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Pairing QR code has expired.", "PAIRING_EXPIRED"));
        }

        if (!string.Equals(session.ChallengeNonce, claim.ChallengeNonce, StringComparison.Ordinal))
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Pairing challenge nonce mismatch.", "PAIRING_CHALLENGE_MISMATCH"));
        }

        if (session.SecretConsumed || session.Snapshot.State != PairingSessionState.AwaitingScan)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Conflict("Pairing QR code has already been used.", "PAIRING_ALREADY_USED"));
        }

        var secretHash = _tokenGenerator.HashToken(claim.OneTimeSecret);
        var consumed = await _pairingRepo.TryConsumeSecretAsync(claim.PairingId, secretHash, cancellationToken).ConfigureAwait(false);
        if (consumed.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(consumed.Error!);
        }

        if (!consumed.Value)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Invalid or already consumed pairing secret.", "PAIRING_SECRET_INVALID"));
        }

        var verificationCode = _identityProvider.ComputeShortVerificationCode(
            session.Snapshot.HostPublicKeyFingerprint,
            claim.ClientPublicKeyFingerprint,
            session.ChallengeNonce);

        var updatedSnapshot = session.Snapshot with
        {
            CandidateDeviceId = claim.ClientDeviceId,
            CandidateDeviceName = claim.ClientDeviceName.Trim(),
            CandidatePlatform = claim.ClientPlatform,
            CandidateDeviceType = claim.ClientDeviceType,
            CandidateAppVersion = claim.ClientAppVersion,
            CandidatePublicKeyFingerprint = claim.ClientPublicKeyFingerprint,
            VerificationCode = verificationCode,
            State = PairingSessionState.AwaitingConfirmation
        };

        var updatedSession = session with
        {
            Snapshot = updatedSnapshot,
            CandidatePublicKeySpkiBase64 = claim.ClientPublicKeySpkiBase64,
            CandidateOperatingSystemVersion = claim.ClientOperatingSystemVersion,
            RequestedPermissions = claim.RequestedPermissions,
            SecretConsumed = true
        };

        var persisted = await _pairingRepo.UpsertAsync(updatedSession, cancellationToken).ConfigureAwait(false);
        if (persisted.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(persisted.Error!);
        }

        await _eventBus.PublishAsync(
            new PairingStateChangedDomainEvent(claim.PairingId, PairingSessionState.AwaitingConfirmation, $"Aguardando confirmação ({verificationCode})"),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Pairing session {PairingId} claimed by {ClientDeviceId}; awaiting confirmation ({Code})",
            claim.PairingId, claim.ClientDeviceId, verificationCode);

        return Result<PairingSessionSnapshot>.Success(updatedSnapshot);
    }

    public async Task<Result<PairingSessionSnapshot>> ConfirmAsync(
        ConfirmPairingRequest confirm,
        CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(confirm);
        if (!validation.IsValid)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Validation(validation));
        }

        var replayCheck = _replayProtector.ValidateAndRecord("pairing.confirm", confirm.HostDeviceId, confirm.TimestampUtc, confirm.Nonce);
        if (replayCheck.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(replayCheck.Error!);
        }

        var loaded = await _pairingRepo.GetByIdAsync(confirm.PairingId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(loaded.Error!);
        }

        if (loaded.Value is null)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.NotFound("Pairing session not found.", "PAIRING_NOT_FOUND"));
        }

        var session = loaded.Value;
        if (session.Snapshot.HostDeviceId != confirm.HostDeviceId)
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Only the initiating host can confirm pairing.", "PAIRING_HOST_MISMATCH"));
        }

        var hostSpki = _pairingRepo is SqlitePairingSessionRepository sqliteRepo
            ? (await sqliteRepo.GetHostPublicKeySpkiAsync(confirm.PairingId, cancellationToken).ConfigureAwait(false)).Value
            : null;

        if (string.IsNullOrWhiteSpace(hostSpki))
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Host public key unavailable for confirmation.", "HOST_KEY_MISSING"));
        }

        var sigCheck = _identityProvider.VerifySignature(
            hostSpki,
            PairingSignaturePayloads.ForConfirmPairing(
                confirm.PairingId,
                confirm.HostDeviceId,
                confirm.Approve,
                confirm.GrantedPermissions,
                confirm.TimestampUtc,
                confirm.Nonce),
            confirm.HostSignatureBase64);

        if (sigCheck.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(sigCheck.Error!);
        }

        var now = _clock.UtcNow;
        if (now > session.Snapshot.ExpiresAtUtc)
        {
            var expired = session.Snapshot with { State = PairingSessionState.Expired, FailureReason = "Sessão de pareamento expirada." };
            await _pairingRepo.UpsertAsync(session with { Snapshot = expired }, cancellationToken).ConfigureAwait(false);
            return Result<PairingSessionSnapshot>.Failure(NexusError.Unauthorized("Pairing session expired before confirmation.", "PAIRING_EXPIRED"));
        }

        if (session.Snapshot.State != PairingSessionState.AwaitingConfirmation ||
            session.Snapshot.CandidateDeviceId is null ||
            string.IsNullOrWhiteSpace(session.CandidatePublicKeySpkiBase64))
        {
            return Result<PairingSessionSnapshot>.Failure(NexusError.Conflict("Pairing session is not awaiting confirmation.", "PAIRING_STATE_INVALID"));
        }

        if (!confirm.Approve)
        {
            var rejected = session.Snapshot with
            {
                State = PairingSessionState.Rejected,
                FailureReason = string.IsNullOrWhiteSpace(confirm.RejectionReason) ? "Pareamento rejeitado pelo usuário." : confirm.RejectionReason,
                CompletedAtUtc = now
            };
            await _pairingRepo.UpsertAsync(session with { Snapshot = rejected }, cancellationToken).ConfigureAwait(false);
            await _eventBus.PublishAsync(new PairingStateChangedDomainEvent(confirm.PairingId, PairingSessionState.Rejected, rejected.FailureReason), cancellationToken).ConfigureAwait(false);
            return Result<PairingSessionSnapshot>.Success(rejected);
        }

        var candidateId = session.Snapshot.CandidateDeviceId.Value;
        var trusted = new TrustedDeviceRecord
        {
            DeviceId = candidateId,
            CustomName = session.Snapshot.CandidateDeviceName ?? "Dispositivo Pareado",
            Platform = session.Snapshot.CandidatePlatform ?? DevicePlatform.Android,
            DeviceType = session.Snapshot.CandidateDeviceType ?? DeviceType.Phone,
            OperatingSystemVersion = session.CandidateOperatingSystemVersion ?? "Unknown",
            AppVersion = session.Snapshot.CandidateAppVersion ?? "0.3.0",
            PublicKeySpkiBase64 = session.CandidatePublicKeySpkiBase64,
            PublicKeyFingerprint = session.Snapshot.CandidatePublicKeyFingerprint!,
            Permissions = confirm.GrantedPermissions,
            TrustStatus = DeviceTrustStatus.Authorized,
            PairedAtUtc = now,
            LastAccessAtUtc = now
        };

        var trustSave = await _trustedRepo.UpsertAsync(trusted, cancellationToken).ConfigureAwait(false);
        if (trustSave.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(trustSave.Error!);
        }

        await _deviceRepo.UpsertAsync(new DeviceInfo
        {
            Id = trusted.DeviceId,
            Name = trusted.CustomName,
            Platform = trusted.Platform,
            Type = trusted.DeviceType,
            OperatingSystemVersion = trusted.OperatingSystemVersion,
            AppVersion = trusted.AppVersion,
            HardwareFingerprint = trusted.PublicKeyFingerprint,
            Capabilities = DeviceCapability.Notifications | DeviceCapability.Clipboard | DeviceCapability.FileTransfer | DeviceCapability.Telemetry,
            Status = DeviceStatus.Online,
            RegisteredAtUtc = now,
            LastSeenAtUtc = now
        }, cancellationToken).ConfigureAwait(false);

        var issued = await _tokenService.IssueTokenPairAsync(candidateId, trusted.Permissions, now, cancellationToken).ConfigureAwait(false);
        if (issued.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(issued.Error!);
        }

        var refreshStore = await _refreshRepo.StoreAsync(new StoredRefreshTokenRecord(
            issued.Value.RefreshTokenHash,
            candidateId,
            now,
            issued.Value.TokenPair.RefreshTokenExpiresAtUtc,
            IsRevoked: false,
            ReplacedByHash: null), cancellationToken).ConfigureAwait(false);

        if (refreshStore.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(refreshStore.Error!);
        }

        var connectedSnapshot = session.Snapshot with
        {
            State = PairingSessionState.Connected,
            CompletedAtUtc = now
        };

        var updatedSession = session with
        {
            Snapshot = connectedSnapshot,
            GrantedPermissions = trusted.Permissions,
            PendingAccessToken = issued.Value.TokenPair.AccessToken,
            PendingRefreshToken = issued.Value.TokenPair.RefreshToken,
            PendingAccessExpiresAtUtc = issued.Value.TokenPair.AccessTokenExpiresAtUtc,
            PendingRefreshExpiresAtUtc = issued.Value.TokenPair.RefreshTokenExpiresAtUtc
        };

        await _pairingRepo.UpsertAsync(updatedSession, cancellationToken).ConfigureAwait(false);
        await _eventBus.PublishAsync(new PairingStateChangedDomainEvent(confirm.PairingId, PairingSessionState.Connected, "Dispositivo pareado e autorizado"), cancellationToken).ConfigureAwait(false);
        await _eventBus.PublishAsync(new DeviceTrustAuthorizedDomainEvent(trusted.DeviceId, trusted.CustomName, trusted.PublicKeyFingerprint), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Pairing session {PairingId} completed: trusted device {DeviceId} ({Name})", confirm.PairingId, trusted.DeviceId, trusted.CustomName);
        return Result<PairingSessionSnapshot>.Success(connectedSnapshot);
    }

    public async Task<Result<DeviceAuthTokenPair>> FinalizeTokensAsync(
        FinalizePairingTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Validation(validation));
        }

        var replayCheck = _replayProtector.ValidateAndRecord("pairing.finalize", request.ClientDeviceId, request.TimestampUtc, request.Nonce);
        if (replayCheck.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(replayCheck.Error!);
        }

        var loaded = await _pairingRepo.GetByIdAsync(request.PairingId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(loaded.Error!);
        }

        if (loaded.Value is null)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.NotFound("Pairing session not found.", "PAIRING_NOT_FOUND"));
        }

        var session = loaded.Value;
        if (session.Snapshot.State != PairingSessionState.Connected || session.Snapshot.CandidateDeviceId != request.ClientDeviceId)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Pairing session is not authorized for this device.", "PAIRING_NOT_AUTHORIZED"));
        }

        if (string.IsNullOrWhiteSpace(session.CandidatePublicKeySpkiBase64))
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Candidate public key missing.", "CLIENT_KEY_MISSING"));
        }

        var sigCheck = _identityProvider.VerifySignature(
            session.CandidatePublicKeySpkiBase64,
            PairingSignaturePayloads.ForFinalizeTokens(request.PairingId, request.ClientDeviceId, request.TimestampUtc, request.Nonce),
            request.ClientSignatureBase64);

        if (sigCheck.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(sigCheck.Error!);
        }

        if (session.TokensFinalized || string.IsNullOrWhiteSpace(session.PendingAccessToken) || string.IsNullOrWhiteSpace(session.PendingRefreshToken))
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Conflict("Pairing credentials have already been collected.", "CREDENTIALS_ALREADY_COLLECTED"));
        }

        var finalized = await _pairingRepo.TryMarkTokensFinalizedAsync(request.PairingId, cancellationToken).ConfigureAwait(false);
        if (finalized.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(finalized.Error!);
        }

        if (!finalized.Value)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Conflict("Pairing credentials were already collected.", "CREDENTIALS_ALREADY_COLLECTED"));
        }

        return Result<DeviceAuthTokenPair>.Success(new DeviceAuthTokenPair
        {
            DeviceId = request.ClientDeviceId,
            AccessToken = session.PendingAccessToken,
            RefreshToken = session.PendingRefreshToken,
            AccessTokenExpiresAtUtc = session.PendingAccessExpiresAtUtc ?? _clock.UtcNow.AddMinutes(15),
            RefreshTokenExpiresAtUtc = session.PendingRefreshExpiresAtUtc ?? _clock.UtcNow.AddDays(30),
            Permissions = session.GrantedPermissions
        });
    }

    private static bool VerifyFingerprintMatch(string spkiBase64, string expectedFingerprint)
    {
        try
        {
            var actual = CryptographicIdentityProvider.ComputePublicKeyFingerprint(Convert.FromBase64String(spkiBase64.Trim()));
            return string.Equals(actual, expectedFingerprint.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class TrustedDeviceManagementService : ITrustedDeviceService
{
    private readonly ITrustedDeviceRepository _trustedRepo;
    private readonly IRefreshTokenRepository _refreshRepo;
    private readonly IDeviceRepository _deviceRepo;
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly IDeviceTokenService _tokenService;
    private readonly INonceReplayProtector _replayProtector;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly ILogger<TrustedDeviceManagementService> _logger;

    public TrustedDeviceManagementService(
        ITrustedDeviceRepository trustedRepo,
        IRefreshTokenRepository refreshRepo,
        IDeviceRepository deviceRepo,
        ICryptographicIdentityProvider identityProvider,
        IDeviceTokenService tokenService,
        INonceReplayProtector replayProtector,
        IEventBus eventBus,
        IClock clock,
        ILogger<TrustedDeviceManagementService> logger)
    {
        _trustedRepo = trustedRepo;
        _refreshRepo = refreshRepo;
        _deviceRepo = deviceRepo;
        _identityProvider = identityProvider;
        _tokenService = tokenService;
        _replayProtector = replayProtector;
        _eventBus = eventBus;
        _clock = clock;
        _logger = logger;
    }

    public Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAllAsync(CancellationToken cancellationToken = default)
        => _trustedRepo.ListAsync(cancellationToken);

    public Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListAuthorizedAsync(CancellationToken cancellationToken = default)
        => _trustedRepo.ListAuthorizedAsync(cancellationToken);

    public async Task<Result<TrustedDeviceRecord>> GetByIdAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var found = await _trustedRepo.GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return Result<TrustedDeviceRecord>.Failure(found.Error!);
        }

        return found.Value is null
            ? Result<TrustedDeviceRecord>.Failure(NexusError.NotFound($"Trusted device '{deviceId}' not found.", "TRUSTED_DEVICE_NOT_FOUND"))
            : Result<TrustedDeviceRecord>.Success(found.Value);
    }

    public async Task<Result<TrustedDeviceRecord>> RenameAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim().Length < 2)
        {
            return Result<TrustedDeviceRecord>.Failure(NexusError.Validation("Device name must have at least 2 characters.", code: "DEVICE_NAME_INVALID"));
        }

        var existing = await GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return existing;
        }

        var renamed = await _trustedRepo.RenameAsync(deviceId, newName.Trim(), cancellationToken).ConfigureAwait(false);
        if (renamed.IsFailure)
        {
            return Result<TrustedDeviceRecord>.Failure(renamed.Error!);
        }

        var updated = existing.Value with { CustomName = newName.Trim() };
        var dev = await _deviceRepo.GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (dev.IsSuccess && dev.Value is not null)
        {
            await _deviceRepo.UpsertAsync(dev.Value with { Name = newName.Trim() }, cancellationToken).ConfigureAwait(false);
        }

        await _eventBus.PublishAsync(new DeviceRenamedDomainEvent(deviceId, newName.Trim()), cancellationToken).ConfigureAwait(false);
        return Result<TrustedDeviceRecord>.Success(updated);
    }

    public async Task<Result<TrustedDeviceRecord>> UpdatePermissionsAsync(Guid deviceId, NexusPermission permissions, CancellationToken cancellationToken = default)
    {
        var existing = await GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return existing;
        }

        if (existing.Value.TrustStatus == DeviceTrustStatus.Revoked)
        {
            return Result<TrustedDeviceRecord>.Failure(NexusError.Unauthorized("Cannot update permissions on a revoked device.", "DEVICE_REVOKED"));
        }

        var updated = await _trustedRepo.UpdatePermissionsAsync(deviceId, permissions, cancellationToken).ConfigureAwait(false);
        return updated.IsFailure
            ? Result<TrustedDeviceRecord>.Failure(updated.Error!)
            : Result<TrustedDeviceRecord>.Success(existing.Value with { Permissions = permissions });
    }

    public async Task<Result> RevokeAsync(Guid deviceId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var existing = await GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result.Failure(existing.Error!);
        }

        var now = _clock.UtcNow;
        var revokeResult = await _trustedRepo.RevokeAsync(deviceId, now, reason ?? "Revogado pelo administrador", cancellationToken).ConfigureAwait(false);
        if (revokeResult.IsFailure)
        {
            return revokeResult;
        }

        await _refreshRepo.RevokeByDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await _deviceRepo.UpdateStatusAsync(deviceId, DeviceStatus.Offline, now, cancellationToken).ConfigureAwait(false);
        await _eventBus.PublishAsync(new DeviceTrustRevokedDomainEvent(deviceId, reason), cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("Trusted device {DeviceId} revoked: {Reason}", deviceId, reason ?? "Manual revocation");
        return Result.Success();
    }

    public async Task<Result<bool>> RemoveAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await _refreshRepo.RevokeByDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await _deviceRepo.DeleteAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return await _trustedRepo.DeleteAsync(deviceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DeviceAuthTokenPair>> RefreshTokensAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Validation(validation));
        }

        var replayCheck = _replayProtector.ValidateAndRecord("auth.refresh", request.DeviceId, request.TimestampUtc, request.Nonce);
        if (replayCheck.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(replayCheck.Error!);
        }

        var deviceResult = await GetByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (deviceResult.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Device is not recognized.", "DEVICE_NOT_TRUSTED"));
        }

        var device = deviceResult.Value;
        if (device.TrustStatus != DeviceTrustStatus.Authorized)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Device trust has been revoked.", "DEVICE_REVOKED"));
        }

        var sigCheck = _identityProvider.VerifySignature(
            device.PublicKeySpkiBase64,
            PairingSignaturePayloads.ForRefreshToken(request.DeviceId, request.RefreshToken, request.TimestampUtc, request.Nonce),
            request.SignatureBase64);

        if (sigCheck.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(sigCheck.Error!);
        }

        var tokenHash = _tokenService.HashRefreshToken(request.RefreshToken);
        var storedToken = await _refreshRepo.GetByTokenHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);
        if (storedToken.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(storedToken.Error!);
        }

        if (storedToken.Value is null || storedToken.Value.DeviceId != request.DeviceId)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Refresh token is invalid.", "REFRESH_TOKEN_INVALID"));
        }

        if (storedToken.Value.IsRevoked)
        {
            // Refresh token reuse detected — revoke device trust and all refresh tokens defensively
            await RevokeAsync(request.DeviceId, "Reutilização de credencial (refresh token) detectada.", cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Refresh token reuse detected for device {DeviceId}; trust and token family revoked.", request.DeviceId);
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Refresh token reuse detected.", "REFRESH_TOKEN_REUSED"));
        }

        var now = _clock.UtcNow;
        if (now >= storedToken.Value.ExpiresAtUtc)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Refresh token has expired.", "REFRESH_TOKEN_EXPIRED"));
        }

        var issued = await _tokenService.IssueTokenPairAsync(device.DeviceId, device.Permissions, now, cancellationToken).ConfigureAwait(false);
        if (issued.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(issued.Error!);
        }

        await _refreshRepo.MarkRotatedAsync(tokenHash, issued.Value.RefreshTokenHash, cancellationToken).ConfigureAwait(false);
        await _refreshRepo.StoreAsync(new StoredRefreshTokenRecord(
            issued.Value.RefreshTokenHash,
            device.DeviceId,
            now,
            issued.Value.TokenPair.RefreshTokenExpiresAtUtc,
            IsRevoked: false,
            ReplacedByHash: null), cancellationToken).ConfigureAwait(false);

        await _trustedRepo.UpdateLastAccessAsync(device.DeviceId, now, cancellationToken).ConfigureAwait(false);
        return Result<DeviceAuthTokenPair>.Success(issued.Value.TokenPair);
    }
}

