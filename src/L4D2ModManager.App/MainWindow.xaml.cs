using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using L4D2ModManager.App.Interop;
using L4D2ModManager.App.ViewModels;
using L4D2ModManager.Core.Services;

namespace L4D2ModManager.App;

public partial class MainWindow : Window
{
    private bool _acrylicApplied;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => ApplyWindowEffects();
        StateChanged += (_, _) => RaiseMaximizeChanged();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>不透明背景：关闭毛玻璃时使用，避免 DWM 每帧重算模糊带来的掉帧。</summary>
    private static Brush CreateOpaqueBackdrop()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0E, 0x0E, 0x13), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x0A, 0x0D), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x18, 0x0A, 0x0C), 1));
        brush.Freeze();
        return brush;
    }

    private void ApplyWindowEffects()
    {
        try
        {
            // 圆角 + 深色标题栏始终启用（几乎无开销）
            WindowEffects.EnableDarkTitleBar(this);
            WindowEffects.EnableRoundedCorners(this);

            var useAcrylic = ViewModel?.Library.Config.UseAcrylic ?? false;

            if (useAcrylic)
            {
                // L4D2 暗红毛玻璃
                _acrylicApplied = WindowEffects.EnableAcrylic(this, Color.FromRgb(0x14, 0x0A, 0x0C), 0xC8);
                if (_acrylicApplied)
                {
                    RootBorder.SetResourceReference(Border.BackgroundProperty, "Brush.WindowBackdrop");
                }
                else
                {
                    RootBorder.Background = CreateOpaqueBackdrop();
                }
            }
            else
            {
                WindowEffects.DisableBlur(this);
                _acrylicApplied = false;
                RootBorder.Background = CreateOpaqueBackdrop();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"应用窗口效果失败: {ex.Message}");
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        ViewModel.AcrylicChanged += ApplyWindowEffects;
        ViewModel.Settings.AcrylicChanged += OnAcrylicChangedFromSettings;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 启动时窗口整体淡入（macOS 应用启动的观感）
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        PlayWindowOpenAnimation();

        UpdateNavIndicator(animate: false);

        // 启动后自动准备缩略图通道并抓取（第一次运行会自动做一次，之后有图就跳过）
        _ = ViewModel.AutoPrepareThumbnailsAsync();

        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
        }

        UpdateNavIndicator(animate: false);

        // 启动后自动准备缩略图通道并抓取（第一次运行会自动做一次，之后有图就跳过）
        _ = ViewModel.AutoPrepareThumbnailsAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            AnimatePageChange();
            UpdateNavIndicator(animate: true);
        }
    }

    /// <summary>
    /// macOS 风格页面切换：淡入 + 轻微上移 + 极小的缩放（Launchpad 那种"卡片浮起"感）。
    /// 只使用 Opacity / RenderTransform，不触发布局。
    /// </summary>
    private void AnimatePageChange()
    {
        try
        {
            if (PageHost.RenderTransform is not TransformGroup group)
            {
                var translate = new TranslateTransform();
                var scale = new ScaleTransform(1, 1);
                group = new TransformGroup();
                group.Children.Add(scale);
                group.Children.Add(translate);
                PageHost.RenderTransform = group;
            }

            var translateTransform = (TranslateTransform)group.Children[1];
            var scaleTransform = (ScaleTransform)group.Children[0];
            PageHost.RenderTransformOrigin = new Point(0.5, 0.5);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            PageHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
            translateTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(440)) { EasingFunction = ease });
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = ease });
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = ease });
        }
        catch (Exception ex)
        {
            Log.Warn($"页面切换动画失败: {ex.Message}");
        }
    }

    /// <summary>让侧边栏指示条平滑滑到当前选中项。</summary>
    private void UpdateNavIndicator(bool animate)
    {
        try
        {
            var viewModel = ViewModel;
            if (viewModel == null) return;

            var index = viewModel.NavItems.ToList().FindIndex(n => n.IsActive);
            if (index < 0) return;

            if (NavList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                // 容器尚未生成时，等布局完成再试一次
                Dispatcher.BeginInvoke(new Action(() => UpdateNavIndicator(animate)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            var position = container.TransformToAncestor(NavHost).Transform(new Point(0, 0));
            var targetY = position.Y + (container.ActualHeight - NavIndicator.Height) / 2;

            if (!animate)
            {
                NavIndicatorTransform.Y = targetY;
                NavIndicator.Opacity = 1;
                return;
            }

            NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(480))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            NavIndicator.Opacity = 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"侧边栏指示条动画失败: {ex.Message}");
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.AcrylicChanged -= ApplyWindowEffects;
            ViewModel.Settings.AcrylicChanged -= OnAcrylicChangedFromSettings;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        ViewModel?.SaveAll();
    }

    /// <summary>设置页切换毛玻璃后，同步按钮文案并重新应用窗口效果。</summary>
    private void OnAcrylicChangedFromSettings()
    {
        ViewModel?.ReloadAcrylicFromConfig();
        ApplyWindowEffects();
    }

    // ------------------------------------------------------------------ 标题栏

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 忽略快速拖动产生的异常
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>窗口化 / 全屏切换（F11、标题栏绿色按钮）。</summary>
    private void ToggleFullScreen()
    {
        ToggleMaximize();
        PlayZoomTransition();
    }

    private void ToggleFullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;

            case Key.Escape when WindowState == WindowState.Maximized:
                ToggleMaximize();
                e.Handled = true;
                break;
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        CompensateWindowChromeClipping();
    }

    /// <summary>
    /// 修正"全屏/最大化之后顶部标题栏（关闭、最小化按钮那一行）消失"的问题。
    ///
    /// 原因：WindowStyle=None + WindowChrome 的窗口最大化时，Windows 会把窗口向外扩展
    /// 一个系统边框厚度（SM_CXSIZEFRAME + SM_CXPADDEDBORDER），WPF 不会自动补偿，
    /// 于是内容四周被裁掉 —— 顶部那条交互栏就被推到屏幕外看不见了。
    /// 处理办法：最大化时把根容器按系统边框厚度向内缩，还原时恢复圆角与零边距。
    /// </summary>
    private void CompensateWindowChromeClipping()
    {
        try
        {
            if (WindowState == WindowState.Maximized)
            {
                var frame = SystemParameters.WindowNonClientFrameThickness;
                if (frame.Left <= 0 || frame.Top <= 0 || frame.Right <= 0 || frame.Bottom <= 0)
                {
                    var resize = SystemParameters.WindowResizeBorderThickness;
                    frame = new Thickness(resize.Left, resize.Top, resize.Right, resize.Bottom);
                }

                RootBorder.Margin = new Thickness(frame.Left, frame.Top, frame.Right, frame.Bottom);
                RootBorder.CornerRadius = new CornerRadius(0);
            }
            else
            {
                RootBorder.Margin = new Thickness(0);
                RootBorder.CornerRadius = new CornerRadius(10);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"窗口布局补偿失败: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 窗口交互动画

    /// <summary>窗口打开动画：整体轻微放大 + 淡入。</summary>
    private void PlayWindowOpenAnimation()
    {
        try
        {
            RootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(0.985, 0.985);
            RootBorder.RenderTransform = scale;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(420);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
        }
        catch (Exception ex)
        {
            Log.Warn($"窗口打开动画失败: {ex.Message}");
        }
    }

    /// <summary>全屏 / 窗口化切换时的一下轻微"回弹"，让操作有反馈。</summary>
    private void PlayZoomTransition()
    {
        try
        {
            if (RootBorder.RenderTransform is not ScaleTransform scale)
            {
                RootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
                scale = new ScaleTransform(1, 1);
                RootBorder.RenderTransform = scale;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(320);
            foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            {
                var animation = new DoubleAnimation(0.994, 1, duration) { EasingFunction = ease };
                scale.BeginAnimation(property, animation);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"全屏切换动画失败: {ex.Message}");
        }
    }

    private void RaiseMaximizeChanged()
    {
        if (!_acrylicApplied) return;

        // 最大化后重新应用圆角设置（最大化时不应有圆角）
        WindowEffects.EnableRoundedCorners(this);
    }

    // ------------------------------------------------------------------ 拖放安装

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDropState(e);

    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDropState(e);

    private void Window_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void UpdateDropState(DragEventArgs e)
    {
        var hasVpk = TryGetDroppedVpkFiles(e).Count > 0;
        e.Effects = hasVpk ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropOverlay.Visibility = hasVpk ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        var files = TryGetDroppedVpkFiles(e);
        if (files.Count == 0 || ViewModel == null) return;

        var library = ViewModel.Library;
        var target = library.ResolveDownloadDirectory();
        var installed = new List<string>();
        var failed = new List<string>();

        foreach (var file in files)
        {
            var (item, message) = await library.InstallVpkAsync(file, target);
            if (item != null)
            {
                installed.Add($"{item.DisplayName}（{item.SizeText}）");
                ViewModel.Mods.Refresh();
            }
            else
            {
                failed.Add($"{Path.GetFileName(file)}：{message}");
            }
        }

        var details = (installed.Count > 0 ? "已安装：\r\n  · " + string.Join("\r\n  · ", installed) : string.Empty) +
                      (failed.Count > 0 ? "\r\n\r\n失败：\r\n  · " + string.Join("\r\n  · ", failed) : string.Empty);

        var summary = $"成功 {installed.Count} 个，失败 {failed.Count} 个\r\n保存目录：{target}";
        ViewModel.SetStatus(failed.Count > 0
            ? "拖放安装完成（部分失败）"
            : $"拖放安装完成：{installed.Count} 个 Mod 已加入列表");

        if (failed.Count > 0)
            Dialogs.DialogWindow.ShowMessage(this, "拖放安装", summary, details);
    }

    private static List<string> TryGetDroppedVpkFiles(DragEventArgs e)
    {
        var result = new List<string>();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return result;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return result;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(path, "*.vpk", SearchOption.TopDirectoryOnly));
                }
                catch
                {
                    // 忽略无法访问的目录
                }
                continue;
            }

            if (File.Exists(path) && path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                result.Add(path);
        }

        return result;
    }
}
