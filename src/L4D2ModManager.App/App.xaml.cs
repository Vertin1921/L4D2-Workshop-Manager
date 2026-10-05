using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using L4D2ModManager.App.Services;
using L4D2ModManager.App.ViewModels;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Deployment;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        // 无界面模式：便于自动化验证与排查问题
        if (TryRunHeadless(e.Args, out var exitCode))
        {
            try
            {
                Shutdown(exitCode);
            }
            catch (Exception ex)
            {
                // 关闭过程（例如安装目录已被卸载删除）不应影响退出
                Log.Warn($"无界面模式关闭时出现异常（已忽略）：{ex.Message}");
            }
            finally
            {
                // 无界面模式下没有需要保留的消息循环，强制结束进程，
                // 保证 --scan / --uninstall 等命令不会残留后台进程。
                Environment.Exit(exitCode);
            }

            return;
        }

        // 自动请求管理员权限：与"清单里写 requireAdministrator"效果一致，
        // 但可以用设置项或 --no-elevate 关闭，也便于无界面模式自动化运行。
        if (TryElevateIfNeeded(e.Args))
        {
            Shutdown(0);
            return;
        }

        Services = new AppServices(() => MainWindow);

        try
        {
            Services.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
            MessageBox.Show($"程序初始化失败：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow
        {
            DataContext = new MainViewModel(Services),
        };

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Services?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"退出清理失败: {ex.Message}");
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 需要时以管理员身份重新启动自身。
    /// 返回 true 表示"已启动提权后的新实例"，当前实例应当立即退出。
    /// </summary>
    private static bool TryElevateIfNeeded(string[] args)
    {
        try
        {
            if (ElevationService.IsElevated) return false;
            if (ElevationService.IsHeadlessCommand(args)) return false;
            if (args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase))) return false;
            if (args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase))) return false;

            // 只读配置，不构造完整服务（避免副作用）
            var config = JsonStore.Load<AppConfig>(AppPaths.ConfigFile) ?? AppConfig.CreateDefault();
            if (!config.RequestElevationOnStartup || config.ElevationDeclined) return false;

            if (ElevationService.TryRestartElevated(args, out var error))
            {
                Log.Info("已请求以管理员身份重新启动程序");
                return true;
            }

            Log.Warn($"未能获取管理员权限，将以当前权限继续运行：{error}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"提权流程异常（忽略）：{ex.Message}");
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("界面线程异常", e.Exception);

        try
        {
            Services?.Dialogs.Error("程序遇到一个未处理的错误，已记录到日志。", "出错了", e.Exception.ToString());
            e.Handled = true;
        }
        catch
        {
            // 对话框本身失败时不再处理
        }
    }

    // ------------------------------------------------------------------ 无界面模式

    /// <summary>支持 --version / --help / --diagnose / --scan [目录]，输出到控制台与数据目录下的报告文件。</summary>
    private static bool TryRunHeadless(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        var command = args[0].Trim().ToLowerInvariant();
        if (command is not ("--version" or "--help" or "-h" or "--diagnose" or "--scan" or "--uninstall" or "--selftest-ui"))
            return false;

        AttachConsole(-1);

        // 关键：无界面模式下没有消息循环，
        // 必须清除同步上下文，否则 await ... ConfigureAwait(true) 的后续代码会被投递到不会执行的调度队列而卡死。
        SynchronizationContext.SetSynchronizationContext(null);

        var report = new StringBuilder();
        report.AppendLine($"{AppInfo.Name} {AppInfo.Version}");
        report.AppendLine($"数据目录：{AppPaths.Root}");
        report.AppendLine($"配置文件：{AppPaths.ConfigFile}");
        report.AppendLine();

        switch (command)
        {
            case "--version":
                report.AppendLine("net8.0-windows / WPF / x64");
                break;

            case "--help":
            case "-h":
                report.AppendLine("用法：");
                report.AppendLine("  L4D2ModManager.exe                 启动图形界面");
                report.AppendLine("  L4D2ModManager.exe --scan [目录]   扫描 Mod 目录并输出统计（不启动界面）");
                report.AppendLine("  L4D2ModManager.exe --diagnose      输出环境诊断信息");
                report.AppendLine("  L4D2ModManager.exe --version       输出版本信息");
                report.AppendLine("  L4D2ModManager.exe --uninstall     卸载程序（--silent 静默卸载，--removedata 同时删除用户数据）");
                report.AppendLine("  L4D2ModManager.exe --selftest-ui   加载全部界面与模板做自检（不显示窗口，可带一个 Mod 目录参数）");
                break;

            case "--selftest-ui":
                exitCode = RunUiSelfTest(args, report);
                break;

            case "--uninstall":
            {
                var silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("/s", StringComparison.OrdinalIgnoreCase));
                var removeData = args.Any(a => a.Equals("--removedata", StringComparison.OrdinalIgnoreCase));

                // 程序所在目录即安装目录
                var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

                if (!silent)
                {
                    var answer = System.Windows.MessageBox.Show(
                        $"确定要卸载 {AppDeployment.AppShortName} 吗？\r\n\r\n" +
                        $"安装目录：{installDirectory}\r\n\r\n" +
                        "点「是」同时删除用户数据（配置、Mod 数据库、方案、缩略图缓存）；\r\n" +
                        "点「否」只删除程序文件，保留用户数据。",
                        "卸载 " + AppDeployment.AppShortName,
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                    if (answer == MessageBoxResult.Cancel)
                    {
                        exitCode = 0;
                        break;
                    }

                    removeData = answer == MessageBoxResult.Yes;
                }

                try
                {
                    var outcome = AppDeployment.UninstallAsync(installDirectory, removeData).GetAwaiter().GetResult();
                    report.AppendLine("卸载完成。");
                    report.AppendLine(outcome.Summary);
                    report.AppendLine($"安装目录：{installDirectory}");
                    if (removeData) report.AppendLine($"已删除用户数据：{AppDeployment.UserDataDirectory}");
                    else report.AppendLine($"已保留用户数据：{AppDeployment.UserDataDirectory}");

                    if (!silent)
                    {
                        System.Windows.MessageBox.Show("卸载完成。\r\n\r\n" + outcome.Summary, AppDeployment.AppShortName,
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine("卸载失败：" + ex.Message);
                    exitCode = 1;
                }

                break;
            }

            case "--diagnose":
                AppendDiagnostics(report);
                break;

            case "--scan":
            {
                var service = new ModLibraryService();
                service.Initialize();

                if (args.Length > 1 && Directory.Exists(args[1]))
                {
                    service.Config.AutoDetectSteamPaths = false;
                    service.Config.ModDirectories.Clear();
                    service.AddDirectory(args[1]);
                }

                report.AppendLine("扫描目录：");
                foreach (var directory in service.Config.ModDirectories)
                    report.AppendLine("  · " + directory);
                report.AppendLine();

                var result = service.ScanAsync().GetAwaiter().GetResult();
                report.AppendLine(result.Summary);

                var mods = service.Mods.ToList();
                report.AppendLine($"列表条目：{mods.Count}");
                report.AppendLine($"含 addoninfo：{mods.Count(m => m.HasAddonInfo)}；启用 {mods.Count(m => m.IsEnabled)}；禁用 {mods.Count(m => m.IsDisabled)}");
                report.AppendLine("分类分布：" + string.Join("，", mods
                    .GroupBy(m => m.Category)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{ModCategoryInfo.Full(g.Key)}={g.Count()}")));

                if (service.Config.DetectConflictsAfterScan && service.LastConflictReport != null)
                    report.AppendLine(service.LastConflictReport.SummaryText);

                report.AppendLine();
                report.AppendLine("前 10 个 Mod：");
                foreach (var mod in mods.Take(10))
                    report.AppendLine($"  {mod.StateText} {mod.CategoryText} {mod.DisplayName} ({mod.SizeText})");

                foreach (var error in result.Errors.Take(10)) report.AppendLine("  [错误] " + error);

                exitCode = result.Failed > 0 ? 1 : 0;
                service.Dispose();
                break;
            }
        }

        var text = report.ToString();

        try
        {
            Console.Write(text);
        }
        catch
        {
            // 没有控制台时忽略
        }

        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(Path.Combine(AppPaths.Root, "headless-report.txt"), text, new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }

        return true;
    }

    /// <summary>
    /// 界面自检：不显示窗口，直接实例化 6 个页面并触发一次测量/布局，
    /// 用来验证 XAML、样式、转换器、模板与视图模型绑定是否能正常加载
    /// （这类问题只在运行期暴露，编译期看不出来）。
    /// </summary>
    private static int RunUiSelfTest(string[] args, StringBuilder report)
    {
        AppServices? services = null;
        try
        {
            services = new AppServices(() => null);
            services.Initialize();

            if (args.Length > 1 && Directory.Exists(args[1]))
            {
                services.Library.Config.AutoDetectSteamPaths = false;
                services.Library.Config.ModDirectories.Clear();
                services.Library.AddDirectory(args[1]);
            }

            var scan = services.Library.ScanAsync().GetAwaiter().GetResult();
            report.AppendLine($"已扫描：{scan.Summary}");
            services.Library.DetectConflictsAsync().GetAwaiter().GetResult();

            var main = new MainViewModel(services);
            main.Mods.Refresh();
            main.Conflicts.Refresh();
            main.Downloads.Refresh();
            main.Profiles.Refresh();
            main.Settings.Refresh();

            var size = new System.Windows.Size(1280, 860);
            var rect = new System.Windows.Rect(size);

            void Check(string name, System.Windows.FrameworkElement view, object viewModel)
            {
                view.DataContext = viewModel;
                view.Measure(size);
                view.Arrange(rect);
                view.UpdateLayout();
                report.AppendLine($"  · {name} 页面加载正常");
            }

            Check("Mod 管理", new Views.ModsView(), main.Mods);
            Check("创意工坊", new Views.WorkshopView(), main.Workshop);
            Check("下载管理", new Views.DownloadsView(), main.Downloads);
            Check("冲突检测", new Views.ConflictsView(), main.Conflicts);
            Check("配置方案", new Views.ProfilesView(), main.Profiles);
            Check("设置", new Views.SettingsView(), main.Settings);

            // 主窗口 XAML 也要能加载（侧边栏滑动指示条、页面容器、拖放遮罩等都在这里）
            var window = new MainWindow { DataContext = main };
            report.AppendLine("  · 主窗口 XAML 加载正常");
            window.Close();

            report.AppendLine($"界面自检通过：主列表 {main.Mods.Items.Count} 项，冲突 {main.Conflicts.VisibleCount} 项，方案 {main.Profiles.Profiles.Count} 个。");
            report.AppendLine($"图形加速：{main.RenderTierText}；管理员权限：{(main.IsElevated ? "是" : "否")}；毛玻璃：{(main.IsAcrylicEnabled ? "开" : "关")}");
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine("界面自检失败：" + ex);
            Log.Error("界面自检失败", ex);
            return 1;
        }
        finally
        {
            try
            {
                services?.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static void AppendDiagnostics(StringBuilder report)    {
        report.AppendLine("环境诊断：");
        report.AppendLine($"  · 操作系统：{Environment.OSVersion}");
        report.AppendLine($"  · 64 位进程：{Environment.Is64BitProcess}");
        report.AppendLine($"  · .NET 版本：{Environment.Version}");

        try
        {
            var version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            report.AppendLine($"  · WebView2 运行时：{version}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  · WebView2 运行时：不可用（{ex.Message}）");
        }

        try
        {
            var paths = Core.Services.Steam.SteamLibraryLocator.Detect();
            report.AppendLine($"  · Steam 根目录：{paths.SteamRoot ?? "(未检测到)"}");
            report.AppendLine($"  · steamcmd：{paths.SteamCmdPath ?? "(未检测到)"}");
            foreach (var library in paths.Libraries) report.AppendLine($"  · Steam 库：{library}");
            foreach (var addons in paths.AddonDirectories) report.AppendLine($"  · L4D2 addons：{addons}");
            foreach (var workshop in paths.WorkshopDirectories) report.AppendLine($"  · 创意工坊目录：{workshop}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  · Steam 探测失败：{ex.Message}");
        }
    }
}
