using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace L4D2ModManager.App.Infrastructure;

/// <summary>MVVM 基类：INotifyPropertyChanged 的轻量实现。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    /// <summary>设置字段并同时通知若干派生属性。</summary>
    protected bool Set<T>(ref T field, T value, string[] alsoNotify, [CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName)) return false;
        Raise(alsoNotify);
        return true;
    }
}

/// <summary>
/// 同步命令。为避免委托重载歧义，这里显式提供四种构造形式。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute) : this(execute, (Func<object?, bool>?)null)
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute) : this(_ => execute(), (Func<object?, bool>?)null)
    {
    }

    public RelayCommand(Action execute, Func<bool>? canExecute)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    /// <summary>执行委托带参数、可用性委托无参数。</summary>
    public RelayCommand(Action<object?> execute, Func<bool>? canExecute)
        : this(execute, canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>异步命令：执行期间自动禁用，异常交给错误处理器。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute) : this(execute, null, null)
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute)
        : this(execute, canExecute, null)
    {
    }

    /// <summary>带错误处理器（第二个参数是异常处理器，不是可用性判断）。</summary>
    public AsyncRelayCommand(Func<object?, Task> execute, Action<Exception> onError)
        : this(execute, null, onError)
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute, Action<Exception>? onError)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public AsyncRelayCommand(Func<Task> execute) : this(_ => execute(), null, null)
    {
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute(), null)
    {
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute, Action<Exception>? onError)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute(), onError)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        IsRunning = true;
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不算错误
        }
        catch (Exception ex)
        {
            if (_onError != null) _onError(ex);
            else System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
