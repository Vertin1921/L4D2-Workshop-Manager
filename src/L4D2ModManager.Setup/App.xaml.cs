using System.Windows;
using L4D2ModManager.Core.Services.Deployment;

namespace L4D2ModManager.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);

        // 程序目录里的卸载程序只当"复制器 + 启动器"：
        // 立刻把自己复制到 %TEMP%，由临时副本执行真正的卸载，自己不留任何界面直接退出。
        // 这样程序目录里那份不是运行中的映像，卸载能把 Uninstall.exe 一起删掉（不再残留）。
        var fromTemp = e.Args.Any(a => a.Equals("--from-temp", StringComparison.OrdinalIgnoreCase) ||
                                       a.Equals("/from-temp", StringComparison.OrdinalIgnoreCase));

        if (!fromTemp && AppDeployment.IsUninstallerProcess())
        {
            if (TryLaunchTempCopy(e.Args))
            {
                Shutdown(0);
                return;
            }
        }
        // 命令行开关：/subdir（在所选目录下再建一层子目录）
        if (e.Args.Any(a => a.Equals("/subdir", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("-subdir", StringComparison.OrdinalIgnoreCase)))
        {
            options.CreateSubdirectory = true;
        }

        if (options.Silent)
        {
            var exitCode = RunSilent(options);
            Shutdown(exitCode);
            return;
        }

        // 安装程序的 app.manifest 已声明 requireAdministrator，Windows 会直接弹 UAC，
        // 这里不再做运行时 runas 自提升（那本身就是杀软误报的诱因之一）。
        var window = new MainWindow();
        window.Configure(options);
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 把当前卸载程序复制到 %TEMP% 并以临时副本启动真正的卸载。
    /// 目标目录通过 /dir= 显式传给副本（副本自己所在目录是 %TEMP%，不能靠它推断）。
    /// </summary>
    private static bool TryLaunchTempCopy(string[] args)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(self) || !File.Exists(self)) return false;

            var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var dirArg = args.FirstOrDefault(a => a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase) ||
                                                  a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase));
            if (dirArg != null) target = dirArg.Substring(dirArg.IndexOf('=') + 1).Trim('"');

            var quiet = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

            var tempCopy = Path.Combine(Path.GetTempPath(), "Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(self, tempCopy, overwrite: true);

            var arguments = $"/uninstall /dir=\"{target}\" /from-temp";
            if (quiet) arguments += " /S";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempCopy)
            {
                UseShellExecute = true,
                Arguments = arguments,
            });

            L4D2ModManager.Core.Services.Log.Info($"已启动临时卸载程序：{tempCopy}（目标目录 {target}）");
            return true;
        }
        catch (Exception ex)
        {
            L4D2ModManager.Core.Services.Log.Warn("启动临时卸载程序失败：" + ex.Message);
            return false;
        }
    }
    /// <summary>静默安装 / 卸载（用于自动化部署）。</summary>
    private static int RunSilent(CommandLineOptions options)
    {
        try
        {
            if (options.Uninstall)
            {
                Installer.UninstallAsync(options.InstallDirectory, removeUserData: options.RemoveUserData)
                    .GetAwaiter().GetResult();
                return 0;
            }

            var installOptions = new InstallOptions
            {
                TargetDirectory = options.InstallDirectory ?? Installer.DefaultTargetDirectory,
                DesktopShortcut = !options.NoDesktopShortcut,
                StartMenuShortcut = !options.NoStartMenuShortcut,
                LaunchAfterInstall = false,
                WriteRegistry = !options.NoRegistry,
            };

            Installer.InstallAsync(installOptions).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                // 静默模式下把错误写入临时文件，便于脚本排查
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "l4d2mm-setup-error.log"),
                    DateTime.Now + Environment.NewLine + ex);
            }
            catch
            {
                // 忽略
            }

            return 1;
        }
    }
}

/// <summary>命令行参数。</summary>
public sealed class CommandLineOptions
{
    public bool Uninstall { get; private set; }
    public bool Silent { get; private set; }
    public bool NoDesktopShortcut { get; private set; }
    /// <summary>在所选目录下再创建子目录后安装（命令行 /subdir）。</summary>
    public bool CreateSubdirectory { get; set; }
    public bool NoStartMenuShortcut { get; private set; }
    public bool NoRegistry { get; private set; }
    public bool RemoveUserData { get; private set; }
    public bool NoElevate { get; private set; }
    public bool ElevatedMarker { get; private set; }
    public string? InstallDirectory { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        foreach (var raw in args)
        {
            var argument = raw.Trim();
            if (argument.Length == 0) continue;

            if (argument.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
            {
                options.InstallDirectory = argument[(argument.IndexOf('=') + 1)..].Trim('"');
                continue;
            }

            switch (argument.TrimStart('/', '-').ToLowerInvariant())
            {
                case "uninstall":
                case "u":
                    options.Uninstall = true;
                    break;
                case "s":
                case "silent":
                case "quiet":
                    options.Silent = true;
                    break;
                case "nodesktop":
                    options.NoDesktopShortcut = true;
                    break;
                case "nostartmenu":
                    options.NoStartMenuShortcut = true;
                    break;
                case "noregistry":
                    options.NoRegistry = true;
                    break;
                case "keepdata":
                    options.RemoveUserData = false;
                    break;
                case "removedata":
                    options.RemoveUserData = true;
                    break;
                case "noelevate":
                    options.NoElevate = true;
                    break;
                case "elevated":
                    options.ElevatedMarker = true;
                    break;
            }
        }

        return options;
    }
}
