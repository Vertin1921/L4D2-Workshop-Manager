using System.Windows;
using System.Windows.Threading;

namespace L4D2ModManager.App.Infrastructure;

/// <summary>跨线程更新界面的小工具。</summary>
public static class Ui
{
    private static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    /// <summary>在 UI 线程执行（若已在 UI 线程则直接执行）。</summary>
    public static void Invoke(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>异步切换到 UI 线程执行。</summary>
    public static Task InvokeAsync(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
