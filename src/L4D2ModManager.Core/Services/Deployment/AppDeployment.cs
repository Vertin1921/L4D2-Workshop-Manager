using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace L4D2ModManager.Core.Services.Deployment;

/// <summary>已安装信息。</summary>
public sealed record InstallInfo(string Location, string Version);

/// <summary>卸载进度。</summary>
public sealed record UninstallProgress(int Percent, string Message);

/// <summary>卸载结果：立即删除的文件数，以及需要重启后才能删除的文件数。</summary>
public sealed record UninstallOutcome(int DeletedFiles, int DeferredFiles)
{
    public bool FullyRemoved => DeferredFiles == 0;

    public string Summary => FullyRemoved
        ? $"已删除 {DeletedFiles} 个文件，安装目录已清理完毕。"
        : $"已删除 {DeletedFiles} 个文件；另有 {DeferredFiles} 个文件正被占用，将在下次重启后自动清理。";
}

/// <summary>
/// 安装/卸载相关的公共逻辑（安装程序与主程序共用）。
///
/// 设计取舍（与杀软误报有关）：
///   · 不生成 .cmd/.bat 脚本、不调用 cmd.exe / rmdir 之类"脚本化递归删除"——
///     这是启发式引擎最敏感的行为组合之一。
///   · 改为纯 .NET 的"重试删除 + MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT) 延迟清理"，
///     行为与普通应用程序一致。
/// </summary>
public static class AppDeployment
{
    public const string AppName = "Left 4 Dead 2 Mod Manager";
    public const string AppShortName = "L4D2 Mod Manager";
    public const string AppId = "L4D2ModManager";
    public const string ExecutableName = "L4D2ModManager.exe";

    /// <summary>数据目录名（%AppData% 下）。</summary>
    public const string UserDataFolderName = "L4D2ModManager";

    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;
    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);

    public static string Version => AppInfo.Version;

    /// <summary>用户数据目录（配置、数据库、缩略图、日志）。</summary>
    public static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), UserDataFolderName);

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>读取卸载注册表中的安装信息。</summary>
    public static InstallInfo? TryGetInstalledInfo()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKey);
            if (key == null) return null;

            var location = key.GetValue("InstallLocation") as string;
            var version = key.GetValue("DisplayVersion") as string ?? AppInfo.Version;
            return string.IsNullOrWhiteSpace(location) ? null : new InstallInfo(location, version);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写入卸载注册表项（HKCU，无需管理员权限）。</summary>
    public static void WriteUninstallRegistry(string installDirectory, long installedSizeBytes)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKey);
            if (key == null) return;

            var executable = Path.Combine(installDirectory, ExecutableName);
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "L4D2 Mod Manager Project");
            key.SetValue("InstallLocation", installDirectory);
            key.SetValue("DisplayIcon", executable);
            key.SetValue("UninstallString", $"\"{executable}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{executable}\" --uninstall --silent");
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, installedSizeBytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入卸载注册表失败: {ex.Message}");
        }
    }

    public static void RemoveUninstallRegistry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除卸载注册表失败: {ex.Message}");
        }
    }

    /// <summary>创建开始菜单与桌面快捷方式。</summary>
    public static void CreateShortcuts(string installDirectory, bool startMenu, bool desktop)
    {
        var executable = Path.Combine(installDirectory, ExecutableName);

        if (startMenu)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppShortName);
                Directory.CreateDirectory(folder);
                CreateShortcut(Path.Combine(folder, AppShortName + ".lnk"), installDirectory, executable);
                CreateShortcut(Path.Combine(folder, "卸载 " + AppShortName + ".lnk"), installDirectory, executable, "--uninstall");
            }
            catch (Exception ex)
            {
                Log.Warn($"创建开始菜单快捷方式失败: {ex.Message}");
            }
        }

        if (desktop)
        {
            try
            {
                var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    AppShortName + ".lnk");
                CreateShortcut(link, installDirectory, executable);
            }
            catch (Exception ex)
            {
                Log.Warn($"创建桌面快捷方式失败: {ex.Message}");
            }
        }
    }

    public static void RemoveShortcuts()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppShortName);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除开始菜单快捷方式失败: {ex.Message}");
        }

        try
        {
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                AppShortName + ".lnk");
            if (File.Exists(link)) File.Delete(link);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除桌面快捷方式失败: {ex.Message}");
        }
    }

    /// <summary>使用 WScript.Shell 创建 .lnk 快捷方式。</summary>
    public static void CreateShortcut(string linkPath, string workingDirectory, string targetPath, string arguments = "")
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;

        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = AppName;
        shortcut.Arguments = arguments;
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Save();
    }

    /// <summary>
    /// 卸载：删除快捷方式与注册表项，然后清理程序文件（占用中的文件注册为重启后删除）。
    /// </summary>
    public static async Task<UninstallOutcome> UninstallAsync(
        string? installDirectory,
        bool removeUserData,
        IProgress<UninstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var target = installDirectory;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            target = TryGetInstalledInfo()?.Location;

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            throw new InvalidOperationException("未找到安装目录，无法卸载。");

        // 顺序很重要：先移除快捷方式与注册表项（此时程序文件还在，相关程序集可正常加载）
        progress?.Report(new UninstallProgress(5, "删除快捷方式…"));
        RemoveShortcuts();

        progress?.Report(new UninstallProgress(12, "清理注册表…"));
        RemoveUninstallRegistry();

        progress?.Report(new UninstallProgress(20, "正在删除程序文件…"));

        var outcome = await Task.Run(
            () => DeleteDirectoryFiles(target, TimeSpan.FromSeconds(10), removeUserData, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new UninstallProgress(100, outcome.Summary));
        Log.Info($"卸载完成：{outcome.Summary}（{target}）");
        return outcome;
    }

    /// <summary>
    /// 尽力删除目录内文件：先重试若干秒（等待自身进程退出、文件解锁），
    /// 仍被占用（例如正在运行的 exe / 已映射的运行时 DLL）时注册为"重启后删除"。
    /// </summary>
    public static UninstallOutcome DeleteDirectoryFiles(
        string directory,
        TimeSpan retryWindow,
        bool removeUserData = false,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + retryWindow;
        var deferred = new List<string>();
        int deleted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deferred.Clear();

            foreach (var file in EnumerateFilesSafe(directory))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    deferred.Add(file);
                }
            }

            RemoveEmptyDirectories(directory);

            if (deferred.Count == 0 || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(400);
        }

        if (deferred.Count > 0)
        {
            foreach (var file in deferred)
                RegisterDeleteOnReboot(file);

            // 目录本身也登记一次：重启后目录已空，系统会一并删除
            RegisterDeleteOnReboot(directory);
        }
        else
        {
            // 全部文件都已删除：连安装目录本身一起删掉
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: false);
            }
            catch
            {
                RegisterDeleteOnReboot(directory);
            }
        }

        if (removeUserData)
        {
            try
            {
                if (Directory.Exists(UserDataDirectory))
                    Directory.Delete(UserDataDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"删除用户数据失败：{ex.Message}");
            }
        }

        return new UninstallOutcome(deleted, deferred.Count);
    }

    /// <summary>
    /// 把安装载荷（zip）解压到目标目录。
    /// 放在 Core 里可以让安装程序与自检程序共用同一份实现（自检不需要管理员权限即可验证解压逻辑）。
    /// </summary>
    public static async Task<int> ExtractPayloadAsync(
        string payloadPath,
        string targetDirectory,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(payloadPath))
            throw new FileNotFoundException("找不到安装载荷 payload.zip", payloadPath);

        targetDirectory = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(targetDirectory);

        return await Task.Run(() =>
        {
            using var payload = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new System.IO.Compression.ZipArchive(payload, System.IO.Compression.ZipArchiveMode.Read);

            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            int index = 0;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
                if (!destination.StartsWith(targetDirectory, StringComparison.OrdinalIgnoreCase))
                    continue; // 防御 zip slip

                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                entry.ExtractToFile(destination, overwrite: true);

                index++;
                progress?.Report((5 + (int)(index * 80.0 / Math.Max(1, entries.Count)), $"正在解压：{entry.Name}"));
            }

            return index;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>请求在本机重启后删除指定文件或空目录（无需子进程、无需脚本）。</summary>
    public static bool RegisterDeleteOnReboot(string path)
    {
        try
        {
            return MoveFileEx(path, null, MoveFileDelayUntilReboot);
        }
        catch (Exception ex)
        {
            Log.Warn($"登记重启后删除失败（{path}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份重新启动指定程序（弹出 UAC）。成功启动新实例后返回 true，调用方应退出当前实例。
    /// 安装程序与主程序共用此实现。
    /// </summary>
    public static bool TryRestartElevated(string executablePath, IReadOnlyList<string> args, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            error = "无法定位程序自身路径";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            };

            foreach (var argument in args)
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员权限请求";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举待删除文件失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>自底向上删除空目录（保留根目录）。</summary>
    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch
                {
                    // 忽略非空/占用目录
                }
            }
        }
        catch
        {
            // 忽略
        }
    }
}
