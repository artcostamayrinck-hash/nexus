using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Contracts.Devices;

namespace Nexus.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IIdGenerator
{
    Guid NewId();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}

public interface IJsonSerializer
{
    string Serialize<T>(T value);
    T? Deserialize<T>(string json);
    JsonSerializerOptions Options { get; }
}

public sealed class SystemTextJsonSerializer : IJsonSerializer
{
    public JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public T? Deserialize<T>(string json)
        => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

public interface ILocalDeviceContext
{
    Guid DeviceId { get; }
    string DeviceName { get; }
    DevicePlatform Platform { get; }
    DeviceType DeviceType { get; }
    string OperatingSystemVersion { get; }
    string AppVersion { get; }
    DeviceCapability Capabilities { get; }
    DeviceInfo ToDeviceInfo(DateTimeOffset? registeredAtUtc = null, DateTimeOffset? lastSeenAtUtc = null, DeviceStatus status = DeviceStatus.Online);
}

public sealed class DefaultLocalDeviceContext : ILocalDeviceContext
{
    public DefaultLocalDeviceContext(
        Guid deviceId,
        string deviceName,
        DevicePlatform platform,
        DeviceType deviceType,
        string operatingSystemVersion,
        string appVersion,
        DeviceCapability capabilities)
    {
        DeviceId = deviceId == Guid.Empty ? Guid.CreateVersion7() : deviceId;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        Platform = platform;
        DeviceType = deviceType;
        OperatingSystemVersion = string.IsNullOrWhiteSpace(operatingSystemVersion)
            ? Environment.OSVersion.VersionString
            : operatingSystemVersion;
        AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "0.2.0" : appVersion;
        Capabilities = capabilities;
    }

    public Guid DeviceId { get; }
    public string DeviceName { get; }
    public DevicePlatform Platform { get; }
    public DeviceType DeviceType { get; }
    public string OperatingSystemVersion { get; }
    public string AppVersion { get; }
    public DeviceCapability Capabilities { get; }

    public DeviceInfo ToDeviceInfo(
        DateTimeOffset? registeredAtUtc = null,
        DateTimeOffset? lastSeenAtUtc = null,
        DeviceStatus status = DeviceStatus.Online)
    {
        var now = DateTimeOffset.UtcNow;
        var registered = registeredAtUtc ?? now;
        var seen = lastSeenAtUtc ?? now;
        return new DeviceInfo
        {
            Id = DeviceId,
            Name = DeviceName,
            Platform = Platform,
            Type = DeviceType,
            OperatingSystemVersion = OperatingSystemVersion,
            AppVersion = AppVersion,
            Capabilities = Capabilities,
            Status = status,
            RegisteredAtUtc = registered,
            LastSeenAtUtc = seen < registered ? registered : seen
        };
    }
}

