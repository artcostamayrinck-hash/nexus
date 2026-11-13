using System.Windows.Threading;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services;

/// <summary>
/// Implementação WPF de <see cref="IUiDispatcher"/> vinculada ao <see cref="Dispatcher"/> principal da aplicação.
/// Nunca instancia um Dispatcher secundário em threads de background.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return ExecuteOnDispatcherAsync(action, cancellationToken);
    }

    private async Task ExecuteOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        try
        {
            await _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken)
                .Task
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                            (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished))
        {
            // Encerramento normal do Dispatcher WPF durante fechamento da aplicação.
        }
    }
}

