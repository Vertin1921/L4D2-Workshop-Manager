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

/// <summary>关闭运行中实例的结果。</summary>
public sealed record CloseProcessOutcome(int Closed, int Remaining);

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

    /// <summary>独立卸载程序（主程序的副本，靠文件名识别卸载意图）。</summary>
    public const string UninstallerName = "Uninstall.exe";

    /// <summary>安装清单：记录本程序装进安装目录的文件。卸载只删清单里的文件，绝不动用户的 mod。</summary>
    public const string ManifestFileName = "installed-files.txt";

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
            var uninstaller = ResolveUninstaller(installDirectory);
            key.SetValue("UninstallString", $"\"{uninstaller}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --silent");
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

    /// <summary>关闭正在运行的程序实例（覆盖更新前必须做，否则文件被占用无法替换）。</summary>
    /// <param name="processName">进程名（不含 .exe），默认就是主程序。</param>
    /// <param name="gracePeriodMs">先礼貌请求关闭的等待时间，超时后强制结束。</param>
    public static CloseProcessOutcome CloseRunningInstances(string? processName = null, int gracePeriodMs = 4000)
    {
        var name = string.IsNullOrWhiteSpace(processName)
            ? Path.GetFileNameWithoutExtension(ExecutableName)
            : processName!;

        int closed = 0;

        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;   // 不关自己

                // 先发关闭消息（程序能正常保存配置），超时再强杀
                if (process.CloseMainWindow() && process.WaitForExit(gracePeriodMs))
                {
                    closed++;
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                closed++;
            }
            catch (Exception ex)
            {
                Log.Warn($"关闭进程 {name}({process.Id}) 失败: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        int remaining;
        try
        {
            remaining = Process.GetProcessesByName(name).Length;
        }
        catch
        {
            remaining = 0;
        }

        if (closed > 0) Log.Info($"安装/更新前已关闭 {closed} 个正在运行的 {name} 实例");
        if (remaining > 0) Log.Warn($"仍有 {remaining} 个 {name} 实例在运行（可能以管理员身份启动）");

        return new CloseProcessOutcome(closed, remaining);
    }

    /// <summary>
    /// 关闭本程序的所有运行实例：既包括正常启动的 L4D2ModManager，
    /// 也包括"被当作卸载器启动、结果跑起了管理器"的 Uninstall.exe（常见于旧版卸载程序）。
    /// 当前进程自身永远不会被关闭。
    /// </summary>
    public static CloseProcessOutcome CloseAppInstances(int gracePeriodMs = 4000)
    {
        int closed = 0;
        int remaining = 0;

        foreach (var name in new[] { Path.GetFileNameWithoutExtension(ExecutableName), "Uninstall" })
        {
            // 当前进程名相同（自己就是 Uninstall.exe）时，CloseRunningInstances 会跳过自身
            var result = CloseRunningInstances(name, gracePeriodMs);
            closed += result.Closed;
            remaining += result.Remaining;
        }

        return new CloseProcessOutcome(closed, remaining);
    }
    /// <summary>安装目录里的卸载程序路径（不存在时回退为主程序）。</summary>
    public static string ResolveUninstaller(string installDirectory)
    {
        var uninstaller = Path.Combine(installDirectory, UninstallerName);
        return File.Exists(uninstaller) ? uninstaller : Path.Combine(installDirectory, ExecutableName);
    }

    /// <summary>在安装目录里生成独立卸载程序（复制主程序，靠文件名识别卸载意图）。</summary>
    public static bool CreateUninstaller(string installDirectory)
    {
        try
        {
            var source = Path.Combine(installDirectory, ExecutableName);
            var target = Path.Combine(installDirectory, UninstallerName);

            if (!File.Exists(source)) return false;

            File.Copy(source, target, overwrite: true);
            Log.Info($"已生成卸载程序：{target}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"生成卸载程序失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>当前进程是否通过"卸载程序"启动的（文件名以 Uninstall 开头）。</summary>
    public static bool IsUninstallerProcess(string? executablePath = null)
    {
        try
        {
            var path = executablePath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return false;

            var name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>目录是否可写（用探测文件判断；不可写通常意味着需要管理员权限）。</summary>
    public static bool IsDirectoryWritable(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return true;

        var probe = Path.Combine(directory, ".l4d2mm-write-probe");

        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
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
                CreateShortcut(Path.Combine(folder, "卸载 " + AppShortName + ".lnk"), installDirectory, ResolveUninstaller(installDirectory));
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

        // 卸载前先关掉正在运行的主程序：否则程序自身的 exe 与运行库被占用，
        // 只能登记为"重启后清理"，安装目录会残留大量文件。
        var closeResult = CloseAppInstances();
        if (closeResult.Closed > 0)
        {
            Log.Info($"卸载前已自动关闭 {closeResult.Closed} 个正在运行的程序实例");
            progress?.Report(new UninstallProgress(2, $"已自动关闭 {closeResult.Closed} 个正在运行的程序实例…"));
        }

        // 顺序很重要：先移除快捷方式与注册表项（此时程序文件还在，相关程序集可正常加载）
        progress?.Report(new UninstallProgress(5, "删除快捷方式…"));
        RemoveShortcuts();

        progress?.Report(new UninstallProgress(12, "清理注册表…"));
        RemoveUninstallRegistry();

        progress?.Report(new UninstallProgress(20, "正在删除程序文件…"));

        var outcome = await Task.Run(
            () => DeleteInstalledFiles(target, TimeSpan.FromSeconds(10), removeUserData, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new UninstallProgress(100, outcome.Summary));
        Log.Info($"卸载完成：{outcome.Summary}（{target}）");
        return outcome;
    }

    /// <summary>
    /// 尽力删除目录内文件：先重试若干秒（等待自身进程退出、文件解锁），
    /// 仍被占用（例如正在运行的 exe / 已映射的运行时 DLL）时注册为"重启后删除"。
    /// </summary>
    /// <summary>写入安装清单（相对安装目录的路径）。</summary>
    public static void WriteInstallManifest(string installDirectory, IEnumerable<string> relativeFiles)
    {
        try
        {
            Directory.CreateDirectory(installDirectory);

            var lines = relativeFiles
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Replace('\\', '/').TrimStart('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 卸载程序本身与清单也要记录（否则会残留）
            if (!lines.Contains(UninstallerName, StringComparer.OrdinalIgnoreCase)) lines.Add(UninstallerName);
            lines.Add(ManifestFileName);

            File.WriteAllLines(Path.Combine(installDirectory, ManifestFileName), lines);
            Log.Info($"已写入安装清单：{lines.Count} 个文件");
        }
        catch (Exception ex)
        {
            Log.Warn($"写入安装清单失败：{ex.Message}");
        }
    }

    /// <summary>读取安装清单；没有清单时返回空列表（旧版本安装）。</summary>
    public static IReadOnlyList<string> ReadInstallManifest(string installDirectory)
    {
        try
        {
            var path = Path.Combine(installDirectory, ManifestFileName);
            if (!File.Exists(path)) return Array.Empty<string>();

            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取安装清单失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 卸载：**只删除安装清单里记录的程序文件**，用户放进同一目录的 mod / 其它文件一律保留。
    /// 没有清单（老版本安装）时保守处理：只删本程序明显的程序文件（L4D2ModManager*/Uninstall.exe 等）。
    /// </summary>
    public static UninstallOutcome DeleteInstalledFiles(
        string directory,
        TimeSpan retryWindow,
        bool removeUserData = false,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + retryWindow;
        var deferred = new List<string>();
        int deleted = 0;

        var root = Path.GetFullPath(directory);
        var manifest = ReadInstallManifest(root);
        var useManifest = manifest.Count > 0;

        if (!useManifest)
        {
            Log.Warn($"安装目录里没有 {ManifestFileName}，将只删除本程序明显的程序文件，其余文件保持不动。");
        }

        var candidates = useManifest
            ? manifest.Select(rel => Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
                      .Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                      .Where(File.Exists)
                      .ToList()
            : Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                       .Where(LooksLikeProgramFile)
                       .ToList();

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryDeleteWithRetry(file, deadline))
            {
                deleted++;
            }
            else
            {
                // Windows 不允许删除"正在运行的映像"与"已加载的 DLL"，但允许给它们改名。
                // 改名挪到临时目录并登记重启后删除，程序目录就能立刻变干净（不再残留 Uninstall.exe 自己）。
                var relocated = TryRelocateLockedFile(file);

                if (relocated != null)
                {
                    // 已经挪出程序目录 => 对用户来说就是"删掉了"：
                    // 摘要里不再出现"被占用/重启后清理"，只在日志里留记录（重启时系统自动清掉临时文件）。
                    deleted++;
                    RegisterDeleteOnReboot(relocated);
                    continue;
                }
                else
                {
                    deferred.Add(file);
                    RegisterDeleteOnReboot(file);
                }
            }
        }

        // 清理因删除而变空的子目录（只删空目录，绝不递归删目录）
        if (useManifest)
        {
            RemoveEmptyDirectories(root, deleted);
        }

        if (removeUserData)
        {
            TryDeleteUserData();
        }

        return new UninstallOutcome(deleted, deferred.Count);
    }

    /// <summary>本程序明显的程序文件（无清单时的保守回退）。</summary>
    private static bool LooksLikeProgramFile(string path)
    {
        var name = Path.GetFileName(path);

        return name.Equals(UninstallerName, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("L4D2ModManager", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("WebView2", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Microsoft.Windows.SDK.NET.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("WinRT.Runtime.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDeleteWithRetry(string file, DateTime deadline)
    {
        while (true)
        {
            try
            {
                if (!File.Exists(file)) return true;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                return true;
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Log.Warn($"删除失败（将安排重启后删除）：{file} -> {ex.Message}");
                    return false;
                }

                Thread.Sleep(150);
            }
        }
    }

    /// <summary>
    /// 清理卸载过程在 %TEMP% 留下的垃圾：
    ///   · L4D2MM-Uninstall-leftover 目录（被改名挪出的旧文件，重启前一直占着空间）
    ///   · 之前版本复制过去的临时卸载程序副本（每份上百 MB）
    /// 只在能够删除时删除，失败一律忽略（正在运行的那个不会被删掉）。
    /// </summary>
    public static void CleanupUninstallLeftovers()
    {
        try
        {
            var leftover = Path.Combine(Path.GetTempPath(), "L4D2MM-Uninstall-leftover");
            if (Directory.Exists(leftover))
            {
                foreach (var file in Directory.EnumerateFiles(leftover))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                    catch { RegisterDeleteOnReboot(file); }
                }

                try { if (!Directory.EnumerateFileSystemEntries(leftover).Any()) Directory.Delete(leftover); } catch { }
            }

            var temp = Path.GetTempPath();
            foreach (var pattern in new[] { "Uninstall-*.exe", "*_Uninstall.exe" })
            {
                foreach (var file in Directory.EnumerateFiles(temp, pattern))
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(Environment.ProcessPath ?? string.Empty), StringComparison.OrdinalIgnoreCase))
                            continue;

                        File.Delete(file);
                        Log.Info($"已清理临时卸载副本：{Path.GetFileName(file)}");
                    }
                    catch
                    {
                        // 被占用（正在运行）或权限不足：留给重启后清理
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理临时文件失败：{ex.Message}");
        }
    }
    /// <summary>把被占用的文件改名挪到临时目录（成功返回新路径），用于清理"运行中的"卸载程序与已加载 DLL。</summary>
    private static string? TryRelocateLockedFile(string file)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "L4D2MM-Uninstall-leftover");
            Directory.CreateDirectory(folder);

            var target = Path.Combine(
                folder,
                Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + Path.GetFileName(file));

            File.Move(file, target);
            Log.Info($"文件被占用，已改名挪出程序目录：{Path.GetFileName(file)} -> {target}");
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn($"挪出被占用文件失败：{file} -> {ex.Message}");
            return null;
        }
    }

    /// <summary>删除空的子目录（自底向上），非空目录一律保留。</summary>
    private static void RemoveEmptyDirectories(string root, int deletedCount)
    {
        if (deletedCount == 0) return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch
                {
                    // 非空或占用：保留
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理空目录失败：{ex.Message}");
        }
    }

    private static void TryDeleteUserData()
    {
        try
        {
            if (Directory.Exists(UserDataDirectory))
            {
                Directory.Delete(UserDataDirectory, recursive: true);
                Log.Info($"已删除用户数据目录：{UserDataDirectory}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除用户数据失败：{ex.Message}");
        }
    }

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
