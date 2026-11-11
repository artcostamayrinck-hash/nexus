using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Modules;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Modules;

namespace Nexus.Infrastructure.Modules;

public sealed class WindowsControlOptions
{
    /// <summary>
    /// Safety guard for automated tests so Lock/Sleep/Shutdown commands verify confirmation and routing without altering developer workstation power state.
    /// </summary>
    public bool DryRunPowerActions { get; set; }

    /// <summary>
    /// Optional test hook only when running unit tests outside an interactive Windows desktop session.
    /// Disabled by default so real Win32 SendInput/CoreAudio APIs execute directly.
    /// </summary>
    public bool SimulateInputInHeadlessTests { get; set; }

    /// <summary>
    /// Safety guard for automated tests so LaunchApprovedApp validates allowlist without spawning GUI applications.
    /// </summary>
    public bool DryRunApprovedAppLaunches { get; set; }
}

/// <summary>
/// Executes real Windows input, shortcut, volume, media key, and system power actions
/// using Win32 SendInput, Core Audio COM, LockWorkStation, and SetSuspendState.
/// Never fabricates success when an OS API fails or when module/confirmation checks block execution.
/// </summary>
public sealed class WindowsControlExecutor : IWindowsControlExecutor
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;

    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private static readonly Dictionary<string, ushort> VirtualKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BACKSPACE"] = 0x08, ["BACK"] = 0x08,
        ["TAB"] = 0x09,
        ["ENTER"] = 0x0D, ["RETURN"] = 0x0D,
        ["SHIFT"] = 0x10, ["LSHIFT"] = 0xA0,
        ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["LCTRL"] = 0xA2,
        ["ALT"] = 0x12, ["MENU"] = 0x12,
        ["PAUSE"] = 0x13,
        ["CAPSLOCK"] = 0x14,
        ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20,
        ["PAGEUP"] = 0x21, ["PGUP"] = 0x21,
        ["PAGEDOWN"] = 0x22, ["PGDN"] = 0x22,
        ["END"] = 0x23,
        ["HOME"] = 0x24,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
        ["PRINTSCREEN"] = 0x2C,
        ["INSERT"] = 0x2D,
        ["DELETE"] = 0x2E, ["DEL"] = 0x2E,
        ["WIN"] = 0x5B, ["LWIN"] = 0x5B, ["SUPER"] = 0x5B,
        ["VOLUME_MUTE"] = 0xAD, ["VOLUMEMUTE"] = 0xAD,
        ["VOLUME_DOWN"] = 0xAE, ["VOLUMEDOWN"] = 0xAE,
        ["VOLUME_UP"] = 0xAF, ["VOLUMEUP"] = 0xAF,
        ["MEDIA_NEXT"] = 0xB0, ["MEDIANEXT"] = 0xB0,
        ["MEDIA_PREV"] = 0xB1, ["MEDIAPREV"] = 0xB1,
        ["MEDIA_STOP"] = 0xB2, ["MEDIASTOP"] = 0xB2,
        ["MEDIA_PLAY_PAUSE"] = 0xB3, ["MEDIAPLAYPAUSE"] = 0xB3,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B
    };

    private readonly IModuleSettingsService _settingsService;
    private readonly ILocalDeviceContext _hostIdentity;
    private readonly WindowsControlOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<WindowsControlExecutor> _logger;

    public WindowsControlExecutor(
        IModuleSettingsService settingsService,
        ILocalDeviceContext hostIdentity,
        WindowsControlOptions options,
        IClock clock,
        ILogger<WindowsControlExecutor> logger)
    {
        _settingsService = settingsService;
        _hostIdentity = hostIdentity;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public bool IsSupportedOnCurrentOs => OperatingSystem.IsWindows() || _options.SimulateInputInHeadlessTests;

    public async Task<Result<RemoteControlExecutionReceipt>> ExecuteAsync(
        RemoteControlCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationErrors = request.Validate().ToArray();
        if (validationErrors.Length > 0)
        {
            return Result<RemoteControlExecutionReceipt>.Failure(
                NexusError.Validation(validationErrors[0].Message, code: validationErrors[0].Code));
        }

        var settingsRes = await _settingsService.GetControlSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsFailure)
        {
            return Result<RemoteControlExecutionReceipt>.Failure(settingsRes.Error!);
        }

        var settings = settingsRes.Value;
        if (!settings.IsEnabled)
        {
            return Result<RemoteControlExecutionReceipt>.Success(Receipt(
                request,
                executed: false,
                requiresConfirmation: false,
                errorCode: "MODULE_DISABLED",
                message: "Nexus Control está desativado nas configurações locais."));
        }

        var isPowerAction = RemoteControlCommandRequest.IsHighImpactAction(request.Action);
        if (isPowerAction)
        {
            if (!settings.AllowPowerActions)
            {
                return Result<RemoteControlExecutionReceipt>.Success(Receipt(
                    request,
                    executed: false,
                    requiresConfirmation: false,
                    errorCode: "POWER_ACTIONS_DISABLED",
                    message: "Ações de energia estão desabilitadas pelo host."));
            }

            if (settings.RequirePowerActionConfirmation && !request.ConfirmedByUser)
            {
                return Result<RemoteControlExecutionReceipt>.Success(Receipt(
                    request,
                    executed: false,
                    requiresConfirmation: true,
                    errorCode: "CONFIRMATION_REQUIRED",
                    message: $"A ação '{request.Action}' exige confirmação explícita antes da execução."));
            }
        }

        if (!IsSupportedOnCurrentOs)
        {
            return Result<RemoteControlExecutionReceipt>.Success(Receipt(
                request,
                executed: false,
                requiresConfirmation: false,
                errorCode: "OS_NOT_SUPPORTED",
                message: "A execução de entrada nativa e energia só é suportada no Windows."));
        }

        if (_options.SimulateInputInHeadlessTests || !OperatingSystem.IsWindows())
        {
            if (_options.SimulateInputInHeadlessTests)
            {
                return Result<RemoteControlExecutionReceipt>.Success(Receipt(
                    request,
                    executed: true,
                    requiresConfirmation: false,
                    errorCode: null,
                    message: $"Ação '{request.Action}' validada (modo headless de teste)."));
            }

            return Result<RemoteControlExecutionReceipt>.Success(Receipt(
                request,
                executed: false,
                requiresConfirmation: false,
                errorCode: "OS_NOT_SUPPORTED",
                message: "A execução de entrada nativa e energia só é suportada no Windows."));
        }

        var (ok, errorCode, message) = ExecuteNativeWindowsAction(request, settings);
        if (!ok)
        {
            _logger.LogWarning("Nexus Control failed executing {Action}: {Code} - {Message}", request.Action, errorCode, message);
        }

        return Result<RemoteControlExecutionReceipt>.Success(Receipt(request, ok, false, errorCode, message));
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) ExecuteNativeWindowsAction(
        RemoteControlCommandRequest request,
        ControlModuleSettings settings)
    {
        return request.Action switch
        {
            RemoteControlActionType.MouseMove => MoveCursorAbsolute(request.DeltaX, request.DeltaY),
            RemoteControlActionType.MouseRelativeMove => MoveCursorRelative(request.DeltaX, request.DeltaY),
            RemoteControlActionType.MouseLeftClick => SendMouseButtonClick(MouseEventLeftDown, MouseEventLeftUp, "Clique esquerdo"),
            RemoteControlActionType.MouseRightClick => SendMouseButtonClick(MouseEventRightDown, MouseEventRightUp, "Clique direito"),
            RemoteControlActionType.MouseMiddleClick => SendMouseButtonClick(MouseEventMiddleDown, MouseEventMiddleUp, "Clique do meio"),
            RemoteControlActionType.MouseDoubleClick => SendMouseDoubleClick(),
            RemoteControlActionType.MouseScroll => SendMouseWheel(request.ScrollDelta),
            RemoteControlActionType.KeyPress => SendSingleKey(request.KeyName!),
            RemoteControlActionType.TextInput => SendUnicodeText(request.Text!),
            RemoteControlActionType.Hotkey => ExecuteHotkey(request, settings),
            RemoteControlActionType.VolumeUp => StepVolumeWithFallback(true),
            RemoteControlActionType.VolumeDown => StepVolumeWithFallback(false),
            RemoteControlActionType.VolumeMuteToggle => ToggleMuteWithFallback(),
            RemoteControlActionType.VolumeSet => SetVolumeExact(request.VolumePercent!.Value),
            RemoteControlActionType.MediaPlayPause => SendVirtualKeyCode(0xB3, "Play/Pause de mídia"),
            RemoteControlActionType.MediaNext => SendVirtualKeyCode(0xB0, "Próxima faixa"),
            RemoteControlActionType.MediaPrevious => SendVirtualKeyCode(0xB1, "Faixa anterior"),
            RemoteControlActionType.MediaStop => SendVirtualKeyCode(0xB2, "Parar mídia"),
            RemoteControlActionType.PowerLock => ExecutePowerLock(),
            RemoteControlActionType.PowerSleep => ExecutePowerSleep(),
            RemoteControlActionType.PowerShutdown => ExecutePowerShutdown(),
            _ => (false, "UNSUPPORTED_ACTION", $"Ação '{request.Action}' não suportada.")
        };
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) MoveCursorAbsolute(int x, int y)
    {
        if (SetCursorPos(x, y))
        {
            return (true, null, $"Cursor posicionado em ({x}, {y}).");
        }

        return Win32Failure("WIN32_CURSOR_FAILED", "SetCursorPos");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) MoveCursorRelative(int dx, int dy)
    {
        if (GetCursorPos(out var current))
        {
            var nx = current.X + dx;
            var ny = current.Y + dy;
            if (SetCursorPos(nx, ny))
            {
                return (true, null, $"Cursor movido em ({dx}, {dy}).");
            }
        }

        var input = new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    dwFlags = MouseEventMove
                }
            }
        };

        return DispatchInputs([input], "Movimento relativo de mouse");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) SendMouseButtonClick(uint downFlag, uint upFlag, string label)
    {
        var inputs = new[]
        {
            new INPUT { type = InputMouse, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = downFlag } } },
            new INPUT { type = InputMouse, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = upFlag } } }
        };
        return DispatchInputs(inputs, label);
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) SendMouseDoubleClick()
    {
        var inputs = new[]
        {
            new INPUT { type = InputMouse, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MouseEventLeftDown } } },
            new INPUT { type = InputMouse, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MouseEventLeftUp } } },
            new INPUT { type = InputMouse, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MouseEventLeftDown } } },
            new INPUT { type = InputMouse, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MouseEventLeftUp } } }
        };
        return DispatchInputs(inputs, "Duplo clique esquerdo");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) SendMouseWheel(int scrollDelta)
    {
        var delta = scrollDelta == 0 ? 120 : scrollDelta;
        var input = new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = MouseEventWheel
                }
            }
        };
        return DispatchInputs([input], $"Rolagem de mouse ({delta})");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) SendSingleKey(string keyName)
    {
        if (!TryResolveVirtualKey(keyName, out var vk))
        {
            return (false, "UNKNOWN_KEY", $"Tecla '{keyName}' não reconhecida para mapeamento Win32.");
        }

        return SendVirtualKeyCode(vk, $"Tecla '{keyName}'");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) SendVirtualKeyCode(ushort vk, string label)
    {
        var inputs = new[]
        {
            new INPUT { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KeyEventKeyUp } } }
        };
        return DispatchInputs(inputs, label);
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) SendUnicodeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyEventUnicode } }
            });
            inputs.Add(new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyEventUnicode | KeyEventKeyUp } }
            });
        }

        return DispatchInputs(inputs.ToArray(), $"Entrada de texto ({text.Length} caractere(s))");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) ExecuteHotkey(
        RemoteControlCommandRequest request,
        ControlModuleSettings settings)
    {
        IReadOnlyList<string>? keys = request.Keys;
        if (!string.IsNullOrWhiteSpace(request.HotkeyId))
        {
            var found = settings.Hotkeys.FirstOrDefault(h =>
                string.Equals(h.HotkeyId, request.HotkeyId, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                keys = found.Keys;
            }
        }

        if (keys is null || keys.Count == 0)
        {
            return (false, "HOTKEY_NOT_FOUND", $"Atalho '{request.HotkeyId}' não encontrado ou vazio.");
        }

        var vks = new List<ushort>(keys.Count);
        foreach (var k in keys)
        {
            if (!TryResolveVirtualKey(k, out var vk))
            {
                return (false, "UNKNOWN_KEY", $"Tecla '{k}' do atalho não é suportada.");
            }
            vks.Add(vk);
        }

        var inputs = new List<INPUT>(vks.Count * 2);
        foreach (var vk in vks)
        {
            inputs.Add(new INPUT { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } });
        }

        for (var i = vks.Count - 1; i >= 0; i--)
        {
            inputs.Add(new INPUT { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vks[i], dwFlags = KeyEventKeyUp } } });
        }

        return DispatchInputs(inputs.ToArray(), $"Atalho [{string.Join("+", keys)}]");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) StepVolumeWithFallback(bool increase)
    {
        var audio = WindowsCoreAudioInterop.StepVolume(increase);
        if (audio.Success)
        {
            return (true, null, audio.Message);
        }

        var vkRes = SendVirtualKeyCode(increase ? (ushort)0xAF : (ushort)0xAE, increase ? "Volume +" : "Volume -");
        return vkRes.Ok
            ? (true, null, vkRes.Message)
            : (false, "AUDIO_CONTROL_FAILED", $"{audio.Message} | Fallback SendInput falhou: {vkRes.Message}");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) ToggleMuteWithFallback()
    {
        var audio = WindowsCoreAudioInterop.ToggleMute();
        if (audio.Success)
        {
            return (true, null, audio.Message);
        }

        var vkRes = SendVirtualKeyCode(0xAD, "Alternar mudo");
        return vkRes.Ok
            ? (true, null, vkRes.Message)
            : (false, "AUDIO_CONTROL_FAILED", $"{audio.Message} | Fallback SendInput falhou: {vkRes.Message}");
    }

    private static (bool Ok, string? ErrorCode, string Message) SetVolumeExact(int volumePercent)
    {
        var audio = WindowsCoreAudioInterop.SetVolumePercent(volumePercent);
        return audio.Success
            ? (true, null, audio.Message)
            : (false, "AUDIO_CONTROL_FAILED", audio.Message);
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) ExecutePowerLock()
    {
        if (_options.DryRunPowerActions)
        {
            return (true, null, "Bloqueio da estação confirmado (modo seguro de simulação/teste).");
        }

        return LockWorkStation()
            ? (true, null, "Estação Windows bloqueada.")
            : Win32Failure("POWER_LOCK_FAILED", "LockWorkStation");
    }

    [SupportedOSPlatform("windows")]
    private (bool Ok, string? ErrorCode, string Message) ExecutePowerSleep()
    {
        if (_options.DryRunPowerActions)
        {
            return (true, null, "Suspensão confirmada (modo seguro de simulação/teste).");
        }

        return SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false)
            ? (true, null, "Comando de suspensão enviado ao Windows.")
            : Win32Failure("POWER_SLEEP_FAILED", "SetSuspendState");
    }

    private (bool Ok, string? ErrorCode, string Message) ExecutePowerShutdown()
    {
        if (_options.DryRunPowerActions)
        {
            return (true, null, "Desligamento confirmado (modo seguro de simulação/teste).");
        }

        try
        {
            var psi = new ProcessStartInfo("shutdown.exe", "/s /t 5")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            return proc is not null
                ? (true, null, "Desligamento do Windows agendado em 5 segundos.")
                : (false, "POWER_SHUTDOWN_FAILED", "Não foi possível iniciar shutdown.exe.");
        }
        catch (Exception ex)
        {
            return (false, "POWER_SHUTDOWN_FAILED", $"Falha ao iniciar shutdown.exe: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (bool Ok, string? ErrorCode, string Message) DispatchInputs(INPUT[] inputs, string label)
    {
        if (inputs.Length == 0)
        {
            return (false, "EMPTY_INPUT", "Nenhum evento de entrada para enviar.");
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            return (true, null, $"{label} executado via SendInput.");
        }

        var err = Marshal.GetLastWin32Error();
        return (false, "WIN32_SENDINPUT_FAILED",
            $"SendInput enviou {sent}/{inputs.Length} evento(s) para '{label}' (Win32Error={err}; desktop pode estar bloqueado ou isolado).");
    }

    private static (bool Ok, string ErrorCode, string Message) Win32Failure(string code, string apiName)
    {
        var err = Marshal.GetLastWin32Error();
        return (false, code, $"{apiName} falhou com código Win32 {err}.");
    }

    private static bool TryResolveVirtualKey(string keyName, out ushort vk)
    {
        var trimmed = keyName.Trim();
        if (VirtualKeyMap.TryGetValue(trimmed, out vk))
        {
            return true;
        }

        if (trimmed.Length == 1)
        {
            var ch = char.ToUpperInvariant(trimmed[0]);
            if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
            {
                vk = ch;
                return true;
            }
        }

        vk = 0;
        return false;
    }

    private RemoteControlExecutionReceipt Receipt(
        RemoteControlCommandRequest request,
        bool executed,
        bool requiresConfirmation,
        string? errorCode,
        string message)
        => new()
        {
            CommandId = request.CommandId,
            HostDeviceId = _hostIdentity.DeviceId,
            Action = request.Action,
            Executed = executed,
            RequiresConfirmation = requiresConfirmation,
            ErrorCode = errorCode,
            Message = message,
            ExecutedAtUtc = _clock.UtcNow
        };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
