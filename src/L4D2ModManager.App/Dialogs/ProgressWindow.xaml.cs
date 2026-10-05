using System.Windows;

namespace L4D2ModManager.App.Dialogs;

/// <summary>
/// 通用进度窗口（卸载 / 安装等耗时操作）。
/// 用 ShowDialog 阻塞在调用处，进度通过 IProgress 从后台线程回传。
/// </summary>
public partial class ProgressWindow : Window
{
    public ProgressWindow(string title)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
    }

    /// <summary>更新进度（可从任意线程调用）。</summary>
    public void Report(int percent, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Report(percent, message));
            return;
        }

        var value = Math.Clamp(percent, 0, 100);
        Progress.Value = value;
        PercentText.Text = value + "%";

        if (!string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
    }

    /// <summary>请求关闭（可从任意线程调用）。</summary>
    public void RequestClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RequestClose);
            return;
        }

        try
        {
            IsHitTestVisible = false;
            Close();
        }
        catch
        {
            // 已关闭
        }
    }
}
