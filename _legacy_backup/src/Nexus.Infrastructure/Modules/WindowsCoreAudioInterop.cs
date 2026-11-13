using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Contracts.Modules;

namespace Nexus.Infrastructure.Modules;

/// <summary>
/// Safe COM interop for Windows Core Audio master endpoint volume and mute state.
/// Returns explicit unavailability when running off Windows or without an active audio endpoint.
/// </summary>
internal static class WindowsCoreAudioInterop
{
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const int ClsCtxInprocServer = 0x1;

    public static (SensorReading<int> VolumePercent, SensorReading<bool> IsMuted) ReadAudioState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (
                SensorReading<int>.Unavailable(SensorAvailability.UnsupportedHardware, "Core Audio disponível apenas no Windows.", "%"),
                SensorReading<bool>.Unavailable(SensorAvailability.UnsupportedHardware, "Core Audio disponível apenas no Windows."));
        }

        return TryWithEndpointVolume(endpoint =>
        {
            var hrVol = endpoint.GetMasterVolumeLevelScalar(out var scalar);
            var hrMute = endpoint.GetMute(out var muted);

            var volReading = hrVol == 0
                ? SensorReading<int>.Available(Math.Clamp((int)Math.Round(scalar * 100f), 0, 100), "%", "Core Audio ativo")
                : SensorReading<int>.Unavailable(SensorAvailability.Unavailable, $"Falha Core Audio HRESULT 0x{hrVol:X8}", "%");

            var muteReading = hrMute == 0
                ? SensorReading<bool>.Available(muted, statusMessage: "Core Audio ativo")
                : SensorReading<bool>.Unavailable(SensorAvailability.Unavailable, $"Falha Core Audio HRESULT 0x{hrMute:X8}");

            return (volReading, muteReading);
        },
        reason => (
            SensorReading<int>.Unavailable(SensorAvailability.Unavailable, reason, "%"),
            SensorReading<bool>.Unavailable(SensorAvailability.Unavailable, reason)));
    }

    public static (bool Success, string Message) SetVolumePercent(int percent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "Controle de volume Core Audio disponível apenas no Windows.");
        }

        var clamped = Math.Clamp(percent, 0, 100);
        return TryWithEndpointVolume(endpoint =>
        {
            var emptyCtx = Guid.Empty;
            var hr = endpoint.SetMasterVolumeLevelScalar(clamped / 100f, ref emptyCtx);
            return hr == 0
                ? (true, $"Volume ajustado para {clamped}%.")
                : (false, $"SetMasterVolumeLevelScalar retornou 0x{hr:X8}.");
        },
        reason => (false, reason));
    }

    public static (bool Success, string Message) StepVolume(bool increase)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "Controle de volume Core Audio disponível apenas no Windows.");
        }

        return TryWithEndpointVolume(endpoint =>
        {
            var emptyCtx = Guid.Empty;
            var hr = increase
                ? endpoint.VolumeStepUp(ref emptyCtx)
                : endpoint.VolumeStepDown(ref emptyCtx);
            return hr == 0
                ? (true, increase ? "Volume aumentado." : "Volume reduzido.")
                : (false, $"StepVolume retornou 0x{hr:X8}.");
        },
        reason => (false, reason));
    }

    public static (bool Success, string Message) ToggleMute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "Controle de mudo Core Audio disponível apenas no Windows.");
        }

        return TryWithEndpointVolume(endpoint =>
        {
            var hrGet = endpoint.GetMute(out var current);
            if (hrGet != 0)
            {
                return (false, $"GetMute retornou 0x{hrGet:X8}.");
            }

            var emptyCtx = Guid.Empty;
            var next = !current;
            var hrSet = endpoint.SetMute(next, ref emptyCtx);
            return hrSet == 0
                ? (true, next ? "Áudio silenciado." : "Áudio reativado.")
                : (false, $"SetMute retornou 0x{hrSet:X8}.");
        },
        reason => (false, reason));
    }

    [SupportedOSPlatform("windows")]
    private static TResult TryWithEndpointVolume<TResult>(
        Func<IAudioEndpointVolume, TResult> action,
        Func<string, TResult> onUnavailable)
    {
        object? enumeratorObj = null;
        IMMDevice? defaultDevice = null;
        object? volumeObj = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator);
            if (type is null)
            {
                return onUnavailable("Classe COM MMDeviceEnumerator indisponível.");
            }

            enumeratorObj = Activator.CreateInstance(type);
            if (enumeratorObj is not IMMDeviceEnumerator enumerator)
            {
                return onUnavailable("Interface IMMDeviceEnumerator indisponível.");
            }

            var hrDevice = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out defaultDevice);
            if (hrDevice != 0 || defaultDevice is null)
            {
                return onUnavailable($"Nenhum dispositivo de saída de áudio ativo encontrado (0x{hrDevice:X8}).");
            }

            var iid = IidIAudioEndpointVolume;
            var hrActivate = defaultDevice.Activate(ref iid, ClsCtxInprocServer, IntPtr.Zero, out volumeObj);
            if (hrActivate != 0 || volumeObj is not IAudioEndpointVolume endpointVolume)
            {
                return onUnavailable($"IAudioEndpointVolume indisponível (0x{hrActivate:X8}).");
            }

            return action(endpointVolume);
        }
        catch (COMException ex)
        {
            return onUnavailable($"Dispositivo de áudio indisponível: {ex.Message} (0x{ex.ErrorCode:X8})");
        }
        catch (Exception ex)
        {
            return onUnavailable($"Falha ao acessar Core Audio: {ex.Message}");
        }
        finally
        {
            if (volumeObj is not null && Marshal.IsComObject(volumeObj))
            {
                Marshal.ReleaseComObject(volumeObj);
            }

            if (defaultDevice is not null && Marshal.IsComObject(defaultDevice))
            {
                Marshal.ReleaseComObject(defaultDevice);
            }

            if (enumeratorObj is not null && Marshal.IsComObject(enumeratorObj))
            {
                Marshal.ReleaseComObject(enumeratorObj);
            }
        }
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppEndpoint);

        int NotImpl2();
        int NotImpl3();
        int NotImpl4();
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            int dwClsCtx,
            IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);

        int NotImpl1();
        int NotImpl2();
        int NotImpl3();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);

        int GetMasterVolumeLevel(out float pfLevelDB);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float pfLevel);

        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);

        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid pguidEventContext);

        [PreserveSig]
        int VolumeStepDown(ref Guid pguidEventContext);

        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }
}

