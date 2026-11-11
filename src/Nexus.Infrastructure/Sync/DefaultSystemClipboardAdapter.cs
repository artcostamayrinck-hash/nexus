using Nexus.Core.Errors;
using Nexus.Core.Sync;

namespace Nexus.Infrastructure.Sync;

/// <summary>
/// Headless/fallback clipboard adapter used when no platform UI adapter (WPF/MAUI) is registered.
/// Also exposes deterministic hooks for unit and integration testing without OS STA requirements.
/// </summary>
public sealed class DefaultSystemClipboardAdapter : ISystemClipboardAdapter
{
    private readonly Lock _lock = new();
    private string? _currentText;

    public bool SupportsContinuousBackgroundMonitor => false;

    public string PlatformRestrictionNotice =>
        "Sem monitor contínuo em segundo plano ativo neste adaptador; captura via ação explícita do usuário.";

    public event EventHandler<string>? SystemClipboardTextCaptured;

    public Task<Result<string?>> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(Result<string?>.Success(_currentText));
        }
    }

    public Task<Result> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _currentText = text;
        }

        return Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Simulates an external OS clipboard copy event (used in automated tests and diagnostics).
    /// </summary>
    public void SimulateUserCopy(string text)
    {
        lock (_lock)
        {
            _currentText = text;
        }

        SystemClipboardTextCaptured?.Invoke(this, text);
    }
}

