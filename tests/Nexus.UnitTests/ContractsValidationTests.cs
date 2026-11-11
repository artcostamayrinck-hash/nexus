using Nexus.Contracts.Commands;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Events;
using Nexus.Contracts.Identity;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Sync;
using Nexus.Contracts.Transfers;
using Xunit;

namespace Nexus.UnitTests;

public sealed class ContractsValidationTests
{
    [Fact]
    public void DeviceRegistrationContract_Valid_PassesValidation()
    {
        var contract = new DeviceRegistrationContract
        {
            DeviceId = Guid.CreateVersion7(),
            Name = "Workstation-01",
            Platform = DevicePlatform.Windows,
            Type = DeviceType.Desktop,
            OperatingSystemVersion = "Windows 11 24H2",
            AppVersion = "0.2.0",
            Capabilities = DeviceCapability.Clipboard | DeviceCapability.FileTransfer
        };

        var result = ContractValidator.Validate(contract);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DeviceInfo_UnsupportedVersionAndUnknownPlatform_FailsValidation()
    {
        var device = new DeviceInfo
        {
            Id = Guid.Empty,
            Name = "A",
            Platform = DevicePlatform.Unknown,
            OperatingSystemVersion = "OS",
            AppVersion = "1.0",
            ContractVersion = "99.0"
        };

        var result = ContractValidator.Validate(device);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "UNSUPPORTED_CONTRACT_VERSION");
        Assert.Contains(result.Errors, e => e.Code == "DEVICE_ID_EMPTY");
        Assert.Contains(result.Errors, e => e.Code == "DEVICE_PLATFORM_UNKNOWN");
    }

    [Fact]
    public void SessionInfo_ExpiresBeforeIssued_FailsValidation()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new SessionInfo
        {
            SessionId = Guid.CreateVersion7(),
            UserId = Guid.CreateVersion7(),
            DeviceId = Guid.CreateVersion7(),
            Status = SessionStatus.Active,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(-5)
        };

        var result = ContractValidator.Validate(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "SESSION_EXPIRY_INVALID");
    }

    [Fact]
    public void DeviceCommand_And_Result_ValidateInvariants()
    {
        var cmd = new DeviceCommand
        {
            SourceDeviceId = Guid.CreateVersion7(),
            TargetDeviceId = Guid.CreateVersion7(),
            CommandName = "ping",
            Timeout = TimeSpan.FromSeconds(10)
        };
        Assert.True(ContractValidator.Validate(cmd).IsValid);

        var failedResultMissingError = new CommandExecutionResult
        {
            CommandId = cmd.CommandId,
            TargetDeviceId = cmd.TargetDeviceId,
            State = CommandExecutionState.Failed,
            Error = null
        };
        var validation = ContractValidator.Validate(failedResultMissingError);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Code == "FAILED_COMMAND_MISSING_ERROR");
    }

    [Fact]
    public void FileTransferMetadata_RejectsPathTraversalAndExceededBytes()
    {
        var transfer = new FileTransferMetadata
        {
            SourceDeviceId = Guid.CreateVersion7(),
            TargetDeviceId = Guid.CreateVersion7(),
            FileName = "../secrets.txt",
            TotalBytes = 100,
            TransferredBytes = 250,
            Sha256Checksum = "short"
        };

        var result = ContractValidator.Validate(transfer);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "TRANSFER_FILENAME_UNSAFE");
        Assert.Contains(result.Errors, e => e.Code == "TRANSFER_BYTES_EXCEEDED");
        Assert.Contains(result.Errors, e => e.Code == "CHECKSUM_LENGTH_INVALID");
    }

    [Fact]
    public void ConnectionStateSnapshot_ConnectedRequiresKnownTransport()
    {
        var invalid = new ConnectionStateSnapshot
        {
            LocalDeviceId = Guid.CreateVersion7(),
            State = ConnectionState.Connected,
            Transport = TransportKind.Unknown
        };

        var result = ContractValidator.Validate(invalid);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "CONNECTED_TRANSPORT_UNKNOWN");
    }

    [Fact]
    public void SyncBatch_Notification_Request_Event_ValidContractsPass()
    {
        var src = Guid.CreateVersion7();
        var tgt = Guid.CreateVersion7();

        var request = new NexusRequest<string>
        {
            SourceDeviceId = src,
            TargetDeviceId = tgt,
            Action = "system.ping",
            Payload = "hello",
            Timeout = TimeSpan.FromSeconds(5)
        };
        Assert.True(ContractValidator.Validate(request).IsValid);

        var evt = new IntegrationEvent
        {
            EventType = NexusEventTypes.SystemHealthChanged,
            SourceDeviceId = src,
            Severity = EventSeverity.Information
        };
        Assert.True(ContractValidator.Validate(evt).IsValid);

        var notification = new DeviceNotification
        {
            SourceDeviceId = src,
            TargetDeviceId = tgt,
            AppName = "Messages",
            Title = "Nova mensagem",
            Body = "Olá do Android",
            Actions = [new NotificationAction("reply", "Responder")]
        };
        Assert.True(ContractValidator.Validate(notification).IsValid);

        var sync = new SyncBatchContract
        {
            SourceDeviceId = src,
            TargetDeviceId = tgt,
            Scope = "devices",
            BaseRevision = 1,
            TargetRevision = 2,
            Items = [new SyncItemMetadata("Device", src.ToString("D"), 2, "abc123", DateTimeOffset.UtcNow)]
        };
        Assert.True(ContractValidator.Validate(sync).IsValid);
    }

    [Fact]
    public void SyncBatch_RevisionRegression_FailsValidation()
    {
        var sync = new SyncBatchContract
        {
            SourceDeviceId = Guid.CreateVersion7(),
            TargetDeviceId = Guid.CreateVersion7(),
            Scope = "settings",
            BaseRevision = 10,
            TargetRevision = 4
        };

        var result = ContractValidator.Validate(sync);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "SYNC_REVISION_REGRESSION");
    }
}

