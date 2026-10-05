using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Deployment;

namespace L4D2ModManager.Setup;

public sealed class InstallOptions
{
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>在所选目录下再创建一个子目录（L4D2 Mod Manager）后安装。</summary>
    public bool CreateSubdirectory { get; set; }
    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
    public bool WriteRegistry { get; set; } = true;
}

public sealed class InstallProgress
{
    public int Percent { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 安装程序核心逻辑：从嵌入资源 payload.zip 解压出主程序，
/// 然后交给 <see cref="AppDeployment"/> 创建快捷方式、写卸载注册表、卸载程序文件。
/// 卸载由主程序自己执行（L4D2ModManager.exe --uninstall / --uninstall --silent），
/// 因此安装目录不需要额外放一份几百 MB 的卸载器。
/// </summary>
public static class Installer
{
    public const string AppName = AppDeployment.AppName;
    public const string AppShortName = AppDeployment.AppShortName;
    public const string AppVersion = AppInfo.Version;
    public const string AppId = AppDeployment.AppId;
    public const string PayloadResourceName = "L4D2ModManager.Setup.payload.zip";
    public const string PayloadFileName = "payload.zip";
    public const string ExecutableName = AppDeployment.ExecutableName;

    private static string? _payloadPath;

    /// <summary>
    /// 查找安装载荷：默认是 Setup.exe 同级的 payload.zip（不再把 62MB 载荷嵌进 exe，
    /// 这是显著降低杀软误报的关键改动）。也兼容内嵌资源的单文件安装包。
    /// </summary>
    public static string? FindPayloadFile()
    {
        if (_payloadPath != null && File.Exists(_payloadPath)) return _payloadPath;

        _payloadPath = InstallPayload.Find(AppContext.BaseDirectory);
        return _payloadPath;
    }

    private static bool EmbeddedPayloadAvailable =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadResourceName) != null;

    public static bool PayloadAvailable => FindPayloadFile() != null || EmbeddedPayloadAvailable;

    public static string PayloadLocation => FindPayloadFile() ?? (EmbeddedPayloadAvailable ? "内嵌资源" : "(未找到)");

    public static long PayloadSize
    {
        get
        {
            var file = FindPayloadFile();
            if (file != null) return new FileInfo(file).Length;

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
            return stream?.Length ?? 0;
        }
    }

    private static Stream OpenPayload()
    {
        var file = FindPayloadFile();
        if (file != null)
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        return Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
               ?? throw new InvalidOperationException(
                   $"找不到安装载荷 {PayloadFileName}。\r\n" +
                   "请确保 payload.zip 与 Setup.exe 位于同一目录（或使用单文件安装包）。");
    }

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

    public static string DefaultTargetDirectory
    {
        get
        {
            if (IsElevated)
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(programFiles))
                    return Path.Combine(programFiles, AppShortName);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppShortName);
        }
    }

    public static string UserDataDirectory => AppDeployment.UserDataDirectory;

    public static bool TryGetInstalledInfo(out string? location, out string? version)
    {
        var info = AppDeployment.TryGetInstalledInfo();
        location = info?.Location;
        version = info?.Version;
        return info != null;
    }

    /// <summary>从安装载荷（外置 zip 或内嵌资源）生成安装清单。</summary>
    private static void WriteInstallManifest(string target, string? payloadPath)
    {
        try
        {
            var entries = new List<string>();

            if (payloadPath != null)
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(payloadPath);
                entries.AddRange(zip.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .Select(e => e.FullName));
            }
            else
            {
                using var stream = OpenPayload();
                using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
                entries.AddRange(zip.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .Select(e => e.FullName));
            }

            AppDeployment.WriteInstallManifest(target, entries);
        }
        catch (Exception ex)
        {
            Log.Warn($"生成安装清单失败（卸载时会退化为只删程序文件）：{ex.Message}");
        }
    }

    /// <summary>执行安装。</summary>
    public static async Task InstallAsync(InstallOptions options, IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!PayloadAvailable)
        {
            throw new InvalidOperationException(
                $"安装包中缺少程序载荷（{PayloadFileName}）。\r\n\r\n" +
                "请把 payload.zip 与 Setup.exe 放在同一个目录，或使用 build\\build-release.ps1 重新生成安装包。");
        }

        var target = string.IsNullOrWhiteSpace(options.TargetDirectory)
            ? DefaultTargetDirectory
            : options.TargetDirectory.Trim();
        target = Path.GetFullPath(target);

        // 勾选"创建子目录"时，在所选目录下再建一层（例如 D:\Games → D:\Games\L4D2 Mod Manager）
        if (options.CreateSubdirectory &&
            !target.TrimEnd(Path.DirectorySeparatorChar).EndsWith(AppShortName, StringComparison.OrdinalIgnoreCase))
        {
            target = Path.Combine(target, AppShortName);
        }

        // 覆盖更新：先关掉正在运行的主程序，否则 exe/dll 被占用会替换失败
        progress?.Report(new InstallProgress { Percent = 1, Message = "检查正在运行的程序…" });
        var closeResult = AppDeployment.CloseRunningInstances();
        if (closeResult.Closed > 0)
        {
            progress?.Report(new InstallProgress
            {
                Percent = 1,
                Message = $"已自动关闭 {closeResult.Closed} 个正在运行的程序实例…",
            });
        }

        if (closeResult.Remaining > 0)
        {
            throw new InvalidOperationException(
                $"检测到 {closeResult.Remaining} 个 L4D2 Mod Manager 仍在运行（可能是以管理员身份启动的），无法覆盖更新。\r\n\r\n" +
                "请先手动退出程序（或在任务管理器中结束 L4D2ModManager.exe）后重试。");
        }

        progress?.Report(new InstallProgress { Percent = 2, Message = "准备安装目录…" });
        Directory.CreateDirectory(target);

        var reporter = progress == null
            ? null
            : new Progress<(int Percent, string Message)>(p => progress.Report(new InstallProgress { Percent = p.Percent, Message = p.Message }));

        // 解压逻辑与自检程序共用（Core 的 AppDeployment.ExtractPayloadAsync）
        var payloadPath = FindPayloadFile();
        if (payloadPath != null)
        {
            await AppDeployment.ExtractPayloadAsync(payloadPath, target, reporter, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 单文件安装包：载荷在嵌入资源里，先落到临时文件再解压
            var temporary = Path.Combine(Path.GetTempPath(), $"l4d2mm-payload-{Guid.NewGuid():N}.zip");
            try
            {
                await Task.Run(() =>
                {
                    using var payload = OpenPayload();
                    using var file = File.Create(temporary);
                    payload.CopyTo(file);
                }, cancellationToken).ConfigureAwait(false);

                await AppDeployment.ExtractPayloadAsync(temporary, target, reporter, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // 忽略
                }
            }
        }

        progress?.Report(new InstallProgress { Percent = 88, Message = "创建快捷方式…" });
        // 记录安装清单（卸载时只删这些文件，用户的 mod 一律保留）
        WriteInstallManifest(target, payloadPath);

        AppDeployment.CreateUninstaller(target);
        AppDeployment.CreateShortcuts(target, options.StartMenuShortcut, options.DesktopShortcut);

        if (options.WriteRegistry)
        {
            progress?.Report(new InstallProgress { Percent = 94, Message = "写入卸载信息…" });
            long size = 0;
            try
            {
                size = new DirectoryInfo(target).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch
            {
                // 忽略
            }

            AppDeployment.WriteUninstallRegistry(target, size);
        }

        progress?.Report(new InstallProgress { Percent = 100, Message = "安装完成" });

        if (options.LaunchAfterInstall)
        {
            var executable = Path.Combine(target, ExecutableName);
            if (File.Exists(executable))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = target, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Warn($"启动主程序失败: {ex.Message}");
                }
            }
        }
    }

    /// <summary>执行卸载（与主程序的 --uninstall 使用同一套逻辑）。</summary>
    public static async Task<UninstallOutcome> UninstallAsync(string? installDirectory, bool removeUserData,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var reporter = progress == null
            ? null
            : new Progress<UninstallProgress>(p => progress.Report(new InstallProgress { Percent = p.Percent, Message = p.Message }));

        return await AppDeployment.UninstallAsync(installDirectory, removeUserData, reporter, cancellationToken)
            .ConfigureAwait(false);
    }
}
