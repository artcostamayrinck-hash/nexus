using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Nexus.Mobile.Services.Mvvm;

public abstract class MobileObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MobileAsyncCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private int _running;

    public MobileAsyncCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        NotifyCanExecuteChanged();
        try
        {
            await _execute(cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class MobileAsyncCommand<T> : ICommand
{
    private readonly Func<T?, CancellationToken, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private int _running;

    public MobileAsyncCommand(Func<T?, CancellationToken, Task> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (Volatile.Read(ref _running) == 1)
        {
            return false;
        }

        return _canExecute?.Invoke(parameter is T typed ? typed : default) ?? true;
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter is T typed ? typed : default);
    }

    public async Task ExecuteAsync(T? parameter = default, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(parameter, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

