using Nexus.Core.Errors;
using Nexus.Core.Sync;

namespace Nexus.Mobile.Services;

/// <summary>
/// Android .NET MAUI clipboard adapter respecting Android 10+ background clipboard restrictions:
/// does NOT promise continuous background monitoring and operates explicitly in foreground.
/// </summary>
public sealed class AndroidClipboardAdapter : ISystemClipboardAdapter
{
    public bool SupportsContinuousBackgroundMonitor => false;

    public string PlatformRestrictionNotice =>
        "Android 10+ bloqueia leitura do clipboard em segundo plano pelo sistema operacional. " +
        "A captura automática contínua em background está desativada; use o app em primeiro plano para capturar ou colar.";

#pragma warning disable CS0067 // Android OS blocks continuous background clipboard monitoring by design
    public event EventHandler<string>? SystemClipboardTextCaptured;
#pragma warning restore CS0067

    public async Task<Result<string?>> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var text = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (!Clipboard.Default.HasText)
                {
                    return null;
                }

                return await Clipboard.Default.GetTextAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);

            return Result<string?>.Success(text);
        }
        catch (Exception ex)
        {
            return Result<string?>.Failure(
                NexusError.Forbidden($"Não foi possível ler o clipboard do Android em primeiro plano: {ex.Message}", code: "ANDROID_CLIPBOARD_READ_DENIED"));
        }
    }

    public async Task<Result> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text)).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(
                NexusError.Unexpected($"Falha ao gravar no clipboard do Android: {ex.Message}", code: "ANDROID_CLIPBOARD_WRITE_FAILED", exception: ex));
        }
    }
}

