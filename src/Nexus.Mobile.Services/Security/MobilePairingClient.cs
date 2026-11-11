using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Pairing;
using Nexus.Core.Storage;
using Nexus.Infrastructure.Security;
using Nexus.Mobile.Services.Settings;
using Nexus.Security.Credentials;

namespace Nexus.Mobile.Services.Security;

public sealed record MobilePairedSessionEnvelope(
    Guid HostDeviceId,
    string HostDeviceName,
    string HostFingerprint,
    string ServerEndpoint,
    string? TlsThumbprint,
    DeviceAuthTokenPair Tokens);

public sealed record MobileClaimOutcome(
    PairingQrPayload QrPayload,
    PairingSessionSnapshot Snapshot,
    string VerificationCode);

public interface IMobilePairingClient
{
    Task<Result<MobilePairedSessionEnvelope?>> GetStoredSessionAsync(CancellationToken cancellationToken = default);
    Task<Result<MobileClaimOutcome>> SubmitQrPairingAsync(string rawQrJson, NexusPermission requestedPermissions = NexusPermission.ViewStatus | NexusPermission.Notifications | NexusPermission.Clipboard | NexusPermission.FileTransfer, CancellationToken cancellationToken = default);
    Task<Result<(PairingSessionSnapshot Snapshot, DeviceAuthTokenPair? Tokens)>> CheckAndFinalizeAsync(PairingQrPayload qr, CancellationToken cancellationToken = default);
    Task<Result<DeviceAuthTokenPair>> RefreshStoredTokensAsync(CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListTrustedDevicesAsync(CancellationToken cancellationToken = default);
    Task<Result<TrustedDeviceRecord>> RenameTrustedDeviceAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default);
    Task<Result> RevokeTrustedDeviceAsync(Guid deviceId, string? reason = null, CancellationToken cancellationToken = default);
    Task<Result<bool>> RemoveTrustedDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

public sealed class MobilePairingClient : IMobilePairingClient
{
    private const string VaultSessionKey = "mobile.paired.session.v1";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly IMobileSettingsService _settingsService;
    private readonly IJsonSerializer _serializer;
    private readonly IClock _clock;
    private readonly IEventBus _eventBus;
    private readonly ILogger<MobilePairingClient> _logger;

    public MobilePairingClient(
        IServiceScopeFactory scopeFactory,
        NexusPairingProtocolClient protocolClient,
        ISecureCredentialStore credentialStore,
        IMobileSettingsService settingsService,
        IJsonSerializer serializer,
        IClock clock,
        IEventBus eventBus,
        ILogger<MobilePairingClient> logger)
    {
        _scopeFactory = scopeFactory;
        _protocolClient = protocolClient;
        _credentialStore = credentialStore;
        _settingsService = settingsService;
        _serializer = serializer;
        _clock = clock;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<Result<MobilePairedSessionEnvelope?>> GetStoredSessionAsync(CancellationToken cancellationToken = default)
    {
        var raw = await _credentialStore.GetStringAsync(VaultSessionKey, cancellationToken).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Result<MobilePairedSessionEnvelope?>.Failure(raw.Error!);
        }

        if (string.IsNullOrWhiteSpace(raw.Value))
        {
            return Result<MobilePairedSessionEnvelope?>.Success(null);
        }

        var parsed = _serializer.Deserialize<MobilePairedSessionEnvelope>(raw.Value);
        return Result<MobilePairedSessionEnvelope?>.Success(parsed);
    }

    public async Task<Result<MobileClaimOutcome>> SubmitQrPairingAsync(
        string rawQrJson,
        NexusPermission requestedPermissions = NexusPermission.ViewStatus | NexusPermission.Notifications | NexusPermission.Clipboard | NexusPermission.FileTransfer,
        CancellationToken cancellationToken = default)
    {
        var parsed = _protocolClient.ParseAndVerifyQrPayload(rawQrJson);
        if (parsed.IsFailure)
        {
            return Result<MobileClaimOutcome>.Failure(parsed.Error!);
        }

        var qr = parsed.Value;
        var claimBuild = await _protocolClient.BuildClaimRequestAsync(qr, requestedPermissions, cancellationToken).ConfigureAwait(false);
        if (claimBuild.IsFailure)
        {
            return Result<MobileClaimOutcome>.Failure(claimBuild.Error!);
        }

        using var client = _protocolClient.CreateHttpClient(qr.ServerEndpoint, qr.TlsCertThumbprint);
        var claimResult = await _protocolClient.SendAsync<PairingSessionSnapshot>(
            client,
            HttpMethod.Post,
            "api/v1/pairing/claim",
            claimBuild.Value.Request,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (claimResult.IsFailure)
        {
            return Result<MobileClaimOutcome>.Failure(claimResult.Error!);
        }

        return Result<MobileClaimOutcome>.Success(new MobileClaimOutcome(
            qr,
            claimResult.Value,
            claimBuild.Value.VerificationCode));
    }

    public async Task<Result<(PairingSessionSnapshot Snapshot, DeviceAuthTokenPair? Tokens)>> CheckAndFinalizeAsync(
        PairingQrPayload qr,
        CancellationToken cancellationToken = default)
    {
        using var client = _protocolClient.CreateHttpClient(qr.ServerEndpoint, qr.TlsCertThumbprint);
        var pollResult = await _protocolClient.SendAsync<PairingSessionSnapshot>(
            client,
            HttpMethod.Get,
            $"api/v1/pairing/sessions/{qr.PairingId:D}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (pollResult.IsFailure)
        {
            return Result<(PairingSessionSnapshot, DeviceAuthTokenPair?)>.Failure(pollResult.Error!);
        }

        var snapshot = pollResult.Value;
        if (snapshot.State != PairingSessionState.Connected)
        {
            return Result<(PairingSessionSnapshot, DeviceAuthTokenPair?)>.Success((snapshot, null));
        }

        var finalizeReq = await _protocolClient.BuildFinalizeRequestAsync(qr.PairingId, cancellationToken).ConfigureAwait(false);
        if (finalizeReq.IsFailure)
        {
            return Result<(PairingSessionSnapshot, DeviceAuthTokenPair?)>.Failure(finalizeReq.Error!);
        }

        var tokensResult = await _protocolClient.SendAsync<DeviceAuthTokenPair>(
            client,
            HttpMethod.Post,
            $"api/v1/pairing/sessions/{qr.PairingId:D}/finalize",
            finalizeReq.Value,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (tokensResult.IsFailure)
        {
            return Result<(PairingSessionSnapshot, DeviceAuthTokenPair?)>.Failure(tokensResult.Error!);
        }

        await PersistCompletedPairingAsync(qr, tokensResult.Value, cancellationToken).ConfigureAwait(false);
        return Result<(PairingSessionSnapshot, DeviceAuthTokenPair?)>.Success((snapshot, tokensResult.Value));
    }

    public async Task<Result<DeviceAuthTokenPair>> RefreshStoredTokensAsync(CancellationToken cancellationToken = default)
    {
        var sessionRes = await GetStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        if (sessionRes.IsFailure || sessionRes.Value is null)
        {
            return Result<DeviceAuthTokenPair>.Failure(NexusError.Unauthorized("Nenhuma credencial de pareamento salva no dispositivo.", "NO_STORED_TOKENS"));
        }

        var env = sessionRes.Value;
        var req = await _protocolClient.BuildRefreshRequestAsync(env.Tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (req.IsFailure)
        {
            return Result<DeviceAuthTokenPair>.Failure(req.Error!);
        }

        using var client = _protocolClient.CreateHttpClient(env.ServerEndpoint, env.TlsThumbprint);
        var refreshed = await _protocolClient.SendAsync<DeviceAuthTokenPair>(
            client,
            HttpMethod.Post,
            "api/v1/auth/refresh",
            req.Value,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (refreshed.IsFailure)
        {
            if (refreshed.Error!.Category is ErrorCategory.Unauthorized or ErrorCategory.Forbidden)
            {
                await _credentialStore.DeleteSecretAsync(VaultSessionKey, cancellationToken).ConfigureAwait(false);
            }

            return refreshed;
        }

        var updatedEnvelope = env with { Tokens = refreshed.Value };
        await _credentialStore.SaveStringAsync(VaultSessionKey, _serializer.Serialize(updatedEnvelope), cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    public async Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        var remote = await TryCallAuthenticatedServerAsync<IReadOnlyList<TrustedDeviceRecord>>(
            HttpMethod.Get,
            "api/v1/trusted-devices",
            body: null,
            cancellationToken).ConfigureAwait(false);

        if (remote.IsSuccess)
        {
            using var scopeSync = _scopeFactory.CreateScope();
            var trustedRepo = scopeSync.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
            foreach (var item in remote.Value)
            {
                await trustedRepo.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        return await trustedService.ListAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<TrustedDeviceRecord>> RenameTrustedDeviceAsync(
        Guid deviceId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        await TryCallAuthenticatedServerAsync<TrustedDeviceRecord>(
            HttpMethod.Patch,
            $"api/v1/trusted-devices/{deviceId:D}/name",
            new RenameDeviceRequest { CustomName = newName },
            cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        return await trustedService.RenameAsync(deviceId, newName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> RevokeTrustedDeviceAsync(
        Guid deviceId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await TryCallAuthenticatedServerAsync<bool>(
            HttpMethod.Post,
            $"api/v1/trusted-devices/{deviceId:D}/revoke",
            new RevokeDeviceRequest { Reason = reason },
            cancellationToken).ConfigureAwait(false);

        var sessionRes = await GetStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        if (sessionRes.IsSuccess && sessionRes.Value is not null && sessionRes.Value.HostDeviceId == deviceId)
        {
            await _credentialStore.DeleteSecretAsync(VaultSessionKey, cancellationToken).ConfigureAwait(false);
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        return await trustedService.RevokeAsync(deviceId, reason, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool>> RemoveTrustedDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await TryCallAuthenticatedServerAsync<bool>(
            HttpMethod.Delete,
            $"api/v1/trusted-devices/{deviceId:D}",
            body: null,
            cancellationToken).ConfigureAwait(false);

        var sessionRes = await GetStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        if (sessionRes.IsSuccess && sessionRes.Value is not null && sessionRes.Value.HostDeviceId == deviceId)
        {
            await _credentialStore.DeleteSecretAsync(VaultSessionKey, cancellationToken).ConfigureAwait(false);
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        return await trustedService.RemoveAsync(deviceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistCompletedPairingAsync(
        PairingQrPayload qr,
        DeviceAuthTokenPair tokens,
        CancellationToken cancellationToken)
    {
        var envelope = new MobilePairedSessionEnvelope(
            qr.HostDeviceId,
            qr.HostDeviceName,
            qr.HostPublicKeyFingerprint,
            qr.ServerEndpoint,
            qr.TlsCertThumbprint,
            tokens);

        await _credentialStore.SaveStringAsync(VaultSessionKey, _serializer.Serialize(envelope), cancellationToken).ConfigureAwait(false);

        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings.IsSuccess)
        {
            await _settingsService.SaveAsync(settings.Value with { ServerEndpoint = qr.ServerEndpoint }, cancellationToken).ConfigureAwait(false);
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

        var now = _clock.UtcNow;
        var hostRecord = new TrustedDeviceRecord
        {
            DeviceId = qr.HostDeviceId,
            CustomName = qr.HostDeviceName,
            Platform = qr.HostPlatform,
            DeviceType = qr.HostDeviceType,
            OperatingSystemVersion = "Windows Host",
            AppVersion = qr.HostAppVersion,
            PublicKeySpkiBase64 = qr.HostPublicKeySpkiBase64,
            PublicKeyFingerprint = qr.HostPublicKeyFingerprint,
            Permissions = tokens.Permissions,
            TrustStatus = DeviceTrustStatus.Authorized,
            PairedAtUtc = now,
            LastAccessAtUtc = now
        };

        await trustedRepo.UpsertAsync(hostRecord, cancellationToken).ConfigureAwait(false);
        await deviceRepo.UpsertAsync(new DeviceInfo
        {
            Id = hostRecord.DeviceId,
            Name = hostRecord.CustomName,
            Platform = hostRecord.Platform,
            Type = hostRecord.DeviceType,
            OperatingSystemVersion = hostRecord.OperatingSystemVersion,
            AppVersion = hostRecord.AppVersion,
            HardwareFingerprint = hostRecord.PublicKeyFingerprint,
            Capabilities = DeviceCapability.Notifications | DeviceCapability.Clipboard | DeviceCapability.FileTransfer | DeviceCapability.Telemetry,
            Status = DeviceStatus.Online,
            RegisteredAtUtc = now,
            LastSeenAtUtc = now
        }, cancellationToken).ConfigureAwait(false);

        await _eventBus.PublishAsync(
            new DeviceTrustAuthorizedDomainEvent(hostRecord.DeviceId, hostRecord.CustomName, hostRecord.PublicKeyFingerprint),
            cancellationToken).ConfigureAwait(false);

        var orchestrator = scope.ServiceProvider.GetService<Nexus.Core.Connections.INexusRouteOrchestrator>();
        if (orchestrator is not null)
        {
            await orchestrator.RegisterEndpointAsync(new Nexus.Contracts.Connections.RegisterEndpointRequest
            {
                DeviceId = qr.HostDeviceId,
                DeviceName = qr.HostDeviceName,
                Label = $"Pareado QR ({qr.HostDeviceName})",
                RouteKind = Nexus.Contracts.Connections.ConnectionRouteKind.Unknown,
                EndpointUrl = qr.ServerEndpoint,
                ExpectedTlsThumbprint = qr.TlsCertThumbprint,
                ExpectedHostKeyFingerprint = qr.HostPublicKeyFingerprint,
                Origin = Nexus.Contracts.Connections.EndpointOrigin.PairingQr
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Result<T>> TryCallAuthenticatedServerAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        var sessionRes = await GetStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        if (sessionRes.IsFailure || sessionRes.Value is null)
        {
            return Result<T>.Failure(NexusError.Unauthorized("Dispositivo ainda não pareado.", "NOT_PAIRED"));
        }

        var session = sessionRes.Value;
        var token = session.Tokens.AccessToken;

        if (_clock.UtcNow.AddMinutes(1) >= session.Tokens.AccessTokenExpiresAtUtc)
        {
            var rotated = await RefreshStoredTokensAsync(cancellationToken).ConfigureAwait(false);
            if (rotated.IsFailure)
            {
                return Result<T>.Failure(rotated.Error!);
            }

            token = rotated.Value.AccessToken;
        }

        try
        {
            using var client = _protocolClient.CreateHttpClient(session.ServerEndpoint, session.TlsThumbprint);
            return await _protocolClient.SendAsync<T>(client, method, relativePath, body, token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Mobile authenticated request {Path} failed", relativePath);
            return Result<T>.Failure(NexusError.Network(ex.Message));
        }
    }
}
