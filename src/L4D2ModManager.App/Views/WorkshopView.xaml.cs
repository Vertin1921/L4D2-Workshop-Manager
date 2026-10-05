using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using L4D2ModManager.App.ViewModels;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Workshop;
using Microsoft.Web.WebView2.Core;

namespace L4D2ModManager.App.Views;

/// <summary>
/// 创意工坊视图：内嵌 WebView2 显示 Steam 创意工坊（不打开外部浏览器）。
/// 视图负责浏览器对象，导航意图由 WorkshopViewModel 通过事件发出。
/// </summary>
public partial class WorkshopView : UserControl
{
    private WorkshopViewModel? _viewModel;
    private bool _initialized;
    private bool _initializationFailed;

    public WorkshopView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            DetachViewModel();
            _viewModel = e.NewValue as WorkshopViewModel;
            AttachViewModel();
        };

        Loaded += (_, _) => _ = InitializeBrowserAsync();
        Unloaded += (_, _) => DetachViewModel();
    }

    private void AttachViewModel()
    {
        if (_viewModel == null) return;

        _viewModel.NavigateRequested += OnNavigateRequested;
        // 注入 JS 执行器：用于在工坊页面里抓取缩略图与标签（不依赖官方 API）
        _viewModel.ScriptRunner = async script =>
        {
            var core = FindBrowser(this)?.CoreWebView2;
            return core == null ? null : await core.ExecuteScriptAsync(script);
        };

        static Microsoft.Web.WebView2.Wpf.WebView2? FindBrowser(System.Windows.DependencyObject root)
        {
            if (root is Microsoft.Web.WebView2.Wpf.WebView2 view) return view;

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindBrowser(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }

            return null;
        }
        _viewModel.BackRequested += OnBackRequested;
        _viewModel.ForwardRequested += OnForwardRequested;
        _viewModel.ReloadRequested += OnReloadRequested;
        _viewModel.CollectWorkshopIdsRequested += CollectWorkshopIdsAsync;
    }

    private void DetachViewModel()
    {
        if (_viewModel == null) return;

        _viewModel.NavigateRequested -= OnNavigateRequested;
        _viewModel.BackRequested -= OnBackRequested;
        _viewModel.ForwardRequested -= OnForwardRequested;
        _viewModel.ReloadRequested -= OnReloadRequested;
        _viewModel.CollectWorkshopIdsRequested -= CollectWorkshopIdsAsync;
    }

    /// <summary>
    /// 从当前页面 DOM 抓取所有工坊物品 ID（合集页会返回几十上百个）。
    /// 这样"下载合集"不需要额外依赖任何第三方接口。
    /// </summary>
    private async Task<IReadOnlyList<string>> CollectWorkshopIdsAsync()
    {
        var result = new List<string>();
        try
        {
            if (Browser.CoreWebView2 == null) return result;

            const string script = @"(function () {
  var set = {};
  function add(url) {
    if (!url) return;
    var m = /[?&]id=(\d{5,20})/.exec(url) || /CommunityFilePage\/(\d{5,20})/.exec(url);
    if (m) set[m[1]] = 1;
  }
  var links = document.querySelectorAll('a[href]');
  for (var i = 0; i < links.length; i++) add(links[i].href);
  add(location.href);
  return Object.keys(set).join(',');
})()";

            var json = await Browser.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);

            var text = json;
            try
            {
                text = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? string.Empty;
            }
            catch
            {
                // 返回值不是 JSON 字符串时按原样处理
            }

            result.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch (Exception ex)
        {
            Log.Warn($"抓取页面工坊 ID 失败：{ex.Message}");
        }

        return result;
    }

    private async void DownloadCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        try
        {
            await _viewModel.CollectAndDownloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("下载合集失败", ex);
            _viewModel.ReportBrowserError("下载合集失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ WebView2

    private async Task InitializeBrowserAsync()
    {
        if (_initialized || _initializationFailed || _viewModel == null) return;
        _initialized = true;

        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "L4D2ModManager", "WebView2");

            // 便携模式/受限环境下退回到程序数据目录
            try
            {
                Directory.CreateDirectory(userDataFolder);
            }
            catch
            {
                userDataFolder = Path.Combine(Core.Services.AppPaths.Root, "WebView2");
                Directory.CreateDirectory(userDataFolder);
            }

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder).ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            var core = Browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.IsZoomControlEnabled = true;

            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            // 工坊部分页面用前端路由切换，SourceChanged 能保证地址栏与当前 ID 始终同步
            core.SourceChanged += (_, _) => SyncNavigationState();
            core.DocumentTitleChanged += (_, _) => { };

            Browser.Visibility = Visibility.Visible;
            BrowserPlaceholder.Visibility = Visibility.Collapsed;

            _viewModel.SetBrowserReady(true);

            var start = string.IsNullOrWhiteSpace(_viewModel.CurrentUrl) ? AppInfo.WorkshopHomeUrl : _viewModel.CurrentUrl;
            core.Navigate(start);
        }
        catch (Exception ex)
        {
            _initializationFailed = true;
            Log.Error("初始化 WebView2 失败", ex);

            var hint = ex.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
                       ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? "未检测到 WebView2 运行时。请安装「Microsoft Edge WebView2 Runtime」（Windows 11 通常已内置），然后重启本程序。"
                : ex.Message;

            BrowserPlaceholderText.Text = "内置浏览器不可用";
            BrowserPlaceholderDetail.Text = hint;
            _viewModel?.SetBrowserReady(false, "内置浏览器不可用：" + hint);
        }
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // steam:// 等协议无法在 WebView2 中打开：改为解析工坊 ID 并获取信息
        if (!e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;

            var id = SteamWorkshopClient.ParseWorkshopId(e.Uri);
            if (id != null && _viewModel != null)
            {
                _viewModel.ManualId = id;
                _ = _viewModel.FetchCurrentInfoAsync();
            }
            else
            {
                _viewModel?.ReportBrowserError("该链接无法在程序内打开：" + e.Uri);
            }
            return;
        }

        _viewModel?.OnNavigationStarting(e.Uri);

        // 顶部的加载条动画
        var animation = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(220)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        LoadingBar.BeginAnimation(OpacityProperty, animation);
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingBar.BeginAnimation(OpacityProperty, null);
        LoadingBar.Opacity = 0;

        if (!e.IsSuccess && _viewModel != null)
            _viewModel.ReportBrowserError($"页面加载失败：{e.WebErrorStatus}");

        SyncNavigationState();
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 所有链接都在同一个 WebView 中打开，绝不弹出外部浏览器
        e.Handled = true;
        try
        {
            Browser.CoreWebView2.Navigate(e.Uri);
        }
        catch (Exception ex)
        {
            Log.Warn($"在同一视图打开链接失败: {ex.Message}");
        }
    }

    private void SyncNavigationState()
    {
        try
        {
            var core = Browser.CoreWebView2;
            if (core == null || _viewModel == null) return;

            _viewModel.OnNavigated(core.Source, core.CanGoBack, core.CanGoForward);
        }
        catch (Exception ex)
        {
            Log.Warn($"同步浏览器状态失败: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 视图模型事件

    private void OnNavigateRequested(string url)
    {
        try
        {
            if (Browser.CoreWebView2 == null)
            {
                _ = InitializeBrowserAsync();
                _viewModel?.ReportBrowserError("内置浏览器尚未就绪，请稍候重试。");
                return;
            }

            Browser.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _viewModel?.ReportBrowserError("导航失败：" + ex.Message);
        }
    }

    private void OnBackRequested()
    {
        if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void OnForwardRequested()
    {
        if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward();
    }

    private void OnReloadRequested()
    {
        try
        {
            Browser.CoreWebView2?.Reload();
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新失败: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 输入事件

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _viewModel?.GoCommand.Execute(null);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _viewModel?.SearchCommand.Execute(null);
    }
}
