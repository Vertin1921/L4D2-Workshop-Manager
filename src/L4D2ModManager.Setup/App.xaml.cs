using System.Windows;
using L4D2ModManager.Core.Services.Deployment;

namespace L4D2ModManager.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);

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
