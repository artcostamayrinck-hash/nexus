namespace Nexus.Desktop.Services.Mvvm;

/// <summary>
/// Abstração para encaminhar atualizações de coleções observáveis, comandos e propriedades
/// vinculadas à UI com segurança para a thread de interface (ex.: WPF Dispatcher).
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Retorna <see langword="true"/> se o chamador atual estiver executando na thread da interface.
    /// </summary>
    bool CheckAccess();

    /// <summary>
    /// Executa <paramref name="action"/> diretamente se já estiver na thread da interface ou a enfileira
    /// de forma assíncrona na thread de UI quando chamado em segundo plano.
    /// </summary>
    void Enqueue(Action action);

    /// <summary>
    /// Executa <paramref name="action"/> na thread da interface de forma assíncrona sem bloquear a thread chamadora.
    /// </summary>
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementação padrão para ambientes sem loop de interface gráfica (ex.: testes unitários headless).
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static ImmediateUiDispatcher Instance { get; } = new();

    public bool CheckAccess() => true;

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}

