using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Services;
using Nexus.Desktop.Services.Security;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Security;
using Nexus.Mobile.Services.Security;
using Nexus.Mobile.Services.ViewModels;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Xunit;

namespace Nexus.UnitTests;

public sealed class RealtimeProtocolTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-rt-unit-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RealtimeMessageEnvelope_Validation_RejectsInvalidAndAcceptsCompleteEnvelope()
    {
        var invalid = new RealtimeMessageEnvelope
        {
            MessageId = Guid.Empty,
            MessageType = "X",
            SourceDeviceId = Guid.Empty,
            TargetDeviceId = Guid.Empty,
            SequenceNumber = -1,
            PayloadJson = "{}",
            CorrelationId = Guid.Empty
        };

        var invalidResult = ContractValidator.Validate(invalid);
        Assert.False(invalidResult.IsValid);
        Assert.Contains(invalidResult.Errors, e => e.PropertyName == nameof(RealtimeMessageEnvelope.SourceDeviceId));
        Assert.Contains(invalidResult.Errors, e => e.PropertyName == nameof(RealtimeMessageEnvelope.TargetDeviceId));
        Assert.Contains(invalidResult.Errors, e => e.PropertyName == nameof(RealtimeMessageEnvelope.SequenceNumber));

        var valid = new RealtimeMessageEnvelope
        {
            MessageId = Guid.CreateVersion7(),
            MessageType = "nexus.test.event",
            Kind = RealtimeMessageKind.Event,
            SourceDeviceId = Guid.CreateVersion7(),
            TargetDeviceId = Guid.CreateVersion7(),
            SequenceNumber = 1,
            PayloadJson = "{\"ok\":true}",
            TimestampUtc = _clock.UtcNow,
            CorrelationId = Guid.CreateVersion7()
        };

        Assert.True(ContractValidator.Validate(valid).IsValid);
    }

    [Fact]
    public void RealtimeMessagePipeline_DeduplicatesRepeatedMessages_AndPreservesCounters()
    {
        var pipeline = new RealtimeMessagePipeline(_clock);
        var senderId = Guid.CreateVersion7();
        var msgId = Guid.CreateVersion7();

        var first = CreateEnvelope(senderId, 1, msgId);
        var firstDecision = pipeline.ProcessIncoming(first);
        Assert.Equal(RealtimeAckStatus.Processed, firstDecision.Status);
        Assert.Single(firstDecision.ReadyMessages);

        var duplicate = CreateEnvelope(senderId, 2, msgId);
        var dupDecision = pipeline.ProcessIncoming(duplicate);
        Assert.Equal(RealtimeAckStatus.DuplicateIgnored, dupDecision.Status);
        Assert.Empty(dupDecision.ReadyMessages);
        Assert.Equal(1, pipeline.DuplicatesDroppedCount);
    }

    [Fact]
    public void RealtimeMessagePipeline_ReordersOutOfOrderMessages_StrictlyInSequence()
    {
        var pipeline = new RealtimeMessagePipeline(_clock);
        var senderId = Guid.CreateVersion7();

        var d3 = pipeline.ProcessIncoming(CreateEnvelope(senderId, 3));
        Assert.Equal(RealtimeAckStatus.OutOfOrderBuffered, d3.Status);
        Assert.Empty(d3.ReadyMessages);
        Assert.Equal(1, d3.ExpectedNextSequence);

        var d2 = pipeline.ProcessIncoming(CreateEnvelope(senderId, 2));
        Assert.Equal(RealtimeAckStatus.OutOfOrderBuffered, d2.Status);
        Assert.Empty(d2.ReadyMessages);

        var d1 = pipeline.ProcessIncoming(CreateEnvelope(senderId, 1));
        Assert.Equal(RealtimeAckStatus.Processed, d1.Status);
        Assert.Equal(new long[] { 1, 2, 3 }, d1.ReadyMessages.Select(m => m.SequenceNumber).ToArray());
        Assert.True(pipeline.OutOfOrderReorderedCount >= 2);
    }

    [Fact]
    public async Task DevicesViewModels_NeverDisplayOfflineRegisteredDevicesAsConnected()
    {
        var trustedPeer = new TrustedDeviceRecord
        {
            DeviceId = Guid.CreateVersion7(),
            CustomName = "Pixel 9 Offline",
            Platform = DevicePlatform.Android,
            DeviceType = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "0.5.0",
            PublicKeySpkiBase64 = "TEST_SPKI",
            PublicKeyFingerprint = new string('A', 64),
            TrustStatus = DeviceTrustStatus.Authorized,
            Permissions = NexusPermission.All,
            PairedAtUtc = _clock.UtcNow.AddDays(-1),
            LastAccessAtUtc = _clock.UtcNow.AddHours(-2)
        };

        var deviceInfo = new DeviceInfo
        {
            Id = trustedPeer.DeviceId,
            Name = trustedPeer.CustomName,
            Platform = DevicePlatform.Android,
            Type = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "0.5.0"
        };

        var fakeRegistry = new StubDeviceRegistry([deviceInfo]);
        var localCtx = new DefaultLocalDeviceContext(
            Guid.CreateVersion7(), "Windows Host", DevicePlatform.Windows, DeviceType.Desktop, "Windows 11", "0.5.0", DeviceCapability.All);

        var desktopVm = new DevicesViewModel(
            fakeRegistry,
            localCtx,
            new StubDesktopPairingClient([trustedPeer]));

        await desktopVm.LoadAllAsync();
        Assert.Single(desktopVm.TrustedDeviceItems);
        Assert.False(desktopVm.TrustedDeviceItems[0].IsRealtimeConnected);
        Assert.Equal(ConnectionState.Disconnected, desktopVm.TrustedDeviceItems[0].ConnectionState);
        Assert.Equal("Offline", desktopVm.TrustedDeviceItems[0].ConnectionBadgeText);
        Assert.Equal("Desconectado (Offline)", desktopVm.SelectedConnectionBadge);
        Assert.Equal(0, desktopVm.ConnectedTrustedCount);

        var mobileVm = new MobileDevicesViewModel(
            fakeRegistry,
            new StubMobilePairingClient([trustedPeer]));

        await mobileVm.LoadAsync();
        Assert.Single(mobileVm.TrustedDeviceItems);
        Assert.False(mobileVm.TrustedDeviceItems[0].IsRealtimeConnected);
        Assert.Equal(ConnectionState.Disconnected, mobileVm.TrustedDeviceItems[0].ConnectionState);
        Assert.Equal("Offline", mobileVm.TrustedDeviceItems[0].ConnectionBadgeText);
        Assert.Equal("Desconectado (Offline)", mobileVm.SelectedConnectionBadge);
    }

    [Fact]
    public async Task HostBridgeCredentialProvider_ReturnsUnauthorized_WhenNoLocalTokenExists()
    {
        Directory.CreateDirectory(_tempDir);
        var identityProvider = new CryptographicIdentityProvider(
            new PlatformSecureCredentialStore(new SecureCredentialStoreOptions { VaultDirectory = Path.Combine(_tempDir, "vault") }),
            new DefaultLocalDeviceContext(Guid.Empty, "Test Host", DevicePlatform.Windows, DeviceType.Desktop, "Windows 11", "0.5.0", DeviceCapability.All),
            new SystemTextJsonSerializer(),
            _clock);

        var provider = new HostBridgeRealtimeCredentialProvider(
            new EmptyHostTokenBridge(),
            identityProvider,
            new StubEndpointRepo(),
            new StubSettingsStore(),
            _clock);

        var result = await provider.GetCredentialsAsync();
        Assert.True(result.IsFailure);
        Assert.Equal("REALTIME_AUTH_MISSING", result.Error!.Code);
        Assert.Equal(ErrorCategory.Unauthorized, result.Error.Category);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private RealtimeMessageEnvelope CreateEnvelope(Guid sourceDeviceId, long sequenceNumber, Guid? messageId = null)
        => new()
        {
            MessageId = messageId ?? Guid.CreateVersion7(),
            MessageType = "nexus.unit.test",
            Kind = RealtimeMessageKind.Event,
            SourceDeviceId = sourceDeviceId,
            SequenceNumber = sequenceNumber,
            StreamKey = "default",
            PayloadJson = "{\"seq\":" + sequenceNumber + "}",
            TimestampUtc = _clock.UtcNow,
            CorrelationId = Guid.CreateVersion7()
        };

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class EmptyHostTokenBridge : ILocalHostTokenBridge
    {
        public Task<Result> WriteAsync(HostBridgeTokenEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<HostBridgeTokenEnvelope?>> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<HostBridgeTokenEnvelope?>.Success(null));
    }

    private sealed class StubEndpointRepo : IEndpointRegistryRepository
    {
        public Task<Result<IReadOnlyList<DeviceEndpointRecord>>> ListAsync(Guid? deviceId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<DeviceEndpointRecord>>.Success(Array.Empty<DeviceEndpointRecord>()));
        public Task<Result<DeviceEndpointRecord?>> GetByIdAsync(Guid endpointId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<DeviceEndpointRecord?>.Success(null));
        public Task<Result<DeviceEndpointRecord>> UpsertAsync(DeviceEndpointRecord endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<DeviceEndpointRecord>.Success(endpoint));
        public Task<Result> DeleteAsync(Guid endpointId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result> UpdateTelemetryAsync(Guid endpointId, int? latencyMs, DateTimeOffset? verifiedAtUtc, DateTimeOffset? connectedAtUtc, string? lastError, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
    }

    private sealed class StubSettingsStore : Nexus.Core.Storage.ISettingsStore
    {
        public Task<Result<T?>> GetAsync<T>(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<T?>.Success(default));
        public Task<Result<T>> GetOrDefaultAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<T>.Success(defaultValue));
        public Task<Result> SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<bool>> RemoveAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<bool>.Success(false));
        public Task<Result<IReadOnlyDictionary<string, string>>> GetAllRawAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Success(new Dictionary<string, string>()));
    }

    private sealed class StubDeviceRegistry(IReadOnlyList<DeviceInfo> list) : IDeviceRegistry
    {
        public Task<Result<DeviceInfo>> RegisterOrUpdateAsync(DeviceRegistrationContract registration, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<DeviceInfo?>> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<DeviceInfo?>.Success(list.FirstOrDefault(d => d.Id == deviceId)));
        public Task<Result<IReadOnlyList<DeviceInfo>>> GetAllDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<DeviceInfo>>.Success(list));
        public Task<Result> UpdateDeviceStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<bool>> RemoveDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<bool>.Success(false));
    }

    private sealed class StubDesktopPairingClient(IReadOnlyList<TrustedDeviceRecord> list) : IDesktopPairingClient
    {
        public Task<Result<StartedPairingOutput>> StartPairingAsync(int lifetimeSeconds = 120, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<PairingSessionSnapshot>> GetSessionAsync(Guid pairingId, bool isRemoteServer, string endpoint, string? tlsThumbprint, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<PairingSessionSnapshot>> ConfirmPairingAsync(Guid pairingId, bool approve, NexusPermission permissions, bool isRemoteServer, string endpoint, string? tlsThumbprint, string? rejectionReason = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListTrustedDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<TrustedDeviceRecord>>.Success(list));
        public Task<Result<TrustedDeviceRecord>> RenameTrustedDeviceAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<TrustedDeviceRecord>> UpdatePermissionsAsync(Guid deviceId, NexusPermission permissions, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result> RevokeTrustedDeviceAsync(Guid deviceId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<bool>> RemoveTrustedDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<bool>.Success(true));
    }

    private sealed class StubMobilePairingClient(IReadOnlyList<TrustedDeviceRecord> list) : IMobilePairingClient
    {
        public Task<Result<MobilePairedSessionEnvelope?>> GetStoredSessionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<MobilePairedSessionEnvelope?>.Success(null));
        public Task<Result<MobileClaimOutcome>> SubmitQrPairingAsync(string rawQrJson, NexusPermission requestedPermissions = NexusPermission.ViewStatus, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<(PairingSessionSnapshot Snapshot, DeviceAuthTokenPair? Tokens)>> CheckAndFinalizeAsync(PairingQrPayload qr, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<DeviceAuthTokenPair>> RefreshStoredTokensAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<TrustedDeviceRecord>>> ListTrustedDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<TrustedDeviceRecord>>.Success(list));
        public Task<Result<TrustedDeviceRecord>> RenameTrustedDeviceAsync(Guid deviceId, string newName, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Result> RevokeTrustedDeviceAsync(Guid deviceId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success());
        public Task<Result<bool>> RemoveTrustedDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<bool>.Success(true));
    }
}
