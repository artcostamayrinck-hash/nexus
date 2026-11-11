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
    private readonly DispatcherTimer? _pollTimer;
    private readonly Lock _lock = new();
    private string? _lastObservedText;
    private bool _disposed;

    public WindowsClipboardAdapter()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, OnPollTick, dispatcher);
            _pollTimer.Start();
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

    private void OnPollTick(object? sender, EventArgs e)
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
        _disposed = true;
        _pollTimer?.Stop();
    }
}

