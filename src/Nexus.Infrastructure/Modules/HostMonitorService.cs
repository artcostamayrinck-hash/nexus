using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Modules;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Modules;

namespace Nexus.Infrastructure.Modules;

/// <summary>
/// Collects genuine host CPU, memory, GPU adapter, thermal availability, disk, network,
/// and process metrics using Windows Win32/Registry APIs and .NET runtime collectors.
/// Explicitly reports unavailable or restricted sensors rather than simulating values.
/// </summary>
public sealed class HostMonitorService : IHostMonitorService
{
    private readonly IModuleSettingsService _settingsService;
    private readonly ILocalDeviceContext _hostIdentity;
    private readonly IClock _clock;
    private readonly ILogger<HostMonitorService> _logger;
    private readonly SemaphoreSlim _sampleLock = new(1, 1);

    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;
    private bool _hasCpuBaseline;

    private DateTimeOffset? _prevNetSampleUtc;
    private readonly Dictionary<string, (long Rx, long Tx)> _prevNetBytes = new(StringComparer.OrdinalIgnoreCase);

    public HostMonitorService(
        IModuleSettingsService settingsService,
        ILocalDeviceContext hostIdentity,
        IClock clock,
        ILogger<HostMonitorService> logger)
    {
        _settingsService = settingsService;
        _hostIdentity = hostIdentity;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<HostTelemetrySnapshot>> CollectSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settingsRes = await _settingsService.GetMonitorSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsFailure)
        {
            return Result<HostTelemetrySnapshot>.Failure(settingsRes.Error!);
        }

        var settings = settingsRes.Value;
        if (!settings.IsEnabled)
        {
            return Result<HostTelemetrySnapshot>.Failure(
                NexusError.Forbidden("Nexus Monitor está desativado nas configurações locais.", "MODULE_DISABLED"));
        }

        await _sampleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            var cpu = await CollectCpuAsync(cancellationToken).ConfigureAwait(false);
            var memory = CollectMemory();
            var gpu = CollectGpu();
            var temperatures = CollectTemperatures();
            var disks = CollectDisks();
            var network = CollectNetwork(now);
            var processes = settings.IncludeTopProcesses
                ? CollectTopProcesses(settings.MaxProcesses)
                : Array.Empty<ProcessTelemetryItem>();

            var uptime = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64));

            return Result<HostTelemetrySnapshot>.Success(new HostTelemetrySnapshot
            {
                HostDeviceId = _hostIdentity.DeviceId,
                HostName = Environment.MachineName,
                OsDescription = RuntimeInformation.OSDescription,
                Uptime = uptime,
                CollectedAtUtc = now,
                Cpu = cpu,
                Memory = memory,
                Gpu = gpu,
                Temperatures = temperatures,
                Disks = disks,
                NetworkInterfaces = network,
                TopProcesses = processes
            });
        }
        finally
        {
            _sampleLock.Release();
        }
    }

    private async Task<CpuTelemetrySnapshot> CollectCpuAsync(CancellationToken cancellationToken)
    {
        var processorName = $"{RuntimeInformation.ProcessArchitecture} CPU";
        var clockReading = SensorReading<int>.Unavailable(SensorAvailability.Unavailable, "Frequência nominal indisponível", "MHz");
        var usageReading = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "Amostragem de CPU indisponível", "%");

        if (OperatingSystem.IsWindows())
        {
            ReadWindowsCpuMetadata(ref processorName, ref clockReading);

            if (GetSystemTimes(out var idleFt1, out var kernelFt1, out var userFt1))
            {
                var idle1 = ToUInt64(idleFt1);
                var kernel1 = ToUInt64(kernelFt1);
                var user1 = ToUInt64(userFt1);

                if (!_hasCpuBaseline)
                {
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    if (GetSystemTimes(out var idleFt2, out var kernelFt2, out var userFt2))
                    {
                        var idle2 = ToUInt64(idleFt2);
                        var kernel2 = ToUInt64(kernelFt2);
                        var user2 = ToUInt64(userFt2);

                        usageReading = ComputeWindowsCpuUsage(idle1, kernel1, user1, idle2, kernel2, user2);
                        _prevIdleTime = idle2;
                        _prevKernelTime = kernel2;
                        _prevUserTime = user2;
                        _hasCpuBaseline = true;
                    }
                }
                else
                {
                    usageReading = ComputeWindowsCpuUsage(_prevIdleTime, _prevKernelTime, _prevUserTime, idle1, kernel1, user1);
                    _prevIdleTime = idle1;
                    _prevKernelTime = kernel1;
                    _prevUserTime = user1;
                }
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                usageReading = SensorReading<double>.Unavailable(
                    SensorAvailability.PermissionRequired,
                    $"GetSystemTimes falhou (Win32Error={err}).",
                    "%");
            }
        }
        else
        {
            usageReading = SensorReading<double>.Unavailable(
                SensorAvailability.UnsupportedHardware,
                "Coleta nativa Win32 GetSystemTimes disponível apenas no Windows.",
                "%");
        }

        return new CpuTelemetrySnapshot
        {
            ProcessorName = processorName,
            LogicalCores = Environment.ProcessorCount,
            UsagePercent = usageReading,
            NominalClockMhz = clockReading
        };
    }

    private static SensorReading<double> ComputeWindowsCpuUsage(
        ulong idle1, ulong kernel1, ulong user1,
        ulong idle2, ulong kernel2, ulong user2)
    {
        var idleDelta = idle2 >= idle1 ? idle2 - idle1 : 0;
        var kernelDelta = kernel2 >= kernel1 ? kernel2 - kernel1 : 0;
        var userDelta = user2 >= user1 ? user2 - user1 : 0;
        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return SensorReading<double>.Available(0d, "%", "Win32 GetSystemTimes");
        }

        var busy = total >= idleDelta ? total - idleDelta : 0;
        var pct = Math.Clamp(Math.Round((double)busy * 100d / total, 1), 0d, 100d);
        return SensorReading<double>.Available(pct, "%", "Win32 GetSystemTimes");
    }

    [SupportedOSPlatform("windows")]
    private static void ReadWindowsCpuMetadata(ref string processorName, ref SensorReading<int> clockReading)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is null) return;

            if (key.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                processorName = name.Trim();
            }

            if (key.GetValue("~MHz") is int mhz && mhz > 0)
            {
                clockReading = SensorReading<int>.Available(mhz, "MHz", "Registro de sistema Windows");
            }
        }
        catch
        {
            // Keep safe defaults when registry access is restricted.
        }
    }

    private static MemoryTelemetrySnapshot CollectMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
            {
                var totalMb = Math.Round(status.ullTotalPhys / (1024d * 1024d), 1);
                var availMb = Math.Round(status.ullAvailPhys / (1024d * 1024d), 1);
                var usedMb = Math.Round(Math.Max(0, totalMb - availMb), 1);
                var pct = Math.Clamp((double)status.dwMemoryLoad, 0d, 100d);

                return new MemoryTelemetrySnapshot
                {
                    UsagePercent = SensorReading<double>.Available(pct, "%", "Win32 GlobalMemoryStatusEx"),
                    TotalMb = SensorReading<double>.Available(totalMb, "MB", "Win32 GlobalMemoryStatusEx"),
                    UsedMb = SensorReading<double>.Available(usedMb, "MB", "Win32 GlobalMemoryStatusEx"),
                    AvailableMb = SensorReading<double>.Available(availMb, "MB", "Win32 GlobalMemoryStatusEx")
                };
            }
        }

        return new MemoryTelemetrySnapshot
        {
            UsagePercent = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "GlobalMemoryStatusEx indisponível.", "%"),
            TotalMb = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "GlobalMemoryStatusEx indisponível.", "MB"),
            UsedMb = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "GlobalMemoryStatusEx indisponível.", "MB"),
            AvailableMb = SensorReading<double>.Unavailable(SensorAvailability.Unavailable, "GlobalMemoryStatusEx indisponível.", "MB")
        };
    }

    private static GpuTelemetrySnapshot CollectGpu()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new GpuTelemetrySnapshot
            {
                IsAvailable = false,
                AdapterName = "Indisponível",
                StatusMessage = "Detecção de GPU disponível apenas no host Windows."
            };
        }

        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (displayClass is not null)
            {
                foreach (var subName in displayClass.GetSubKeyNames())
                {
                    if (!subName.All(char.IsDigit)) continue;
                    using var adapterKey = displayClass.OpenSubKey(subName);
                    var desc = adapterKey?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(desc)) continue;

                    var vramReading = SensorReading<double>.Unavailable(
                        SensorAvailability.UnsupportedHardware,
                        "Memória dedicada de vídeo não reportada no registro.",
                        "MB");

                    var rawMem = adapterKey?.GetValue("HardwareInformation.qwMemorySize")
                        ?? adapterKey?.GetValue("HardwareInformation.MemorySize");
                    if (rawMem is long qwordMem && qwordMem > 0)
                    {
                        vramReading = SensorReading<double>.Available(Math.Round(qwordMem / (1024d * 1024d), 0), "MB", "Registro de driver WDDM");
                    }
                    else if (rawMem is int dwordMem && dwordMem > 0)
                    {
                        vramReading = SensorReading<double>.Available(Math.Round((uint)dwordMem / (1024d * 1024d), 0), "MB", "Registro de driver WDDM");
                    }
                    else if (rawMem is byte[] bytes && bytes.Length >= 8)
                    {
                        var val = BitConverter.ToUInt64(bytes, 0);
                        if (val > 0)
                        {
                            vramReading = SensorReading<double>.Available(Math.Round(val / (1024d * 1024d), 0), "MB", "Registro de driver WDDM");
                        }
                    }

                    return new GpuTelemetrySnapshot
                    {
                        IsAvailable = true,
                        AdapterName = desc.Trim(),
                        AdapterRamMb = vramReading,
                        UtilizationPercent = SensorReading<double>.Unavailable(
                            SensorAvailability.UnsupportedHardware,
                            "Contador dinâmico de uso de GPU requer extensão de driver NVML/ADL/DXGI avançada.",
                            "%"),
                        TemperatureCelsius = SensorReading<double>.Unavailable(
                            SensorAvailability.UnsupportedHardware,
                            "Sensor térmico de GPU não exposto via Win32 padrão sem driver proprietário.",
                            "°C"),
                        StatusMessage = "Adaptador detectado via Windows Display Driver Registry; uso/temperatura em tempo real não simulados."
                    };
                }
            }
        }
        catch (Exception ex)
        {
            return new GpuTelemetrySnapshot
            {
                IsAvailable = false,
                AdapterName = "Indisponível",
                StatusMessage = $"Falha ao consultar adaptador GPU: {ex.Message}"
            };
        }

        return new GpuTelemetrySnapshot
        {
            IsAvailable = false,
            AdapterName = "Nenhum adaptador detectado",
            StatusMessage = "Nenhum adaptador gráfico reportado pelo registro do Windows."
        };
    }

    private static TemperatureTelemetrySnapshot CollectTemperatures()
    {
        const string reason = "Sensores térmicos ACPI/SMBus exigem suporte OEM na placa-mãe e privilégios administrativos de kernel; não disponíveis via Win32 user-mode.";
        return new TemperatureTelemetrySnapshot
        {
            IsAvailable = false,
            PrimaryCelsius = SensorReading<double>.Unavailable(SensorAvailability.PermissionRequired, reason, "°C"),
            Sensors = Array.Empty<NamedSensorReading>(),
            StatusMessage = reason
        };
    }

    private IReadOnlyList<DiskTelemetryItem> CollectDisks()
    {
        var list = new List<DiskTelemetryItem>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    list.Add(new DiskTelemetryItem
                    {
                        DriveName = drive.Name.TrimEnd('\\'),
                        VolumeLabel = drive.VolumeLabel,
                        FileSystem = drive.DriveFormat,
                        TotalBytes = drive.TotalSize,
                        AvailableFreeBytes = drive.AvailableFreeSpace
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping unavailable drive {DriveName}", drive.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed enumerating local drives.");
        }

        return list.AsReadOnly();
    }

    private IReadOnlyList<NetworkInterfaceTelemetryItem> CollectNetwork(DateTimeOffset now)
    {
        var items = new List<NetworkInterfaceTelemetryItem>();
        var elapsedSeconds = _prevNetSampleUtc.HasValue
            ? Math.Max(0.1, (now - _prevNetSampleUtc.Value).TotalSeconds)
            : 0d;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                try
                {
                    var stats = nic.GetIPStatistics();
                    var rx = stats.BytesReceived;
                    var tx = stats.BytesSent;
                    double rxRate = 0d;
                    double txRate = 0d;

                    if (elapsedSeconds > 0 && _prevNetBytes.TryGetValue(nic.Id, out var prev))
                    {
                        rxRate = rx >= prev.Rx ? Math.Round((rx - prev.Rx) / elapsedSeconds, 1) : 0d;
                        txRate = tx >= prev.Tx ? Math.Round((tx - prev.Tx) / elapsedSeconds, 1) : 0d;
                    }

                    _prevNetBytes[nic.Id] = (rx, tx);

                    items.Add(new NetworkInterfaceTelemetryItem
                    {
                        Name = nic.Name,
                        Description = nic.Description,
                        InterfaceType = nic.NetworkInterfaceType.ToString(),
                        IsUp = true,
                        LinkSpeedMbps = nic.Speed > 0 ? Math.Round(nic.Speed / 1_000_000d, 1) : 0d,
                        BytesReceived = rx,
                        BytesSent = tx,
                        RxBytesPerSec = rxRate,
                        TxBytesPerSec = txRate
                    });
                }
                catch
                {
                    // Skip adapters that reject IP statistics on this OS.
                }
            }

            _prevNetSampleUtc = now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed collecting network interface telemetry.");
        }

        return items.AsReadOnly();
    }

    private static IReadOnlyList<ProcessTelemetryItem> CollectTopProcesses(int maxProcesses)
    {
        var procs = Process.GetProcesses();
        var items = new List<ProcessTelemetryItem>(procs.Length);
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == 0) continue;
                    var wsMb = Math.Round(p.WorkingSet64 / (1024d * 1024d), 1);
                    var responding = true;
                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            responding = p.Responding;
                        }
                    }
                    catch
                    {
                        responding = true;
                    }

                    items.Add(new ProcessTelemetryItem
                    {
                        ProcessId = p.Id,
                        ProcessName = p.ProcessName,
                        WorkingSetMb = wsMb,
                        Responding = responding
                    });
                }
                catch
                {
                    // Protected/exited system processes cannot always be inspected.
                }
            }

            return items
                .OrderByDescending(i => i.WorkingSetMb)
                .Take(Math.Clamp(maxProcesses, 1, 50))
                .ToArray();
        }
        finally
        {
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }

    private static ulong ToUInt64(FILETIME ft)
        => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
