using System.Windows.Input;

namespace Deployer.App.Commands;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(parameter); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
