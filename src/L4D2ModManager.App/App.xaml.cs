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

        // 自愈：安装目录里的 Uninstall.exe 可能来自旧版本安装包（表现为"双击卸载程序却打开了管理器"），
        // 只要发现它与当前程序不是同一份，就用当前程序覆盖它。
        SynchronizeUninstaller();

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

    /// <summary>让安装目录里的 Uninstall.exe 与当前程序保持同版本（旧副本会导致卸载程序打开管理器）。</summary>
    private static void SynchronizeUninstaller()
    {
        try
        {
            // 自己就是卸载程序时不要动（走的是卸载流程）
            if (Core.Services.Deployment.AppDeployment.IsUninstallerProcess()) return;

            var self = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(self) || !File.Exists(self)) return;

            var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var uninstaller = Path.Combine(directory, Core.Services.Deployment.AppDeployment.UninstallerName);

            if (!File.Exists(uninstaller)) return;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase)) return;

            var selfInfo = new FileInfo(self);
            var uninstallerInfo = new FileInfo(uninstaller);

            // 只自愈"主程序副本"形态的卸载程序（大小与主程序接近）。
            // 安装程序装进来的独立卸载程序体积完全不同（上百 MB），绝不能被覆盖掉。
            var looksLikeAppCopy = uninstallerInfo.Length <= selfInfo.Length * 2;

            if (!looksLikeAppCopy) return;

            // 版本不同（大小不同，或卸载程序比主程序旧）就用当前版本覆盖它
            var outdated = selfInfo.Length != uninstallerInfo.Length ||
                           uninstallerInfo.LastWriteTimeUtc < selfInfo.LastWriteTimeUtc.AddMinutes(-1);

            if (!outdated) return;

            File.Copy(self, uninstaller, overwrite: true);
            Log.Info("已把程序目录里的卸载程序更新为当前版本");
        }
        catch (Exception ex)
        {
            Log.Warn($"同步卸载程序失败：{ex.Message}");
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

        // 通过安装目录里的 Uninstall.exe 启动时等同于 --uninstall（即使一个参数都没有）。
        // 注意：这段必须在 args.Length == 0 的早退之前，否则双击卸载程序只会打开主程序。
        if (L4D2ModManager.Core.Services.Deployment.AppDeployment.IsUninstallerProcess())
        {
            args = new[] { "--uninstall" }
                .Concat(args.Where(x => x is "--silent" or "--removedata"))
                .ToArray();
        }

        if (args.Length == 0) return false;

        var command = args[0].Trim().ToLowerInvariant();

        if (command is not ("--version" or "--help" or "-h" or "--diagnose" or "--scan" or "--uninstall" or "--selftest-ui" or "--remove-leftover"))
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

                // 自我复制到临时目录后再卸载：
                // Windows 不允许运行中的 exe 删掉自己的文件，所以在程序目录里直接卸载时，
                // Uninstall.exe（以及它加载的几个 DLL）只能登记为"重启后清理"。
                // 复制到 %TEMP% 运行后，程序目录里那份就不是运行中的映像，可以一次删干净。
                // 只对"自包含"的卸载程序这么做（体积远大于主程序）；主程序小副本离不开同目录运行库。
                if (!args.Any(a => a.Equals("--from-temp", StringComparison.OrdinalIgnoreCase)))
                {
                    var selfPath = Environment.ProcessPath;

                    if (!string.IsNullOrWhiteSpace(selfPath) && File.Exists(selfPath))
                    {
                        var selfLength = new FileInfo(selfPath).Length;

                        // 默认关闭：实测该路径可能被系统/杀软拦掉（副本起不来，导致卸载静默失败）。
                        // 需要时可用环境变量 L4D2MM_TEMP_UNINSTALL=1 启用。
                        if (selfLength > 8L * 1024 * 1024 &&
                            Environment.GetEnvironmentVariable("L4D2MM_TEMP_UNINSTALL") == "1")
                        {
                            try
                            {
                                var targetArg = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase));
                                var installPath = targetArg != null
                                    ? Path.GetFullPath(targetArg.Substring("--dir=".Length).Trim('"'))
                                    : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

                                var tempCopy = Path.Combine(
                                    Path.GetTempPath(),
                                    "L4D2MM-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");

                                File.Copy(selfPath, tempCopy, overwrite: true);

                                var tempArgs = new List<string> { "--uninstall", "--from-temp", "--dir=" + installPath };
                                if (silent) tempArgs.Add("--silent");
                                if (removeData) tempArgs.Add("--removedata");

                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempCopy)
                                {
                                    UseShellExecute = true,
                                    Arguments = string.Join(" ", tempArgs.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)),
                                });

                                report.AppendLine("已从临时目录启动卸载程序（这样程序目录里的文件能一次删干净）。");
                                exitCode = 0;
                                break;
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine("复制到临时目录失败，改为原地卸载：" + ex.Message);
                            }
                        }
                    }
                }

                // 程序所在目录即安装目录；从临时目录运行时由 --dir= 显式传入
                var dirArgument = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase));
                var installDirectory = dirArgument != null
                    ? Path.GetFullPath(dirArgument.Substring("--dir=".Length).Trim('"'))
                    : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

                // 需要管理员权限时先自提升（例如装在 Program Files 下）。
                // 只在"目录不可写"且当前未提权时才弹 UAC，用户目录下不会打扰用户。
                if (!AppDeployment.IsElevated && !AppDeployment.IsDirectoryWritable(installDirectory))
                {
                    var elevatedArgs = new List<string> { "--uninstall" };
                    if (silent) elevatedArgs.Add("--silent");
                    if (removeData) elevatedArgs.Add("--removedata");

                    var self = Environment.ProcessPath;
                    string? elevateError = null;

                    if (!string.IsNullOrWhiteSpace(self) &&
                        AppDeployment.TryRestartElevated(self, elevatedArgs, out elevateError))
                    {
                        report.AppendLine("安装目录需要管理员权限，已请求以管理员身份重新启动卸载程序。");
                        exitCode = 0;
                        break;
                    }

                    report.AppendLine("未能获取管理员权限，将继续尝试卸载（部分文件可能无法立即删除）：" + elevateError);

                    if (!silent)
                    {
                        System.Windows.MessageBox.Show(
                            "安装目录位于受保护位置（例如 Program Files），需要管理员权限才能彻底删除。\r\n\r\n" +
                            "刚才的提权请求被取消或失败，将尝试继续卸载；" +
                            "建议右键卸载程序选择「以管理员身份运行」再试一次。",
                            "需要管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

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
                    UninstallOutcome outcome = new(0, 0);
                    var uninstallFailed = false;

                    if (silent)
                    {
                        outcome = AppDeployment.UninstallAsync(installDirectory, removeData).GetAwaiter().GetResult();
                    }
                    else
                    {
                        // 图形模式：显示独立进度窗口，边卸载边看进度
                        var progressWindow = new Dialogs.ProgressWindow("卸载 " + AppDeployment.AppShortName);
                        var progress = new Progress<UninstallProgress>(
                            p => progressWindow.Report(p.Percent, p.Message));

                        progressWindow.Loaded += (_, _) =>
                        {
                            Task.Run(() =>
                            {
                                try
                                {
                                    outcome = AppDeployment.UninstallAsync(installDirectory, removeData, progress)
                                        .GetAwaiter().GetResult();
                                }
                                catch (Exception ex)
                                {
                                    report.AppendLine("卸载失败：" + ex.Message);
                                    uninstallFailed = true;
                                }
                                finally
                                {
                                    progressWindow.RequestClose();
                                }
                            });
                        };

                        progressWindow.ShowDialog();
                    }

                    if (uninstallFailed) exitCode = 1;

                    // 从临时目录运行时：把自己（%TEMP% 里的临时副本）登记为重启后删除，避免留下大文件
                    if (args.Any(a => a.Equals("--from-temp", StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(Environment.ProcessPath))
                    {
                        AppDeployment.RegisterDeleteOnReboot(Environment.ProcessPath);
                    }

                    report.AppendLine("卸载完成。");
                    report.AppendLine(outcome.Summary);
                    report.AppendLine($"安装目录：{installDirectory}");
                    if (removeData) report.AppendLine($"已删除用户数据：{AppDeployment.UserDataDirectory}");
                    else report.AppendLine($"已保留用户数据：{AppDeployment.UserDataDirectory}");

                    // 兜底：卸载程序自己可能既删不掉也改不了名（被杀软/权限拦住）。
                    // 这时把"删掉自己"交给 %TEMP% 里的副本：主进程退出后由副本完成并显示"删除完毕"。
                    var selfPath = Environment.ProcessPath;
                    var runningFromTemp = args.Any(v => v.Equals("--from-temp", StringComparison.OrdinalIgnoreCase));

                    if (!runningFromTemp && !string.IsNullOrWhiteSpace(selfPath) && File.Exists(selfPath))
                    {
                        try
                        {
                            var helper = Path.Combine(Path.GetTempPath(), "Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                            File.Copy(selfPath, helper, overwrite: true);

                            var helperArgs = $"--remove-leftover \"{selfPath}\" --deleted={outcome.DeletedFiles}";
                            if (silent) helperArgs += " --silent";

                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helper)
                            {
                                UseShellExecute = true,
                                Arguments = helperArgs,
                            });

                            report.AppendLine("已交给临时目录的副本来删除卸载程序自身（随后会显示删除完毕）。");
                            exitCode = 0;
                            break;
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine("启动收尾副本失败：" + ex.Message);
                        }
                    }

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

            case "--remove-leftover":
            {
                // 第二段：主卸载程序已经删掉其它所有文件，并在退出后把"删掉 Uninstall.exe"的活交给这里。
                // 目标文件此刻被前一个进程占用，等它退出后即可删除。
                var leftover = args.Length > 1 ? args[1].Trim('"') : null;
                var deletedCount = 0;

                var deletedArg = args.FirstOrDefault(v => v.StartsWith("--deleted=", StringComparison.OrdinalIgnoreCase));
                if (deletedArg != null) int.TryParse(deletedArg.Substring("--deleted=".Length), out deletedCount);

                if (!string.IsNullOrWhiteSpace(leftover))
                {
                    for (var i = 0; i < 120 && File.Exists(leftover); i++)
                    {
                        try { File.Delete(leftover); }
                        catch { /* 前一进程还没完全退出，稍后再试 */ }

                        if (!File.Exists(leftover)) break;
                        Thread.Sleep(500);
                    }
                }

                AppDeployment.RegisterDeleteOnReboot(Environment.ProcessPath ?? string.Empty);

                if (!args.Any(v => v.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
                {
                    System.Windows.MessageBox.Show(
                        $"卸载完成。\r\n\r\n已删除 {deletedCount} 个文件，程序目录已清空。",
                        AppDeployment.AppShortName, MessageBoxButton.OK, MessageBoxImage.Information);
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
            Check("人物语音", new Views.VoiceView(), main.Voice);
            Check("创意工坊", new Views.WorkshopView(), main.Workshop);
            Check("下载管理", new Views.DownloadsView(), main.Downloads);
            Check("冲突检测", new Views.ConflictsView(), main.Conflicts);
            Check("配置方案", new Views.ProfilesView(), main.Profiles);
            Check("设置", new Views.SettingsView(), main.Settings);

            // 每个导航页都必须能通过 DataTemplate 映射到真实视图。
            // （漏写 DataTemplate 时 WPF 会直接把 ViewModel 的 ToString() 显示在页面里，这里专门把它挡住）
            var navHost = new System.Windows.Controls.ContentControl();
            foreach (var nav in main.NavItems)
            {
                navHost.Content = nav.Page;
                navHost.Measure(size);
                navHost.Arrange(rect);
                navHost.UpdateLayout();

                var view = FindView(navHost);
                if (view == null)
                {
                    throw new InvalidOperationException(
                        $"导航页「{nav.Title}」没有对应的视图：MainWindow 里缺少 {nav.Page.GetType().Name} 的 DataTemplate 映射。");
                }

                report.AppendLine($"  · {nav.Title} → {view.GetType().Name}");
            }

            static System.Windows.Controls.UserControl? FindView(System.Windows.DependencyObject root)
            {
                int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                    if (child is System.Windows.Controls.UserControl control) return control;

                    var nested = FindView(child);
                    if (nested != null) return nested;
                }

                return null;
            }

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
