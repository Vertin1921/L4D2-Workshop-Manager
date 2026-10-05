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
    }

    public void Configure(CommandLineOptions options)
    {
        _options = options;
        _uninstallMode = options.Uninstall;

        if (_uninstallMode)
        {
            HeaderText.Text = "卸载 " + Installer.AppShortName;
            SubHeaderText.Text = "移除已安装的程序文件、快捷方式与卸载信息";
            InstallPanel.Visibility = Visibility.Collapsed;
            UninstallPanel.Visibility = Visibility.Visible;
            ActionButton.Content = "开始卸载";
            CancelButton.Content = "关闭";

            var location = options.InstallDirectory;
            if (string.IsNullOrWhiteSpace(location))
                Installer.TryGetInstalledInfo(out location, out _);

            UninstallLocationText.Text = string.IsNullOrWhiteSpace(location)
                ? "未检测到安装目录，将尝试从注册表卸载信息中查找。"
                : $"安装目录：{location}";
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
                SubHeaderText.Text = $"检测到已安装版本 {installedVersion}，将直接覆盖更新到 " +
                                     L4D2ModManager.Core.Services.Deployment.AppDeployment.Version;
            }
            else
            {
                PathBox.Text = Installer.DefaultTargetDirectory;
            }

            DesktopCheck.IsChecked = !options.NoDesktopShortcut;
            StartMenuCheck.IsChecked = !options.NoStartMenuShortcut;
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
