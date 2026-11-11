using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Security;

namespace Nexus.Contracts.Modules;

public enum IntegrationModuleKind
{
    Control = 0,
    Notify = 1,
    Monitor = 2,
    Media = 3
}

public enum ModuleAvailabilityState
{
    Available = 0,
    Limited = 1,
    Unavailable = 2,
    Disabled = 3,
    PermissionDenied = 4
}

public enum SensorAvailability
{
    Available = 0,
    Unavailable = 1,
    UnsupportedHardware = 2,
    PermissionRequired = 3,
    Partial = 4
}

public enum RemoteControlActionType
{
    MouseMove = 0,
    MouseRelativeMove = 1,
    MouseLeftClick = 2,
    MouseRightClick = 3,
    MouseDoubleClick = 4,
    MouseMiddleClick = 5,
    MouseScroll = 6,
    KeyPress = 7,
    TextInput = 8,
    Hotkey = 9,
    VolumeUp = 10,
    VolumeDown = 11,
    VolumeMuteToggle = 12,
    VolumeSet = 13,
    MediaPlayPause = 14,
    MediaNext = 15,
    MediaPrevious = 16,
    MediaStop = 17,
    PowerLock = 18,
    PowerSleep = 19,
    PowerShutdown = 20
}

public enum NotificationFilterMode
{
    AllowAll = 0,
    AllowList = 1,
    BlockList = 2
}

public enum MediaPlaybackStatus
{
    Unknown = 0,
    Closed = 1,
    Opened = 2,
    Changing = 3,
    Stopped = 4,
    Playing = 5,
    Paused = 6
}

public enum MediaControlAction
{
    PlayPause = 0,
    Play = 1,
    Pause = 2,
    Stop = 3,
    Next = 4,
    Previous = 5,
    VolumeUp = 6,
    VolumeDown = 7,
    SetVolume = 8,
    ToggleMute = 9
}

public sealed record SensorReading<T>
{
    public bool IsAvailable { get; init; }
    public T? Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public SensorAvailability Availability { get; init; } = SensorAvailability.Unavailable;
    public string StatusMessage { get; init; } = string.Empty;

    public static SensorReading<T> Available(T value, string unit = "", string statusMessage = "Disponível")
        => new()
        {
            IsAvailable = true,
            Value = value,
            Unit = unit,
            Availability = SensorAvailability.Available,
            StatusMessage = statusMessage
        };

    public static SensorReading<T> Unavailable(
        SensorAvailability availability,
        string reason,
        string unit = "")
        => new()
        {
            IsAvailable = false,
            Value = default,
            Unit = unit,
            Availability = availability,
            StatusMessage = reason
        };
}

public sealed record NamedSensorReading(
    string Name,
    SensorReading<double> Reading);

public sealed record IntegrationModuleStatusSnapshot
{
    public IntegrationModuleKind ModuleKind { get; init; }
    public string ModuleId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public NexusPermission RequiredPermission { get; init; }
    public ModuleAvailabilityState Availability { get; init; } = ModuleAvailabilityState.Available;
    public string StatusMessage { get; init; } = string.Empty;
    public bool RequireConfirmationForCriticalActions { get; init; } = true;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record UpdateModuleToggleRequest(
    IntegrationModuleKind ModuleKind,
    bool IsEnabled,
    bool? RequireConfirmationForCriticalActions = null);

public sealed record RemoteHotkeyDefinition
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string HotkeyId { get; init; } = string.Empty;

    [Required]
    [StringLength(80, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();
    public string Description { get; init; } = string.Empty;
}

public sealed record ControlModuleSettings
{
    public bool IsEnabled { get; init; } = true;
    public bool RequirePowerActionConfirmation { get; init; } = true;
    public bool AllowPowerActions { get; init; } = true;
    public IReadOnlyList<RemoteHotkeyDefinition> Hotkeys { get; init; } = DefaultHotkeys;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static IReadOnlyList<RemoteHotkeyDefinition> DefaultHotkeys { get; } = new RemoteHotkeyDefinition[]
    {
        new() { HotkeyId = "copy", DisplayName = "Copiar", Keys = ["Ctrl", "C"], Description = "Copia a seleção atual no Windows." },
        new() { HotkeyId = "paste", DisplayName = "Colar", Keys = ["Ctrl", "V"], Description = "Cola da área de transferência do Windows." },
        new() { HotkeyId = "switch-app", DisplayName = "Alternar Janela", Keys = ["Alt", "Tab"], Description = "Alterna para a janela anterior." },
        new() { HotkeyId = "task-manager", DisplayName = "Gerenciador de Tarefas", Keys = ["Ctrl", "Shift", "Esc"], Description = "Abre o Gerenciador de Tarefas do Windows." },
        new() { HotkeyId = "show-desktop", DisplayName = "Mostrar Área de Trabalho", Keys = ["Win", "D"], Description = "Minimiza ou restaura janelas." },
        new() { HotkeyId = "screenshot-snip", DisplayName = "Captura de Tela", Keys = ["Win", "Shift", "S"], Description = "Abre a Ferramenta de Captura do Windows." }
    };
}

public sealed record RemoteControlCommandRequest : IVersionedContract, IValidatableContract
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid? TargetDeviceId { get; init; }
    public RemoteControlActionType Action { get; init; }
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }
    public int ScrollDelta { get; init; }
    public string? KeyName { get; init; }
    public string? Text { get; init; }
    public string? HotkeyId { get; init; }
    public IReadOnlyList<string>? Keys { get; init; }
    public int? VolumePercent { get; init; }
    public bool ConfirmedByUser { get; init; }
    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public static bool IsHighImpactAction(RemoteControlActionType action)
        => action is RemoteControlActionType.PowerLock
            or RemoteControlActionType.PowerSleep
            or RemoteControlActionType.PowerShutdown;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (CommandId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CommandId), "CONTROL_COMMAND_ID_EMPTY", "CommandId cannot be empty.");
        }

        if (Action == RemoteControlActionType.KeyPress && string.IsNullOrWhiteSpace(KeyName))
        {
            yield return new ContractValidationError(nameof(KeyName), "CONTROL_KEY_REQUIRED", "KeyName is required for KeyPress.");
        }

        if (Action == RemoteControlActionType.TextInput && string.IsNullOrEmpty(Text))
        {
            yield return new ContractValidationError(nameof(Text), "CONTROL_TEXT_REQUIRED", "Text cannot be empty for TextInput.");
        }

        if (Action == RemoteControlActionType.Hotkey && string.IsNullOrWhiteSpace(HotkeyId) && (Keys is null || Keys.Count == 0))
        {
            yield return new ContractValidationError(nameof(HotkeyId), "CONTROL_HOTKEY_REQUIRED", "HotkeyId or Keys are required for Hotkey action.");
        }

        if (Action == RemoteControlActionType.VolumeSet && (!VolumePercent.HasValue || VolumePercent.Value < 0 || VolumePercent.Value > 100))
        {
            yield return new ContractValidationError(nameof(VolumePercent), "CONTROL_VOLUME_OUT_OF_RANGE", "VolumePercent must be between 0 and 100.");
        }
    }
}

public sealed record RemoteControlExecutionReceipt : IVersionedContract
{
    public Guid CommandId { get; init; }
    public Guid HostDeviceId { get; init; }
    public RemoteControlActionType Action { get; init; }
    public bool Executed { get; init; }
    public bool RequiresConfirmation { get; init; }
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset ExecutedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record AndroidNotificationPermissionNotice
{
    public bool IsSupportedOnCurrentOs { get; init; }
    public bool PermissionGranted { get; init; }
    public string PlatformNote { get; init; } = string.Empty;
}

public sealed record NotifyModuleSettings
{
    public bool IsEnabled { get; init; } = true;
    public NotificationFilterMode FilterMode { get; init; } = NotificationFilterMode.AllowAll;
    public IReadOnlyList<string> AllowedApps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedApps { get; init; } = Array.Empty<string>();
    public AndroidNotificationPermissionNotice AndroidPermission { get; init; } = new()
    {
        IsSupportedOnCurrentOs = false,
        PermissionGranted = false,
        PlatformNote = "Requer permissão explícita de NotificationListenerService no Android (Acesso às Notificações)."
    };
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PushMobileNotificationRequest : IVersionedContract, IValidatableContract
{
    public Guid NotificationId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid? TargetDeviceId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string AppName { get; init; } = string.Empty;

    [StringLength(200)]
    public string? PackageName { get; init; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Title { get; init; } = string.Empty;

    [StringLength(2000)]
    public string Body { get; init; } = string.Empty;

    public NotificationUrgency Urgency { get; init; } = NotificationUrgency.Normal;
    public IReadOnlyList<NotificationAction> Actions { get; init; } = Array.Empty<NotificationAction>();
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (NotificationId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(NotificationId), "NOTIFY_ID_EMPTY", "NotificationId cannot be empty.");
        }

        if (SourceDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SourceDeviceId), "NOTIFY_SOURCE_EMPTY", "SourceDeviceId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(AppName))
        {
            yield return new ContractValidationError(nameof(AppName), "NOTIFY_APP_REQUIRED", "AppName is required.");
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            yield return new ContractValidationError(nameof(Title), "NOTIFY_TITLE_REQUIRED", "Title is required.");
        }
    }
}

public sealed record MobileNotificationRecord : IVersionedContract
{
    public Guid NotificationId { get; init; }
    public Guid SourceDeviceId { get; init; }
    public Guid? TargetDeviceId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public string? PackageName { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public NotificationUrgency Urgency { get; init; } = NotificationUrgency.Normal;
    public bool IsDismissed { get; init; }
    public bool ForwardedToDesktop { get; init; }
    public string FilterOutcome { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record PushMobileNotificationReceipt
{
    public Guid NotificationId { get; init; }
    public bool Accepted { get; init; }
    public bool Forwarded { get; init; }
    public string FilterOutcome { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CpuTelemetrySnapshot
{
    public string ProcessorName { get; init; } = string.Empty;
    public int LogicalCores { get; init; }
    public SensorReading<double> UsagePercent { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Não medido");
    public SensorReading<int> NominalClockMhz { get; init; } = SensorReading<int>.Unavailable(SensorAvailability.Unavailable, "Não disponível");
}

public sealed record MemoryTelemetrySnapshot
{
    public SensorReading<double> UsagePercent { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Não medido");
    public SensorReading<double> TotalMb { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Não medido");
    public SensorReading<double> UsedMb { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Não medido");
    public SensorReading<double> AvailableMb { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Não medido");
}

public sealed record GpuTelemetrySnapshot
{
    public bool IsAvailable { get; init; }
    public string AdapterName { get; init; } = "Indisponível";
    public SensorReading<double> AdapterRamMb { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Indisponível");
    public SensorReading<double> UtilizationPercent { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Contador de GPU indisponível");
    public SensorReading<double> TemperatureCelsius { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.UnsupportedHardware, "Sensor térmico de GPU indisponível");
    public string StatusMessage { get; init; } = string.Empty;
}

public sealed record TemperatureTelemetrySnapshot
{
    public bool IsAvailable { get; init; }
    public SensorReading<double> PrimaryCelsius { get; init; } = SensorReading<double>.Unavailable(SensorAvailability.UnsupportedHardware, "Sensor de temperatura indisponível");
    public IReadOnlyList<NamedSensorReading> Sensors { get; init; } = Array.Empty<NamedSensorReading>();
    public string StatusMessage { get; init; } = string.Empty;
}

public sealed record DiskTelemetryItem
{
    public string DriveName { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long AvailableFreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableFreeBytes);
    public double UsedPercent => TotalBytes > 0 ? Math.Round((double)UsedBytes * 100d / TotalBytes, 1) : 0d;
}

public sealed record NetworkInterfaceTelemetryItem
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string InterfaceType { get; init; } = string.Empty;
    public bool IsUp { get; init; }
    public double LinkSpeedMbps { get; init; }
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }
    public double RxBytesPerSec { get; init; }
    public double TxBytesPerSec { get; init; }
}

public sealed record ProcessTelemetryItem
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public double WorkingSetMb { get; init; }
    public bool Responding { get; init; }
}

public sealed record HostTelemetrySnapshot : IVersionedContract
{
    public Guid SnapshotId { get; init; } = Guid.NewGuid();
    public Guid HostDeviceId { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string OsDescription { get; init; } = string.Empty;
    public TimeSpan Uptime { get; init; }
    public DateTimeOffset CollectedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public CpuTelemetrySnapshot Cpu { get; init; } = new();
    public MemoryTelemetrySnapshot Memory { get; init; } = new();
    public GpuTelemetrySnapshot Gpu { get; init; } = new();
    public TemperatureTelemetrySnapshot Temperatures { get; init; } = new();
    public IReadOnlyList<DiskTelemetryItem> Disks { get; init; } = Array.Empty<DiskTelemetryItem>();
    public IReadOnlyList<NetworkInterfaceTelemetryItem> NetworkInterfaces { get; init; } = Array.Empty<NetworkInterfaceTelemetryItem>();
    public IReadOnlyList<ProcessTelemetryItem> TopProcesses { get; init; } = Array.Empty<ProcessTelemetryItem>();
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record MonitorModuleSettings
{
    public bool IsEnabled { get; init; } = true;
    public int RefreshIntervalSeconds { get; init; } = 3;
    public bool IncludeTopProcesses { get; init; } = true;
    public int MaxProcesses { get; init; } = 12;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MediaPlaybackStateContract : IVersionedContract
{
    public Guid HostDeviceId { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsMetadataAvailable { get; init; }
    public string MetadataAvailabilityNote { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? SourceAppId { get; init; }
    public MediaPlaybackStatus PlaybackStatus { get; init; } = MediaPlaybackStatus.Unknown;
    public SensorReading<int> VolumePercent { get; init; } = SensorReading<int>.Unavailable(SensorAvailability.Unavailable, "Volume indisponível", "%");
    public SensorReading<bool> IsMuted { get; init; } = SensorReading<bool>.Unavailable(SensorAvailability.Unavailable, "Estado de mudo indisponível");
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;
}

public sealed record MediaControlCommandRequest : IVersionedContract, IValidatableContract
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid? TargetDeviceId { get; init; }
    public MediaControlAction Action { get; init; }
    public int? VolumePercent { get; init; }
    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (CommandId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(CommandId), "MEDIA_COMMAND_ID_EMPTY", "CommandId cannot be empty.");
        }

        if (Action == MediaControlAction.SetVolume && (!VolumePercent.HasValue || VolumePercent.Value < 0 || VolumePercent.Value > 100))
        {
            yield return new ContractValidationError(nameof(VolumePercent), "MEDIA_VOLUME_OUT_OF_RANGE", "VolumePercent must be between 0 and 100.");
        }
    }
}

public sealed record MediaModuleSettings
{
    public bool IsEnabled { get; init; } = true;
    public bool SyncPlaybackMetadata { get; init; } = true;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

