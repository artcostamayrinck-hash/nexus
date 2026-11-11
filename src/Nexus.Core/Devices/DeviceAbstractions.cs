using Nexus.Contracts.Devices;
using Nexus.Core.Errors;

namespace Nexus.Core.Devices;

public interface IDeviceCapabilityProvider
{
    DeviceCapability GetLocalCapabilities();
    IReadOnlyList<string> DescribeCapabilities(DeviceCapability capabilities);
}

public sealed class DefaultDeviceCapabilityProvider : IDeviceCapabilityProvider
{
    public DeviceCapability GetLocalCapabilities()
    {
        if (OperatingSystem.IsWindows())
        {
            return DeviceCapability.Notifications
                | DeviceCapability.Clipboard
                | DeviceCapability.FileTransfer
                | DeviceCapability.RemoteCommands
                | DeviceCapability.Telemetry;
        }

        if (OperatingSystem.IsAndroid())
        {
            return DeviceCapability.Notifications
                | DeviceCapability.Clipboard
                | DeviceCapability.FileTransfer
                | DeviceCapability.MediaControl
                | DeviceCapability.Telemetry;
        }

        return DeviceCapability.Telemetry | DeviceCapability.FileTransfer;
    }

    public IReadOnlyList<string> DescribeCapabilities(DeviceCapability capabilities)
    {
        if (capabilities == DeviceCapability.None)
        {
            return Array.Empty<string>();
        }

        return Enum.GetValues<DeviceCapability>()
            .Where(c => c != DeviceCapability.None && c != DeviceCapability.All && capabilities.HasFlag(c))
            .Select(c => c.ToString())
            .ToArray();
    }
}

public interface IDeviceRegistry
{
    Task<Result<DeviceInfo>> RegisterOrUpdateAsync(DeviceRegistrationContract registration, CancellationToken cancellationToken = default);
    Task<Result<DeviceInfo?>> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<DeviceInfo>>> GetAllDevicesAsync(CancellationToken cancellationToken = default);
    Task<Result> UpdateDeviceStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken cancellationToken = default);
    Task<Result<bool>> RemoveDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

