namespace Nexus.Contracts.Devices;

public enum DevicePlatform
{
    Unknown = 0,
    Windows = 1,
    Android = 2,
    Linux = 3,
    MacOS = 4
}

public enum DeviceType
{
    Unknown = 0,
    Desktop = 1,
    Laptop = 2,
    Phone = 3,
    Tablet = 4,
    Server = 5
}

public enum DeviceStatus
{
    Offline = 0,
    Online = 1,
    Connecting = 2,
    Busy = 3,
    Error = 4
}

[Flags]
public enum DeviceCapability
{
    None = 0,
    Notifications = 1 << 0,
    Clipboard = 1 << 1,
    FileTransfer = 1 << 2,
    RemoteCommands = 1 << 3,
    ScreenMirror = 1 << 4,
    MediaControl = 1 << 5,
    Telemetry = 1 << 6,
    All = Notifications | Clipboard | FileTransfer | RemoteCommands | ScreenMirror | MediaControl | Telemetry
}

