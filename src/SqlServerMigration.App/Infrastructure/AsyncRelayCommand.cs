using System.Windows.Input;

namespace SqlServerMigration.App.Infrastructure;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception>? _onException;
    private readonly bool _allowConcurrentExecutions;
    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null,
        Action<Exception>? onException = null,
        bool allowConcurrentExecutions = false)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onException = onException;
        _allowConcurrentExecutions = allowConcurrentExecutions;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter) =>
        (_allowConcurrentExecutions || !_isExecuting) &&
        (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        if (!_allowConcurrentExecutions)
        {
            _isExecuting = true;
            NotifyCanExecuteChanged();
        }

        try
        {
            await _execute(parameter);
        }
        catch (Exception exception)
        {
            _onException?.Invoke(exception);
        }
        finally
        {
            if (!_allowConcurrentExecutions)
            {
                _isExecuting = false;
                NotifyCanExecuteChanged();
            }
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
