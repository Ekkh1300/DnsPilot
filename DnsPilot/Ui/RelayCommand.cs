using System;
using System.Windows.Input;

namespace DnsPilot.Ui;

/// <summary>
/// The smallest command that works: one delegate, optional CanExecute, and the raise that makes
/// a change of state reach the button.
/// </summary>
/// <remarks>
/// Two constructors, because half this app's commands act on nothing (flush, restore, the two
/// toggles) and one acts on a parameter (the group selector, which passes its key). Rather than
/// wrap every no-argument command in a lambda that ignores a parameter it never wanted, each has
/// its own shape and the compiler picks between them - a nullary lambda cannot convert to the
/// parameterised one and vice versa, so the choice is never ambiguous.
///
/// The detail that matters is raising CanExecuteChanged on the UI thread. These commands gate on
/// an async "something is running" flag, so a raise from a completed continuation would otherwise
/// arrive on a background thread and WPF would throw rather than re-query - leaving Apply
/// clickable while a change is in flight, which is how two switches interleave.
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(execute is null
                   ? throw new ArgumentNullException(nameof(execute))
                   : new Action<object?>(_ => execute()),
               canExecute)
    {
    }

    public RelayCommand(Action<object?> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null) return;

        // A command can be raised from a completed background continuation; marshalling is the
        // difference between an orderly refresh and an InvalidOperationException from WPF.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) handler(this, EventArgs.Empty);
        else dispatcher.BeginInvoke(new Action(() => handler(this, EventArgs.Empty)));
    }
}
