using System.Runtime.InteropServices;
using Nexus.Contracts.Automation;
using Nexus.Core.Automation;

namespace Nexus.Infrastructure.Automation;

/// <summary>
/// Reads genuine battery/AC state on Windows using Win32 GetSystemPowerStatus.
/// Explicitly reports absence of battery on desktops/VMs rather than fabricating percentages.
/// </summary>
public sealed class WindowsDeviceBatteryProvider : IDeviceBatteryProvider
{
    public Task<BatteryStateSnapshot> ReadBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new BatteryStateSnapshot
            {
                IsBatteryPresent = false,
                IsAvailable = false,
                StatusMessage = "Leitura nativa Win32 GetSystemPowerStatus disponível apenas no Windows."
            });
        }

        if (!GetSystemPowerStatus(out var status))
        {
            var err = Marshal.GetLastWin32Error();
            return Task.FromResult(new BatteryStateSnapshot
            {
                IsBatteryPresent = false,
                IsAvailable = false,
                StatusMessage = $"GetSystemPowerStatus falhou (Win32Error={err})."
            });
        }

        if ((status.BatteryFlag & 128) != 0 || status.BatteryFlag == 255 || status.BatteryLifePercent > 100)
        {
            return Task.FromResult(new BatteryStateSnapshot
            {
                IsBatteryPresent = false,
                IsAvailable = false,
                IsCharging = status.ACLineStatus == 1,
                StatusMessage = "Nenhuma bateria detectada neste computador (energia AC / desktop)."
            });
        }

        var isCharging = status.ACLineStatus == 1 || (status.BatteryFlag & 8) != 0;
        return Task.FromResult(new BatteryStateSnapshot
        {
            IsBatteryPresent = true,
            IsAvailable = true,
            ChargePercent = status.BatteryLifePercent,
            IsCharging = isCharging,
            StatusMessage = isCharging
                ? $"Bateria em {status.BatteryLifePercent}% (conectada à tomada)"
                : $"Bateria em {status.BatteryLifePercent}%"
        });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}

