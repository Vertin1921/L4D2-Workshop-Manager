using L4D2ModManager.App.Infrastructure;
using L4D2ModManager.App.Services;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.App.ViewModels;

/// <summary>侧边栏导航项。</summary>
public sealed class NavItem : ObservableObject
{
    private bool _isActive;

    public NavItem(string icon, string title, string subtitle, object page)
    {
        Icon = icon;
        Title = title;
        Subtitle = subtitle;
        Page = page;
    }

    public string Icon { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public object Page { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!Set(ref _isActive, value)) return;
            Raise(nameof(ActiveTag));
        }
    }

    /// <summary>供 XAML 触发器使用（Tag="Active"）。</summary>
    public string ActiveTag => _isActive ? "Active" : string.Empty;
}

/// <summary>主窗口视图模型：导航 + 状态栏 + 全局命令。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private NavItem _selectedNav;
    private string _statusMessage = "就绪";
    private bool _isAcrylicEnabled = true;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Library = services.Library;

        Mods = new ModsViewModel(services);
        Workshop = new WorkshopViewModel(services);
        Downloads = new DownloadsViewModel(services);
        Conflicts = new ConflictsViewModel(services);
        Profiles = new ProfilesViewModel(services);
        Settings = new SettingsViewModel(services);

        NavItems = new List<NavItem>
        {
            new("🎮", "Mod 管理", "扫描 / 启停 / 排序", Mods),
            new("🛒", "创意工坊", "程序内浏览与下载", Workshop),
            new("⬇", "下载管理", "进度 / 暂停 / 重试", Downloads),
            new("⚠", "冲突检测", "重复文件与覆盖", Conflicts),
            new("🗂", "配置方案", "一键切换启停组合", Profiles),
            new("⚙", "设置", "目录 / 选项 / 诊断", Settings),
        };

        _selectedNav = NavItems[0];
        _selectedNav.IsActive = true;
        _isAcrylicEnabled = Library.Config.UseAcrylic;

        ScanAllCommand = new AsyncRelayCommand(async () => await Mods.ScanAsync().ConfigureAwait(true),
            () => !Mods.IsBusy, ex => ReportError("扫描失败", ex));
        DetectConflictsCommand = new AsyncRelayCommand(async () => await Conflicts.DetectAsync().ConfigureAwait(true),
            () => !Conflicts.IsBusy, ex => ReportError("冲突检测失败", ex));
        LaunchGameCommand = new AsyncRelayCommand(
            () => { LaunchGame(insecure: false); return Task.CompletedTask; },
            () => true, ex => ReportError("启动游戏失败", ex));
        LaunchGameInsecureCommand = new AsyncRelayCommand(
            () => { LaunchGame(insecure: true); return Task.CompletedTask; },
            () => true, ex => ReportError("启动游戏失败", ex));
        OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.Root));
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogDir));
        OpenDownloadFolderCommand = new RelayCommand(_ => OpenFolder(Library.ResolveDownloadDirectory()));
        ToggleAcrylicCommand = new RelayCommand(_ => IsAcrylicEnabled = !IsAcrylicEnabled);
        SaveCommand = new RelayCommand(_ =>
        {
            Library.SaveConfig();
            Library.SaveDatabase(force: true);
            StatusMessage = "配置与数据库已保存";
        });
        SelectNavCommand = new RelayCommand(p =>
        {
            if (p is NavItem item) SelectedNav = item;
        });
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());
        DismissElevationCommand = new RelayCommand(_ =>
        {
            Library.Config.ElevationDeclined = true;
            Library.SaveConfig();
            Raise(nameof(ShowElevationBanner));
        });
        AddVpkFilesCommand = new AsyncRelayCommand(AddVpkFilesAsync, () => !Mods.IsBusy, ex => ReportError("添加 Mod 文件失败", ex));
        OpenWebView2DownloadCommand = new RelayCommand(_ => OpenInBrowser(
            "https://developer.microsoft.com/microsoft-edge/webview2/consumer/"));

        // 下载任务加入后自动跳到下载页
        Mods.DownloadQueued += _ =>
        {
            Downloads.Refresh();
            SelectedNav = NavItems.First(n => n.Page is DownloadsViewModel);
        };

        Library.StatusChanged += (_, message) => Ui.InvokeAsync(() =>
        {
            StatusMessage = message;
            RaiseSummary();
        });

        Library.ModsChanged += (_, _) => Ui.InvokeAsync(RaiseSummary);

        Library.ConflictsUpdated += (_, _) => Ui.InvokeAsync(() =>
        {
            Raise(nameof(ConflictBadgeText));
            RaiseSummary();
        });
    }

    public ModLibraryService Library { get; }

    public ModsViewModel Mods { get; }

    public WorkshopViewModel Workshop { get; }

    public DownloadsViewModel Downloads { get; }

    public ConflictsViewModel Conflicts { get; }

    public ProfilesViewModel Profiles { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    public AsyncRelayCommand ScanAllCommand { get; }

    public AsyncRelayCommand DetectConflictsCommand { get; }

    /// <summary>启动游戏（正常模式）。</summary>
    public AsyncRelayCommand LaunchGameCommand { get; }

    /// <summary>以 -insecure 启动游戏（关闭 VAC，联机 Mod / 测试用）。</summary>
    public AsyncRelayCommand LaunchGameInsecureCommand { get; }

    private string _launchStatusText = string.Empty;

    /// <summary>启动游戏等操作的反馈文字（显示在侧边栏启动按钮下方）。</summary>
    public string LaunchStatusText
    {
        get => _launchStatusText;
        private set => Set(ref _launchStatusText, value);
    }

    /// <summary>启动 Left 4 Dead 2。若已安装 Steam 则通过 Steam 启动（保留覆盖层与游戏时间）。</summary>
    public string LaunchGameToolTip =>
        "启动 Left 4 Dead 2。已安装 Steam 时通过 Steam 启动（保留覆盖层与游戏时间）。";

    /// <summary>启动游戏；insecure = 附加 -insecure（关闭 VAC）。</summary>
    private void LaunchGame(bool insecure)
    {
        var arguments = Library.Config.LaunchArguments ?? string.Empty;
        var options = new L4D2ModManager.Core.Services.Steam.GameLaunchOptions
        {
            Insecure = insecure || arguments.Contains("-insecure", StringComparison.OrdinalIgnoreCase),
            ExtraArguments = arguments,
            PreferSteam = Library.Config.LaunchWithSteam,
        };

        // -insecure 会关闭 VAC，先提醒一次（可在设置里取消这个确认）
        if (options.Insecure && Library.Config.ConfirmInsecureLaunch &&
            !_services.Dialogs.Confirm(
                L4D2ModManager.Core.Services.Steam.GameLauncher.InsecureWarning,
                "-insecure 启动",
                "确定要以 -insecure 启动游戏吗？",
                "以 -insecure 启动"))
        {
            LaunchStatusText = "已取消启动";
            return;
        }

        var result = L4D2ModManager.Core.Services.Steam.GameLauncher.Launch(Library.SteamPaths, options);

        if (result.Success)
        {
            LaunchStatusText = $"已启动游戏（{(options.Insecure ? "-insecure" : "正常")}）";
            Log.Info($"启动游戏：{result.CommandLine}");
            return;
        }

        _services.Dialogs.Error(result.Error ?? "启动游戏失败。", "启动游戏", result.CommandLine);
    }

    /// <summary>设置页里的"启动参数"文本。</summary>
    public string LaunchArgumentsText
    {
        get => Library.Config.LaunchArguments;
        set
        {
            var text = value ?? string.Empty;
            if (Library.Config.LaunchArguments == text) return;
            Library.Config.LaunchArguments = text;
            Raise();
            Library.SaveConfig();
        }
    }

    public bool LaunchWithSteam
    {
        get => Library.Config.LaunchWithSteam;
        set
        {
            if (Library.Config.LaunchWithSteam == value) return;
            Library.Config.LaunchWithSteam = value;
            Raise();
            Library.SaveConfig();
        }
    }

    public bool ConfirmInsecureLaunch
    {
        get => Library.Config.ConfirmInsecureLaunch;
        set
        {
            if (Library.Config.ConfirmInsecureLaunch == value) return;
            Library.Config.ConfirmInsecureLaunch = value;
            Raise();
            Library.SaveConfig();
        }
    }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenDownloadFolderCommand { get; }

    public RelayCommand ToggleAcrylicCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand SelectNavCommand { get; }

    public RelayCommand RestartElevatedCommand { get; }

    public RelayCommand DismissElevationCommand { get; }

    public AsyncRelayCommand AddVpkFilesCommand { get; }

    public RelayCommand OpenWebView2DownloadCommand { get; }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value == null || !Set(ref _selectedNav, value)) return;

            foreach (var item in NavItems) item.IsActive = ReferenceEquals(item, value);
            _services.Thumbnails.Clear();

            // 进入页面时触发一次刷新
            switch (value.Page)
            {
                case ModsViewModel mods:
                    mods.Refresh();
                    _ = mods.InitializeAsync();
                    break;
                case ConflictsViewModel conflicts:
                    conflicts.Refresh();
                    break;
                case DownloadsViewModel downloads:
                    downloads.Refresh();
                    break;
                case ProfilesViewModel profiles:
                    profiles.Refresh();
                    break;
                case SettingsViewModel settings:
                    settings.Refresh();
                    break;
            }

            Raise(nameof(CurrentPage), nameof(CurrentTitle), nameof(CurrentSubtitle));
        }
    }

    public object CurrentPage => SelectedNav.Page;

    public string CurrentTitle => SelectedNav.Title;

    public string CurrentSubtitle => SelectedNav.Subtitle;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool IsAcrylicEnabled
    {
        get => _isAcrylicEnabled;
        set
        {
            if (!Set(ref _isAcrylicEnabled, value)) return;

            Library.Config.UseAcrylic = value;
            Library.SaveConfig();
            Raise(nameof(AcrylicButtonText));
            AcrylicChanged?.Invoke();
        }
    }

    public string AcrylicButtonText => IsAcrylicEnabled ? "毛玻璃：开" : "毛玻璃：关";

    /// <summary>毛玻璃开关变化（窗口据此重新应用视觉效果）。</summary>
    public event Action? AcrylicChanged;

    /// <summary>从配置重新读取毛玻璃开关（设置页修改后由窗口调用）。</summary>
    public void ReloadAcrylicFromConfig()
    {
        var value = Library.Config.UseAcrylic;
        if (_isAcrylicEnabled == value) return;

        _isAcrylicEnabled = value;
        Raise(nameof(IsAcrylicEnabled), nameof(AcrylicButtonText));
        AcrylicChanged?.Invoke();
    }

    /// <summary>当前是否以管理员身份运行。</summary>
    public bool IsElevated => ElevationService.IsElevated;

    /// <summary>是否显示"未提权"提示条。</summary>
    public bool ShowElevationBanner => !IsElevated && !Library.Config.ElevationDeclined;

    public string ElevationBannerText =>
        "当前以普通权限运行。若 Steam 装在 C:\\Program Files (x86) 等受保护目录，启用/禁用或下载 Mod 会因权限不足失败——建议以管理员身份重新启动。";

    /// <summary>提权后 Windows 会阻止资源管理器拖放，需要在界面上说明并给出替代入口。</summary>
    public bool ShowElevatedDragDropHint => IsElevated;

    public string ElevatedDragDropHintText =>
        "已以管理员身份运行：Windows 会阻止管理员程序接收资源管理器拖放，请改用「添加 Mod 文件」按钮（效果完全相同）。";

    /// <summary>当前 WPF 渲染层级（用于判断是否启用了硬件加速）。</summary>
    public string RenderTierText
    {
        get
        {
            try
            {
                return (System.Windows.Media.RenderCapability.Tier >> 16) switch
                {
                    0 => "软件渲染（未启用硬件加速，动画可能不够流畅）",
                    1 => "部分硬件加速",
                    _ => "硬件加速（Tier 2）",
                };
            }
            catch
            {
                return "未知";
            }
        }
    }

    public bool IsSoftwareRendering
    {
        get
        {
            try
            {
                return (System.Windows.Media.RenderCapability.Tier >> 16) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public string VersionText => $"{AppInfo.ShortName} v{AppInfo.Version}";

    public string ModSummary => Mods.SummaryText;

    public string ProgressText => Mods.ProgressText;

    public bool IsBusy => Mods.IsBusy;

    public string ConflictBadgeText => Library.LastConflictReport is { FileCount: > 0 } report
        ? $"⚠ {report.FileCount} 处冲突"
        : "⚠ 无冲突";

    public async Task InitializeAsync()
    {
        StatusMessage = "正在初始化…";
        await Mods.InitializeAsync().ConfigureAwait(true);
        StatusMessage = string.IsNullOrWhiteSpace(Mods.ProgressText) ? Mods.SummaryText : Mods.ProgressText;
    }

    public void SaveAll()
    {
        try
        {
            Library.SaveConfig();
            Library.SaveDatabase(force: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"退出保存失败: {ex.Message}");
        }
    }

    /// <summary>由窗口（拖放安装等）设置状态栏文字。</summary>
    public void SetStatus(string text) => StatusMessage = text;

    private void RaiseSummary()
    {
        Raise(nameof(ModSummary), nameof(ConflictBadgeText), nameof(ProgressText), nameof(IsBusy));
    }

    /// <summary>以管理员身份重新启动。</summary>
    private void RestartElevated()
    {
        if (ElevationService.IsElevated)
        {
            StatusMessage = "当前已经是管理员权限";
            return;
        }

        if (ElevationService.TryRestartElevated(Array.Empty<string>(), out var error))
        {
            SaveAll();
            System.Windows.Application.Current.Shutdown();
            return;
        }

        StatusMessage = "未能获取管理员权限：" + error;
        _services.Dialogs.Info("未能获取管理员权限。\r\n\r\n" + error +
                               "\r\n\r\n你仍然可以正常使用扫描、启停、冲突检测等功能，" +
                               "只有在写入受保护的 Steam 目录时才会失败。", "管理员权限");
    }

    /// <summary>
    /// 用文件选择框批量添加 .vpk（拖放的等价替代，且在管理员权限下依然可用）。
    /// </summary>
    private async Task AddVpkFilesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要安装的 Mod 文件",
            Filter = "Left 4 Dead 2 Mod (*.vpk)|*.vpk|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

        var target = Library.ResolveDownloadDirectory();
        var installed = new List<string>();
        var failed = new List<string>();

        foreach (var file in dialog.FileNames)
        {
            var (item, message) = await Library.InstallVpkAsync(file, target).ConfigureAwait(true);
            if (item != null) installed.Add(item.DisplayName);
            else failed.Add($"{Path.GetFileName(file)}：{message}");
        }

        Mods.Refresh();
        StatusMessage = $"已添加 {installed.Count} 个 Mod" + (failed.Count > 0 ? $"，失败 {failed.Count} 个" : string.Empty);

        if (failed.Count > 0)
            _services.Dialogs.Error("部分文件未能安装。", "添加 Mod", string.Join("\r\n", failed));
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportError("打开链接失败", ex);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ReportError("打开目录失败", ex);
        }
    }

    private void ReportError(string title, Exception exception)
    {
        Log.Error(title, exception);
        StatusMessage = $"{title}：{exception.Message}";
        _services.Dialogs.Error(exception.Message, title, exception.ToString());
    }
}
