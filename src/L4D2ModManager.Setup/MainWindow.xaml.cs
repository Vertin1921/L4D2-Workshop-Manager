using System.Windows;
using Microsoft.Win32;

namespace L4D2ModManager.Setup;

public partial class MainWindow : Window
{
    private CommandLineOptions _options = new();
    private bool _uninstallMode;
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
        CancelButton.Content = "关闭";

        if (string.IsNullOrWhiteSpace(location))
        {
            Installer.TryGetInstalledInfo(out location, out _);
        }

        UninstallLocationText.Text = string.IsNullOrWhiteSpace(location)
            ? "未检测到安装目录，将尝试从注册表卸载信息中查找。"
            : $"安装目录：{location}";
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
        _uninstallMode = options.Uninstall;

        if (_uninstallMode)
        {
            EnterUninstallMode(options.InstallDirectory);
        }
        else
        {
            // 覆盖更新：优先用命令行指定目录 → 已安装目录（注册表）→ 默认目录
            if (!string.IsNullOrWhiteSpace(options.InstallDirectory))
            {
                PathBox.Text = options.InstallDirectory;
            }
            else if (Installer.TryGetInstalledInfo(out var installedPath, out var installedVersion) &&
                     !string.IsNullOrWhiteSpace(installedPath) && Directory.Exists(installedPath))
            {
                _updateMode = true;
                PathBox.Text = installedPath;
                HeaderText.Text = "更新 " + Installer.AppShortName;
                UninstallShortcutButton.Visibility = Visibility.Visible;
                SubHeaderText.Text = $"检测到已安装版本 {installedVersion}，将直接覆盖更新到 " +
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
                StatusText.Text = $"覆盖更新：{PathBox.Text}\n" +
                                  "安装时会自动关闭正在运行的程序；你的 Mod、配置与数据库都会保留。";
                ActionButton.Content = "更新";
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
            Title = "选择安装目录",
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
            MessageBox.Show(this, "请先选择安装目录。", "需要安装目录", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var confirm = MessageBox.Show(this,
            "确定要卸载 " + Installer.AppShortName + " 吗？",
            "确认卸载", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

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
            await Installer.UninstallAsync(_options.InstallDirectory, RemoveDataCheck.IsChecked == true, progress);
            MessageBox.Show(this, "卸载完成。", "卸载", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "卸载失败：\n" + ex.Message, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
