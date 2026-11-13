using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Nexus.Desktop.Services.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    private readonly IUiDispatcher _uiDispatcher;

    protected ObservableObject(IUiDispatcher? uiDispatcher = null)
    {
        _uiDispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
    }

    protected IUiDispatcher UiDispatcher => _uiDispatcher;

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
    {
        if (_uiDispatcher.CheckAccess())
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return;
        }

        _uiDispatcher.Enqueue(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }

    /// <summary>
    /// Executa mutações de estado/coleções garantidamente na thread da UI.
    /// </summary>
    protected void RunOnUi(Action action) => _uiDispatcher.Enqueue(action);

    /// <summary>
    /// Aguarda de forma assíncrona (sem bloquear) a aplicação de mutações de UI na thread da interface.
    /// </summary>
    protected Task RunOnUiAsync(Action action, CancellationToken cancellationToken = default)
        => _uiDispatcher.InvokeAsync(action, cancellationToken);

    /// <summary>
    /// Substitui itens de uma <see cref="ObservableCollection{T}"/> preservando a instância vinculada ao WPF.
    /// Deve ser invocada dentro de <see cref="RunOnUi"/> ou <see cref="RunOnUiAsync"/>.
    /// </summary>
    protected static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(items);

        var snapshot = items as IReadOnlyList<T> ?? items.ToArray();
        target.Clear();
        foreach (var item in snapshot)
        {
            target.Add(item);
        }
    }

    /// <summary>
    /// Insere um item no topo de uma coleção mantendo limite máximo de elementos.
    /// Deve ser invocada dentro de <see cref="RunOnUi"/> ou <see cref="RunOnUiAsync"/>.
    /// </summary>
    protected static void PrependCapped<T>(ObservableCollection<T> target, T item, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Insert(0, item);
        while (target.Count > maxCount)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    private readonly IUiDispatcher _uiDispatcher;

    public RelayCommand(Action execute, Func<bool>? canExecute = null, IUiDispatcher? uiDispatcher = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _uiDispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();

    public void NotifyCanExecuteChanged()
    {
        if (_uiDispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _uiDispatcher.Enqueue(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly IUiDispatcher _uiDispatcher;
    private int _isRunning;

    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _uiDispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            return;
        }

        NotifyCanExecuteChanged();
        try
        {
            await _execute(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged()
    {
        if (_uiDispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _uiDispatcher.Enqueue(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, CancellationToken, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private readonly IUiDispatcher _uiDispatcher;
    private int _isRunning;

    public AsyncRelayCommand(
        Func<T?, CancellationToken, Task> execute,
        Func<T?, bool>? canExecute = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _uiDispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool CanExecute(object? parameter)
    {
        if (IsRunning)
        {
            return false;
        }

        return _canExecute?.Invoke(parameter is T typed ? typed : default) ?? true;
    }

    public async void Execute(object? parameter) => await ExecuteAsync(parameter is T typed ? typed : default);

    public async Task ExecuteAsync(T? parameter = default, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            return;
        }

        NotifyCanExecuteChanged();
        try
        {
            await _execute(parameter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged()
    {
        if (_uiDispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _uiDispatcher.Enqueue(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}

