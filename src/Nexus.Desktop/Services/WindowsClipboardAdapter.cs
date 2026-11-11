using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Nexus.Core.Errors;
using Nexus.Core.Sync;

namespace Nexus.Desktop.Services;

/// <summary>
/// Windows WPF implementation of <see cref="ISystemClipboardAdapter"/> supporting foreground and
/// interactive background polling via the WPF UI dispatcher, handling COM clipboard locks gracefully.
/// </summary>
public sealed class WindowsClipboardAdapter : ISystemClipboardAdapter, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Lock _lock = new();
    private System.Windows.Interop.HwndSource? _hwndSource;
    private bool _listenerRegistered;
    private string? _lastObservedText;
    private bool _disposed;

    public WindowsClipboardAdapter()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Normal, RegisterClipboardListener);
        }
    }

    public bool SupportsContinuousBackgroundMonitor => true;

    public string PlatformRestrictionNotice =>
        "Windows suporta monitoramento contínuo da área de transferência durante sessão interativa (com bloqueio anti-eco).";

    public event EventHandler<string>? SystemClipboardTextCaptured;

    public async Task<Result<string?>> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Result<string?>.Success(null);
        }

        try
        {
            var text = await dispatcher.InvokeAsync(ReadClipboardTextSafe).Task.ConfigureAwait(false);
            return Result<string?>.Success(text);
        }
        catch (Exception ex)
        {
            return Result<string?>.Failure(
                NexusError.Unexpected($"Falha ao ler clipboard do Windows: {ex.Message}", code: "WIN_CLIPBOARD_READ_FAILED", exception: ex));
        }
    }

    public async Task<Result> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            _lastObservedText = text;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Result.Success();
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Clipboard.SetDataObject(text, copy: true);
                }
                catch (ExternalException)
                {
                    // Retry once briefly if another Windows process momentarily held the clipboard open.
                    Clipboard.SetText(text);
                }
            }).Task.ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(
                NexusError.Unexpected($"Falha ao atualizar clipboard do Windows: {ex.Message}", code: "WIN_CLIPBOARD_WRITE_FAILED", exception: ex));
        }
    }

    private void RegisterClipboardListener()
    {
        if (_disposed || _hwndSource is not null)
        {
            return;
        }

        try
        {
            var parameters = new System.Windows.Interop.HwndSourceParameters("NexusClipboardListener")
            {
                ParentWindow = HwndMessage,
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };

            var source = new System.Windows.Interop.HwndSource(parameters);
            source.AddHook(WndProc);
            if (AddClipboardFormatListener(source.Handle))
            {
                _hwndSource = source;
                _listenerRegistered = true;
            }
            else
            {
                source.RemoveHook(WndProc);
                source.Dispose();
            }
        }
        catch
        {
            // Clipboard hook registration may fail in restricted non-interactive sessions.
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && !_disposed)
        {
            OnClipboardUpdated();
        }

        return IntPtr.Zero;
    }

    private void OnClipboardUpdated()
    {
        if (_disposed)
        {
            return;
        }

        var text = ReadClipboardTextSafe();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_lock)
        {
            if (string.Equals(text, _lastObservedText, StringComparison.Ordinal))
            {
                return;
            }

            _lastObservedText = text;
        }

        SystemClipboardTextCaptured?.Invoke(this, text);
    }

    private static string? ReadClipboardTextSafe()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var source = Interlocked.Exchange(ref _hwndSource, null);
        if (source is null)
        {
            return;
        }

        void Cleanup()
        {
            if (_listenerRegistered && source.Handle != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(source.Handle);
                _listenerRegistered = false;
            }

            source.RemoveHook(WndProc);
            source.Dispose();
        }

        if (source.Dispatcher.CheckAccess())
        {
            Cleanup();
        }
        else
        {
            source.Dispatcher.BeginInvoke(Cleanup);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

