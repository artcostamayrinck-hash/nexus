using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Pairing;
using Nexus.Core.Storage;
using Nexus.Desktop.Services.Settings;
using Nexus.Infrastructure.Security;
using Nexus.Security.Pairing;

namespace Nexus.Desktop.Services.Security;

public sealed record StartedPairingOutput(
    PairingSessionSnapshot Snapshot,
    byte[] QrPngBytes,
    bool IsRemoteServer,
    string EndpointUsed,
    string? TlsThumbprint);

public interface IDesktopPairingClient
{
    Task<Result<StartedPairingOutput>> StartPairingAsync(int lifetimeSeconds = 120, CancellationToken cancellationToken = default);
    Task<Result<PairingSessionSnapshot>> GetSessionAsync(Guid pairingId, bool isRemoteServer, string endpoint, string? tlsThumbprint, CancellationToken cancellationToken = default);
    Task<Result<PairingSessionSnapshot>> ConfirmPairingAsync(Guid pairingId, bool approve, NexusPermission permissions, bool isRemoteServer, string endpoint, string? tlsThumbprint, string? rejectionReason = null, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListTrustedDevicesAsync(CancellationToken cancellationToken = default);
    Task<Result<TrustedDeviceRecord>> RenameTrustedDeviceAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default);
    Task<Result<TrustedDeviceRecord>> UpdatePermissionsAsync(Guid deviceId, NexusPermission permissions, CancellationToken cancellationToken = default);
    Task<Result> RevokeTrustedDeviceAsync(Guid deviceId, string? reason = null, CancellationToken cancellationToken = default);
    Task<Result<bool>> RemoveTrustedDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

public sealed class DesktopPairingClient : IDesktopPairingClient
{
    private sealed record ServerInfoLite(string? TlsThumbprint, string? HostKeyFingerprint);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly IQrCodeService _qrCodeService;
    private readonly IDesktopSettingsService _settingsService;
    private readonly ILocalHostTokenBridge _hostBridge;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DesktopPairingClient> _logger;

    public DesktopPairingClient(
        IServiceScopeFactory scopeFactory,
        NexusPairingProtocolClient protocolClient,
        IQrCodeService qrCodeService,
        IDesktopSettingsService settingsService,
        ILocalHostTokenBridge hostBridge,
        IEventBus eventBus,
        ILogger<DesktopPairingClient> logger)
    {
        _scopeFactory = scopeFactory;
        _protocolClient = protocolClient;
        _qrCodeService = qrCodeService;
        _settingsService = settingsService;
        _hostBridge = hostBridge;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<Result<StartedPairingOutput>> StartPairingAsync(
        int lifetimeSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        var settingsResult = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = settingsResult.IsSuccess
            ? settingsResult.Value.ServerEndpoint.Trim().TrimEnd('/')
            : "http://127.0.0.1:5280";

        var bridge = await TryReadBridgeAsync(cancellationToken).ConfigureAwait(false);
        var thumbprint = bridge?.TlsThumbprint;

        var discoveredThumbprint = await TryFetchServerThumbprintAsync(endpoint, thumbprint, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(discoveredThumbprint.Thumbprint))
        {
            thumbprint = discoveredThumbprint.Thumbprint;
        }

        var createReq = await _protocolClient.BuildCreatePairingRequestAsync(
            endpoint,
            thumbprint,
            lifetimeSeconds,
            cancellationToken).ConfigureAwait(false);

        if (createReq.IsFailure)
        {
            return Result<StartedPairingOutput>.Failure(createReq.Error!);
        }

        if (discoveredThumbprint.ServerReachable)
        {
            using var client = _protocolClient.CreateHttpClient(endpoint, thumbprint);
            var remote = await _protocolClient.SendAsync<PairingSessionSnapshot>(
                client,
                HttpMethod.Post,
                "api/v1/pairing/sessions",
                createReq.Value,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (remote.IsSuccess && !string.IsNullOrWhiteSpace(remote.Value.QrPayloadJson))
            {
                var png = _qrCodeService.GeneratePng(remote.Value.QrPayloadJson, 10);
                return Result<StartedPairingOutput>.Success(new StartedPairingOutput(
                    remote.Value,
                    png,
                    IsRemoteServer: true,
                    EndpointUsed: endpoint,
                    TlsThumbprint: thumbprint));
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var localPairing = scope.ServiceProvider.GetRequiredService<IPairingService>();
        var localResult = await localPairing.CreateSessionAsync(createReq.Value, cancellationToken).ConfigureAwait(false);
        if (localResult.IsFailure)
        {
            return Result<StartedPairingOutput>.Failure(localResult.Error!);
        }

        var qrBytes = _qrCodeService.GeneratePng(localResult.Value.QrPayloadJson ?? string.Empty, 10);
        return Result<StartedPairingOutput>.Success(new StartedPairingOutput(
            localResult.Value,
            qrBytes,
            IsRemoteServer: false,
            EndpointUsed: endpoint,
            TlsThumbprint: thumbprint));
    }

    public async Task<Result<PairingSessionSnapshot>> GetSessionAsync(
        Guid pairingId,
        bool isRemoteServer,
        string endpoint,
        string? tlsThumbprint,
        CancellationToken cancellationToken = default)
    {
        if (isRemoteServer)
        {
            using var client = _protocolClient.CreateHttpClient(endpoint, tlsThumbprint);
            return await _protocolClient.SendAsync<PairingSessionSnapshot>(
                client,
                HttpMethod.Get,
                $"api/v1/pairing/sessions/{pairingId:D}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        using var scope = _scopeFactory.CreateScope();
        var localPairing = scope.ServiceProvider.GetRequiredService<IPairingService>();
        return await localPairing.GetSessionAsync(pairingId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PairingSessionSnapshot>> ConfirmPairingAsync(
        Guid pairingId,
        bool approve,
        NexusPermission permissions,
        bool isRemoteServer,
        string endpoint,
        string? tlsThumbprint,
        string? rejectionReason = null,
        CancellationToken cancellationToken = default)
    {
        var reqResult = await _protocolClient.BuildConfirmRequestAsync(
            pairingId,
            approve,
            permissions,
            rejectionReason,
            cancellationToken).ConfigureAwait(false);

        if (reqResult.IsFailure)
        {
            return Result<PairingSessionSnapshot>.Failure(reqResult.Error!);
        }

        Result<PairingSessionSnapshot> confirmResult;
        if (isRemoteServer)
        {
            using var client = _protocolClient.CreateHttpClient(endpoint, tlsThumbprint);
            confirmResult = await _protocolClient.SendAsync<PairingSessionSnapshot>(
                client,
                HttpMethod.Post,
                $"api/v1/pairing/sessions/{pairingId:D}/confirm",
                reqResult.Value,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (confirmResult.IsSuccess && confirmResult.Value.State == PairingSessionState.Connected)
            {
                await MirrorConfirmedCandidateLocallyAsync(confirmResult.Value, permissions, endpoint, tlsThumbprint, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            using var scope = _scopeFactory.CreateScope();
            var localPairing = scope.ServiceProvider.GetRequiredService<IPairingService>();
            confirmResult = await localPairing.ConfirmAsync(reqResult.Value, cancellationToken).ConfigureAwait(false);
        }

        return confirmResult;
    }

    public async Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        await TrySyncRemoteDevicesToLocalAsync(cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        return await trustedService.ListAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<TrustedDeviceRecord>> RenameTrustedDeviceAsync(
        Guid deviceId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        await TryExecuteRemoteAdminAsync<TrustedDeviceRecord>(
            HttpMethod.Patch,
            $"api/v1/trusted-devices/{deviceId:D}/name",
            new RenameDeviceRequest { CustomName = newName },
            cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        return await trustedService.RenameAsync(deviceId, newName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<TrustedDeviceRecord>> UpdatePermissionsAsync(
        Guid deviceId,
        NexusPermission permissions,
        CancellationToken cancellationToken = default)
    {
        await TryExecuteRemoteAdminAsync<TrustedDeviceRecord>(
            HttpMethod.Patch,
            $"api/v1/trusted-devices/{deviceId:D}/permissions",
            new UpdateDevicePermissionsRequest { Permissions = permissions },
            cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        return await trustedService.UpdatePermissionsAsync(deviceId, permissions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> RevokeTrustedDeviceAsync(
        Guid deviceId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var remote = await TryExecuteRemoteAdminAsync<bool>(
            HttpMethod.Post,
            $"api/v1/trusted-devices/{deviceId:D}/revoke",
            new RevokeDeviceRequest { Reason = reason },
            cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        var local = await trustedService.RevokeAsync(deviceId, reason, cancellationToken).ConfigureAwait(false);

        if (local.IsFailure && remote.IsSuccess)
        {
            await _eventBus.PublishAsync(new DeviceTrustRevokedDomainEvent(deviceId, reason), cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }

        return local;
    }

    public async Task<Result<bool>> RemoveTrustedDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var remote = await TryExecuteRemoteAdminAsync<bool>(
            HttpMethod.Delete,
            $"api/v1/trusted-devices/{deviceId:D}",
            body: null,
            cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var trustedService = scope.ServiceProvider.GetRequiredService<ITrustedDeviceService>();
        var local = await trustedService.RemoveAsync(deviceId, cancellationToken).ConfigureAwait(false);

        return local.IsSuccess ? local : remote;
    }

    private async Task MirrorConfirmedCandidateLocallyAsync(
        PairingSessionSnapshot snapshot,
        NexusPermission permissions,
        string endpoint,
        string? tlsThumbprint,
        CancellationToken cancellationToken)
    {
        if (snapshot.CandidateDeviceId is null)
        {
            return;
        }

        TrustedDeviceRecord? fetched = null;
        var bridge = await TryReadBridgeAsync(cancellationToken).ConfigureAwait(false);
        if (bridge is not null)
        {
            using var client = _protocolClient.CreateHttpClient(endpoint, tlsThumbprint ?? bridge.TlsThumbprint);
            var remoteRecord = await _protocolClient.SendAsync<TrustedDeviceRecord>(
                client,
                HttpMethod.Get,
                $"api/v1/trusted-devices/{snapshot.CandidateDeviceId.Value:D}",
                bearerToken: bridge.AccessToken,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (remoteRecord.IsSuccess)
            {
                fetched = remoteRecord.Value;
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

        var now = snapshot.CompletedAtUtc ?? DateTimeOffset.UtcNow;
        var record = fetched ?? new TrustedDeviceRecord
        {
            DeviceId = snapshot.CandidateDeviceId.Value,
            CustomName = snapshot.CandidateDeviceName ?? "Android Pareado",
            Platform = snapshot.CandidatePlatform ?? DevicePlatform.Android,
            DeviceType = snapshot.CandidateDeviceType ?? DeviceType.Phone,
            OperatingSystemVersion = "Android",
            AppVersion = snapshot.CandidateAppVersion ?? "0.3.0",
            PublicKeySpkiBase64 = "mirrored-fingerprint-only",
            PublicKeyFingerprint = snapshot.CandidatePublicKeyFingerprint ?? new string('0', 64),
            Permissions = permissions,
            TrustStatus = DeviceTrustStatus.Authorized,
            PairedAtUtc = now,
            LastAccessAtUtc = now
        };

        await trustedRepo.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await deviceRepo.UpsertAsync(new DeviceInfo
        {
            Id = record.DeviceId,
            Name = record.CustomName,
            Platform = record.Platform,
            Type = record.DeviceType,
            OperatingSystemVersion = record.OperatingSystemVersion,
            AppVersion = record.AppVersion,
            HardwareFingerprint = record.PublicKeyFingerprint,
            Capabilities = DeviceCapability.Notifications | DeviceCapability.Clipboard | DeviceCapability.FileTransfer | DeviceCapability.Telemetry,
            Status = DeviceStatus.Online,
            RegisteredAtUtc = record.PairedAtUtc,
            LastSeenAtUtc = record.LastAccessAtUtc
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task TrySyncRemoteDevicesToLocalAsync(CancellationToken cancellationToken)
    {
        var remote = await TryExecuteRemoteAdminAsync<IReadOnlyList<TrustedDeviceRecord>>(
            HttpMethod.Get,
            "api/v1/trusted-devices",
            body: null,
            cancellationToken).ConfigureAwait(false);

        if (remote.IsFailure)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var trustedRepo = scope.ServiceProvider.GetRequiredService<ITrustedDeviceRepository>();
        var bridge = await TryReadBridgeAsync(cancellationToken).ConfigureAwait(false);

        foreach (var item in remote.Value)
        {
            if (bridge is not null && item.DeviceId == bridge.ServerDeviceId)
            {
                continue;
            }

            await trustedRepo.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Result<T>> TryExecuteRemoteAdminAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        var bridge = await TryReadBridgeAsync(cancellationToken).ConfigureAwait(false);
        if (bridge is null)
        {
            return Result<T>.Failure(NexusError.Unauthorized("Ponte administrativa local indisponível.", "BRIDGE_UNAVAILABLE"));
        }

        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = settings.IsSuccess ? settings.Value.ServerEndpoint.Trim().TrimEnd('/') : "http://127.0.0.1:5280";

        try
        {
            using var client = _protocolClient.CreateHttpClient(endpoint, bridge.TlsThumbprint);
            return await _protocolClient.SendAsync<T>(
                client,
                method,
                relativePath,
                body,
                bridge.AccessToken,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Remote admin call {Path} failed; falling back to local state", relativePath);
            return Result<T>.Failure(NexusError.Network(ex.Message));
        }
    }

    private async Task<HostBridgeTokenEnvelope?> TryReadBridgeAsync(CancellationToken cancellationToken)
    {
        var loaded = await _hostBridge.ReadAsync(cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess ? loaded.Value : null;
    }

    private async Task<(bool ServerReachable, string? Thumbprint)> TryFetchServerThumbprintAsync(
        string endpoint,
        string? knownThumbprint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _protocolClient.CreateHttpClient(endpoint, knownThumbprint, TimeSpan.FromSeconds(2));
            var info = await _protocolClient.SendAsync<ServerInfoLite>(
                client,
                HttpMethod.Get,
                "api/v1/system/info",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return info.IsSuccess
                ? (true, info.Value.TlsThumbprint)
                : (false, knownThumbprint);
        }
        catch
        {
            return (false, knownThumbprint);
        }
    }
}
