using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LudoCrown.App.Mvvm;

/// <summary>Minimal INotifyPropertyChanged base (no third-party MVVM library needed).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Base for screens: busy flag and a user-facing error message.</summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _error;

    public bool IsBusy { get => _isBusy; protected set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !IsBusy;
    public string? Error { get => _error; protected set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Called by the page when it appears.</summary>
    public virtual Task OnAppearingAsync() => Task.CompletedTask;
    /// <summary>Called by the page when it disappears.</summary>
    public virtual Task OnDisappearingAsync() => Task.CompletedTask;

    protected async Task RunBusyAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try { await work(); }
        finally { IsBusy = false; }
    }
}

/// <summary>Async command that disables itself while running and reports exceptions instead of crashing.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public static Action<Exception>? UnhandledError { get; set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await _execute(parameter); }
        catch (Exception ex) { UnhandledError?.Invoke(ex); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
