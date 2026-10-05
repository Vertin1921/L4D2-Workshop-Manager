using System.Windows;
using Microsoft.Win32;

namespace L4D2ModManager.Setup;

public partial class MainWindow : Window
{
    private CommandLineOptions _options = new();
    private bool _uninstallMode;

    /// <summary>true = 本次是以 Uninstall.exe 启动（显示"修复/程序目录"措辞）；false = 安装程序本体（原措辞）。</summary>
    private bool _isUninstallerBinary;
    private bool _finished;
    private bool _busy;

    /// <summary>是否处于"覆盖更新"模式（检测到已安装）。</summary>
    private bool _updateMode;

    public MainWindow()
    {
        InitializeComponent();

        // 安装程序启动时把窗口带到最前面一次（不是强制置顶：用户切换窗口后不会一直压在最上面）
        Loaded += (_, _) => BringToFrontOnce();

        // 路径预览（会在所选目录下自动加一层程序目录）
        PathBox.TextChanged += (_, _) => UpdateEffectivePath();
    }

    /// <summary>短暂置顶 + 激活，保证安装程序出现在用户眼前。</summary>
    /// <summary>切换到卸载界面（安装程序自带卸载，不需要额外文件）。</summary>
    private void EnterUninstallMode(string? location)
    {
        _uninstallMode = true;

        HeaderText.Text = "卸载 " + Installer.AppShortName;
        SubHeaderText.Text = "移除已安装的程序文件、快捷方式与卸载信息（不会删除你的 Mod 文件）";
        InstallPanel.Visibility = Visibility.Collapsed;
        UninstallPanel.Visibility = Visibility.Visible;
        UninstallShortcutButton.Visibility = Visibility.Collapsed;
        ActionButton.Content = "开始卸载";
        ActionButton.IsEnabled = true;
        CancelButton.Content = "关闭";
        CancelButton.IsEnabled = true;
        _busy = false;
        _finished = false;

        if (string.IsNullOrWhiteSpace(location))
        {
            Installer.TryGetInstalledInfo(out location, out _);
        }

        UninstallLocationText.Text = string.IsNullOrWhiteSpace(location)
            ? "未检测到程序目录，将尝试从注册表卸载信息中查找。"
            : $"程序目录：{location}";
    }

    private void SwitchToUninstall_Click(object sender, RoutedEventArgs e) => EnterUninstallMode(null);

    private void BringToFrontOnce()    {
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            Topmost = true;
            Activate();
            Topmost = false;
            Focus();
        }
        catch
        {
            // 置顶失败不影响安装
        }
    }

    public void Configure(CommandLineOptions options)
    {
        _options = options;
        // 以 Uninstall 开头的文件名启动时（打包成 Uninstall.exe），直接进入卸载界面；
        // 安装程序清单是 requireAdministrator，因此双击即自动获得管理员权限。
        _isUninstallerBinary = L4D2ModManager.Core.Services.Deployment.AppDeployment.IsUninstallerProcess();
        _uninstallMode = options.Uninstall || _isUninstallerBinary;

        if (_uninstallMode)
        {
            EnterUninstallMode(options.InstallDirectory);
        }
        else
        {
            // 覆盖修复：优先用命令行指定目录 → 已程序目录（注册表）→ 默认目录
            if (!string.IsNullOrWhiteSpace(options.InstallDirectory))
            {
                PathBox.Text = options.InstallDirectory;
            }
            else if (Installer.TryGetInstalledInfo(out var installedPath, out var installedVersion) &&
                     !string.IsNullOrWhiteSpace(installedPath) && Directory.Exists(installedPath))
            {
                _updateMode = true;
                PathBox.Text = installedPath;
                HeaderText.Text = (_isUninstallerBinary ? "修复 " : "更新 ") + Installer.AppShortName;
                UninstallShortcutButton.Visibility = Visibility.Visible;
                SubHeaderText.Text = $"检测到已安装版本 {installedVersion}，将直接覆盖修复到 " +
                                     L4D2ModManager.Core.Services.Deployment.AppDeployment.Version;
            }
            else
            {
                PathBox.Text = Installer.DefaultTargetDirectory;
            }

            DesktopCheck.IsChecked = !options.NoDesktopShortcut;
            StartMenuCheck.IsChecked = !options.NoStartMenuShortcut;
            UpdateEffectivePath();
            LaunchCheck.IsChecked = true;

            PathHint.Text = Installer.IsElevated
                ? "当前以管理员权限运行，默认安装到 Program Files。"
                : "当前未以管理员权限运行，默认安装到当前用户目录（无需管理员权限）。";

            if (!Installer.PayloadAvailable)
            {
                StatusText.Text = "警告：此安装包没有内置程序文件（payload.zip），无法安装。\n" +
                                  "请使用 build\\build-release.ps1 重新生成 Setup.exe。";
                ActionButton.IsEnabled = false;
            }
            else if (_updateMode)
            {
                StatusText.Text = $"覆盖修复：{PathBox.Text}\n" +
                                  "安装时会自动关闭正在运行的程序；你的 Mod、配置与数据库都会保留。";
                ActionButton.Content = _isUninstallerBinary ? "修复" : "更新";
            }
            else
            {
                StatusText.Text = $"安装包大小：{ModItemFormatSize(Installer.PayloadSize)}";
            }
        }
    }

    /// <summary>更新"实际安装位置"提示（勾选子目录时会把子目录拼上）。</summary>
    private void UpdateEffectivePath()
    {
        try
        {
            var path = PathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                EffectivePathText.Text = string.Empty;
                return;
            }

            // 始终自动加一层程序目录；如果目录本身就是它，就不再重复嵌套
            if (!path.TrimEnd(Path.DirectorySeparatorChar).EndsWith(Installer.AppShortName, StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(path, Installer.AppShortName);
            }

            EffectivePathText.Text = "实际安装位置：" + path;
        }
        catch
        {
            EffectivePathText.Text = string.Empty;
        }
    }

    private static string ModItemFormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择程序目录",
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "选择此文件夹",
        };

        if (dialog.ShowDialog(this) != true) return;

        var path = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrWhiteSpace(path)) PathBox.Text = path;
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_finished)
        {
            Close();
            return;
        }

        if (_uninstallMode)
        {
            await RunUninstallAsync();
            return;
        }

        await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        var target = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请先选择程序目录。", "需要程序目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _busy = true;
        ActionButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.Value = p.Percent;
            StatusText.Text = p.Message;
        });

        try
        {
            var options = new InstallOptions
            {
                TargetDirectory = target,
                DesktopShortcut = DesktopCheck.IsChecked == true,
                StartMenuShortcut = StartMenuCheck.IsChecked == true,
                LaunchAfterInstall = LaunchCheck.IsChecked == true,
                WriteRegistry = !_options.NoRegistry,
            };

            await Installer.InstallAsync(options, progress);

            _finished = true;
            Progress.Value = 100;
            StatusText.Text = "安装完成，可以关闭窗口了。";
            HeaderText.Text = "安装完成";
            SubHeaderText.Text = "已创建快捷方式，可从开始菜单启动 " + Installer.AppShortName;
            ActionButton.Content = "完成";
            ActionButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "安装失败：\n" + ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "安装失败：" + ex.Message;
            ActionButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunUninstallAsync()
    {
        // 目录解析：命令行 → 注册表里的安装记录 → 界面上显示的路径（三者都试，避免"空目录导致无反应"）
        var target = _options.InstallDirectory;

        if (string.IsNullOrWhiteSpace(target))
        {
            Installer.TryGetInstalledInfo(out var installed, out _);
            target = installed;
        }

        if (string.IsNullOrWhiteSpace(target)) target = PathBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            MessageBox.Show(this,
                "没有找到已安装的目录，无法卸载。\r\n\r\n" +
                "可以先把程序目录填到上面的路径框，或到「设置 → 应用和功能」里卸载。",
                "卸载 " + Installer.AppShortName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"确定要卸载 {Installer.AppShortName} 吗？\r\n\r\n" +
            $"程序目录：{target}\r\n\r\n" +
            "只删除程序自己的文件，同目录里的 Mod / 地图等一律保留。",
            "确认卸载", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        ActionButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = "准备卸载…";
        Progress.Value = 0;

        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.Value = p.Percent;
            StatusText.Text = p.Message;
        });

        try
        {
            L4D2ModManager.Core.Services.Log.Info($"开始卸载：{target}（删除用户数据：{RemoveDataCheck.IsChecked == true}）");
            var outcome = await Installer.UninstallAsync(target, RemoveDataCheck.IsChecked == true, progress);
            L4D2ModManager.Core.Services.Log.Info("卸载完成：" + outcome.Summary);

            MessageBox.Show(this, "卸载完成。\r\n\r\n" + outcome.Summary, "卸载 " + Installer.AppShortName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            L4D2ModManager.Core.Services.Log.Error("卸载失败", ex);
            MessageBox.Show(this, "卸载失败：\r\n" + ex.Message, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            ActionButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }
}
