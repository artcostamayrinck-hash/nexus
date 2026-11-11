using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.State;
using Nexus.Core.Storage;

namespace Nexus.Core.Services;

/// <summary>
/// Coordinates device validation, persistence, state counters, and domain event publication.
/// </summary>
public sealed class DeviceCoordinationService : IDeviceRegistry
{
    private readonly IDeviceRepository _repository;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly IStateManager<NexusRuntimeState> _stateManager;

    public DeviceCoordinationService(
        IDeviceRepository repository,
        IEventBus eventBus,
        IClock clock,
        IStateManager<NexusRuntimeState> stateManager)
    {
        _repository = repository;
        _eventBus = eventBus;
        _clock = clock;
        _stateManager = stateManager;
    }

    public async Task<Result<DeviceInfo>> RegisterOrUpdateAsync(
        DeviceRegistrationContract registration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var validation = ContractValidator.Validate(registration);
        if (!validation.IsValid)
        {
            return Result<DeviceInfo>.Failure(NexusError.Validation(validation));
        }

        var existingResult = await _repository.GetByIdAsync(registration.DeviceId, cancellationToken).ConfigureAwait(false);
        if (existingResult.IsFailure)
        {
            return Result<DeviceInfo>.Failure(existingResult.Error!);
        }

        var now = _clock.UtcNow;
        var registeredAt = existingResult.Value?.RegisteredAtUtc ?? now;

        var device = new DeviceInfo
        {
            Id = registration.DeviceId,
            Name = registration.Name.Trim(),
            Platform = registration.Platform,
            Type = registration.Type,
            OperatingSystemVersion = registration.OperatingSystemVersion.Trim(),
            AppVersion = registration.AppVersion.Trim(),
            HardwareFingerprint = registration.HardwareFingerprint,
            Capabilities = registration.Capabilities,
            Status = DeviceStatus.Online,
            RegisteredAtUtc = registeredAt,
            LastSeenAtUtc = now,
            ContractVersion = registration.ContractVersion
        };

        var upsert = await _repository.UpsertAsync(device, cancellationToken).ConfigureAwait(false);
        if (upsert.IsFailure)
        {
            return Result<DeviceInfo>.Failure(upsert.Error!);
        }

        await RefreshKnownDevicesCountAsync(cancellationToken).ConfigureAwait(false);
        await _eventBus.PublishAsync(new DeviceRegisteredDomainEvent(device), cancellationToken).ConfigureAwait(false);

        return Result<DeviceInfo>.Success(device);
    }

    public Task<Result<DeviceInfo?>> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            return Task.FromResult(Result<DeviceInfo?>.Failure(
                NexusError.Validation("DeviceId cannot be empty.", code: "DEVICE_ID_EMPTY")));
        }

        return _repository.GetByIdAsync(deviceId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DeviceInfo>>> GetAllDevicesAsync(CancellationToken cancellationToken = default)
        => _repository.ListAsync(cancellationToken);

    public async Task<Result> UpdateDeviceStatusAsync(
        Guid deviceId,
        DeviceStatus status,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (deviceId == Guid.Empty)
        {
            return Result.Failure(NexusError.Validation("DeviceId cannot be empty.", code: "DEVICE_ID_EMPTY"));
        }

        var existing = await _repository.GetByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result.Failure(existing.Error!);
        }

        if (existing.Value is null)
        {
            return Result.Failure(NexusError.NotFound($"Device '{deviceId}' was not found.", "DEVICE_NOT_FOUND"));
        }

        var previousStatus = existing.Value.Status;
        var update = await _repository.UpdateStatusAsync(deviceId, status, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (update.IsFailure)
        {
            return update;
        }

        if (previousStatus != status)
        {
            await _eventBus.PublishAsync(
                new DeviceStatusChangedDomainEvent(deviceId, previousStatus, status),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    public async Task<Result<bool>> RemoveDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            return Result<bool>.Failure(NexusError.Validation("DeviceId cannot be empty.", code: "DEVICE_ID_EMPTY"));
        }

        var deleted = await _repository.DeleteAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (deleted.IsSuccess && deleted.Value)
        {
            await RefreshKnownDevicesCountAsync(cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    private async Task RefreshKnownDevicesCountAsync(CancellationToken cancellationToken)
    {
        var list = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            var count = list.Value.Count;
            _stateManager.Update(s => s with
            {
                KnownDevicesCount = count,
                UpdatedAtUtc = _clock.UtcNow
            });
        }
    }
}

/// <summary>
/// Manages local connection lifecycle snapshots and notifies listeners of transitions.
/// </summary>
public sealed class ConnectionStateService : IConnectionManager
{
    private readonly Lock _sync = new();
    private readonly ILocalDeviceContext _localDevice;
    private readonly IClock _clock;
    private readonly IEventBus _eventBus;
    private readonly IStateManager<NexusRuntimeState> _stateManager;
    private ConnectionStateSnapshot _current;

    public ConnectionStateService(
        ILocalDeviceContext localDevice,
        IClock clock,
        IEventBus eventBus,
        IStateManager<NexusRuntimeState> stateManager)
    {
        _localDevice = localDevice;
        _clock = clock;
        _eventBus = eventBus;
        _stateManager = stateManager;
        _current = new ConnectionStateSnapshot
        {
            ConnectionId = Guid.CreateVersion7(),
            LocalDeviceId = localDevice.DeviceId,
            State = ConnectionState.Disconnected,
            Transport = TransportKind.Unknown,
            UpdatedAtUtc = clock.UtcNow
        };
    }

    public ConnectionStateSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public async Task<Result<ConnectionStateSnapshot>> TransitionAsync(
        ConnectionState targetState,
        TransportKind transport = TransportKind.Unknown,
        string? endpoint = null,
        Guid? remoteDeviceId = null,
        int? latencyMs = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _clock.UtcNow;
        var candidate = new ConnectionStateSnapshot
        {
            ConnectionId = _current.ConnectionId,
            LocalDeviceId = _localDevice.DeviceId,
            RemoteDeviceId = remoteDeviceId ?? _current.RemoteDeviceId,
            State = targetState,
            Transport = transport == TransportKind.Unknown ? _current.Transport : transport,
            Endpoint = endpoint ?? _current.Endpoint,
            LatencyMs = latencyMs,
            Reason = reason,
            UpdatedAtUtc = now
        };

        var validation = ContractValidator.Validate(candidate);
        if (!validation.IsValid)
        {
            return Result<ConnectionStateSnapshot>.Failure(NexusError.Validation(validation));
        }

        lock (_sync)
        {
            _current = candidate;
        }

        _stateManager.Update(s => s with
        {
            ConnectionState = candidate.State,
            ActiveEndpoint = candidate.Endpoint,
            UpdatedAtUtc = now
        });

        await _eventBus.PublishAsync(new ConnectionStateChangedDomainEvent(candidate), cancellationToken).ConfigureAwait(false);
        return Result<ConnectionStateSnapshot>.Success(candidate);
    }

    public async Task<Result<ConnectionStateSnapshot>> ConnectAsync(
        string endpoint,
        TransportKind transport,
        Guid? remoteDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Result<ConnectionStateSnapshot>.Failure(
                NexusError.Validation("Connection endpoint cannot be empty.", code: "ENDPOINT_EMPTY"));
        }

        var connecting = await TransitionAsync(
            ConnectionState.Connecting,
            transport,
            endpoint.Trim(),
            remoteDeviceId,
            reason: "Initiating connection",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (connecting.IsFailure)
        {
            return connecting;
        }

        return await TransitionAsync(
            ConnectionState.Connected,
            transport,
            endpoint.Trim(),
            remoteDeviceId,
            latencyMs: 0,
            reason: "Channel prepared",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<ConnectionStateSnapshot>> DisconnectAsync(
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(
            ConnectionState.Disconnected,
            TransportKind.Unknown,
            endpoint: null,
            remoteDeviceId: null,
            latencyMs: null,
            reason: reason ?? "Disconnected by user",
            cancellationToken: cancellationToken);
    }
}

public sealed record SubsystemHealthReport(
    bool IsHealthy,
    DeviceInfo LocalDevice,
    ConnectionState ConnectionState,
    StorageStats? Storage,
    DateTimeOffset CheckedAtUtc);

public sealed class DiagnosticHealthService
{
    private readonly ILocalDeviceContext _localDevice;
    private readonly IConnectionManager _connectionManager;
    private readonly IFileStorageService _fileStorage;
    private readonly IClock _clock;

    public DiagnosticHealthService(
        ILocalDeviceContext localDevice,
        IConnectionManager connectionManager,
        IFileStorageService fileStorage,
        IClock clock)
    {
        _localDevice = localDevice;
        _connectionManager = connectionManager;
        _fileStorage = fileStorage;
        _clock = clock;
    }

    public async Task<Result<SubsystemHealthReport>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var storageResult = await _fileStorage.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var report = new SubsystemHealthReport(
            IsHealthy: storageResult.IsSuccess && storageResult.Value.IsAvailable,
            LocalDevice: _localDevice.ToDeviceInfo(lastSeenAtUtc: now),
            ConnectionState: _connectionManager.CurrentSnapshot.State,
            Storage: storageResult.IsSuccess ? storageResult.Value : null,
            CheckedAtUtc: now);

        return Result<SubsystemHealthReport>.Success(report);
    }
}

