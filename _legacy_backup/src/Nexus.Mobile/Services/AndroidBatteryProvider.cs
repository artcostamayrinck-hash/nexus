using Nexus.Contracts.Automation;
using Nexus.Core.Automation;

namespace Nexus.Mobile.Services;

/// <summary>
/// Reads genuine hardware battery state from .NET MAUI <see cref="Battery.Default"/> on Android.
/// Returns <c>IsAvailable = false</c> if hardware telemetry cannot be read, never fabricating values.
/// </summary>
public sealed class AndroidBatteryProvider : IDeviceBatteryProvider
{
    public async Task<BatteryStateSnapshot> ReadBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var level = Battery.Default.ChargeLevel;
                var state = Battery.Default.State;

                if (state == BatteryState.NotPresent)
                {
                    return new BatteryStateSnapshot
                    {
                        IsBatteryPresent = false,
                        IsAvailable = false,
                        StatusMessage = "Bateria ausente no dispositivo."
                    };
                }

                if (double.IsNaN(level) || level < 0d || level > 1d || state == BatteryState.Unknown)
                {
                    return new BatteryStateSnapshot
                    {
                        IsBatteryPresent = true,
                        IsAvailable = false,
                        StatusMessage = "Telemetria de bateria indisponível pelo sistema Android."
                    };
                }

                var percent = (int)Math.Clamp(Math.Round(level * 100d), 0, 100);
                var isCharging = state is BatteryState.Charging or BatteryState.Full;
                return new BatteryStateSnapshot
                {
                    IsBatteryPresent = true,
                    IsAvailable = true,
                    ChargePercent = percent,
                    IsCharging = isCharging,
                    StatusMessage = $"Bateria real Android: {percent}% ({state})"
                };
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new BatteryStateSnapshot
            {
                IsBatteryPresent = false,
                IsAvailable = false,
                StatusMessage = $"Não foi possível consultar bateria: {ex.Message}"
            };
        }
    }
}

