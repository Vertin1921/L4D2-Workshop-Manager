using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using L4D2ModManager.App.Interop;

namespace L4D2ModManager.App.Dialogs;

/// <summary>通用对话框：消息 / 确认 / 输入 / 下拉选择。</summary>
public partial class DialogWindow : Window
{
    private DialogWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.EnableDarkTitleBar(this);
            WindowEffects.EnableRoundedCorners(this);
        };

        // macOS sheet 风格：从标题栏下方滑入 + 淡入
        Opacity = 0;
        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(390)));
            SheetTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-24, 0, TimeSpan.FromMilliseconds(610))
                {
                    EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut },
                });
        };
    }

    /// <summary>关闭时的收拢动画（淡出 + 上移），完成后真正关闭窗口。</summary>
    private void CloseAnimated()
    {
        try
        {
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(310));
            fade.Completed += (_, _) =>
            {
                try
                {
                    Close();
                }
                catch
                {
                    // 忽略
                }
            };

            BeginAnimation(OpacityProperty, fade);
            SheetTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(SheetTransform.Y, -14, TimeSpan.FromMilliseconds(500))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                });
        }
        catch
        {
            Close();
        }
    }

    /// <summary>是否点了确定（或选择了某项）。</summary>
    public bool Accepted { get; private set; }

    public string? InputValue => InputBox.Text;

    public object? SelectedChoice => ChoiceBox.SelectedItem;

    private static DialogWindow Prepare(
        Window? owner,
        string title,
        string message,
        string? details,
        string okText,
        string cancelText,
        bool showCancel,
        bool showExtra)
    {
        var dialog = new DialogWindow
        {
            Owner = owner,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };

        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = okText;
        dialog.CancelButton.Content = cancelText;
        dialog.CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        dialog.ExtraButton.Visibility = showExtra && !string.IsNullOrWhiteSpace(details)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(details))
        {
            dialog.DetailsPanel.Visibility = Visibility.Visible;
            dialog.DetailsText.Text = details;
        }

        return dialog;
    }

    /// <summary>消息提示。</summary>
    public static bool ShowMessage(Window? owner, string title, string message, string? details = null, string okText = "知道了")
    {
        var dialog = Prepare(owner, title, message, details, okText, string.Empty, showCancel: false, showExtra: true);
        dialog.ShowDialog();
        return dialog.Accepted;
    }

    /// <summary>确认对话框。</summary>
    public static bool Confirm(Window? owner, string title, string message, string? details = null,
        string okText = "确定", string cancelText = "取消")
    {
        var dialog = Prepare(owner, title, message, details, okText, cancelText, showCancel: true, showExtra: false);
        dialog.ShowDialog();
        return dialog.Accepted;
    }

    /// <summary>文本输入。</summary>
    public static string? Prompt(Window? owner, string title, string message, string initialValue = "",
        string? label = null, string okText = "确定")
    {
        var dialog = Prepare(owner, title, message, null, okText, "取消", showCancel: true, showExtra: false);
        dialog.InputPanel.Visibility = Visibility.Visible;
        dialog.InputLabel.Text = label ?? string.Empty;
        dialog.InputLabel.Visibility = string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;
        dialog.InputBox.Text = initialValue;

        dialog.Loaded += (_, _) =>
        {
            dialog.InputBox.Focus();
            dialog.InputBox.SelectAll();
        };

        dialog.ShowDialog();
        return dialog.Accepted ? dialog.InputValue : null;
    }

    /// <summary>下拉选择。</summary>
    public static string? Choose(Window? owner, string title, string message, IEnumerable<string> choices,
        string okText = "确定")
    {
        var dialog = Prepare(owner, title, message, null, okText, "取消", showCancel: true, showExtra: false);
        dialog.ChoicePanel.Visibility = Visibility.Visible;

        var items = choices.ToList();
        foreach (var item in items) dialog.ChoiceBox.Items.Add(item);
        if (items.Count > 0) dialog.ChoiceBox.SelectedIndex = 0;

        dialog.ShowDialog();
        return dialog.Accepted ? dialog.SelectedChoice as string : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        CloseAnimated();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        CloseAnimated();
    }

    private void Extra_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailsText.Text);
            ExtraButton.Content = "已复制";
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
