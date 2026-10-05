using System.Diagnostics;
using System.Text;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Steam;

namespace L4D2ModManager.Core.Services.Downloads;

/// <summary>
/// 通道三：steamcmd。
/// 执行：steamcmd +login anonymous +workshop_download_item 550 &lt;id&gt; +quit
/// 下载完成后从 steamcmd\steamapps\workshop\content\550\&lt;id&gt;\ 复制 .vpk 到目标目录。
///
/// 说明：Left 4 Dead 2 的创意工坊内容对匿名登录并不总是开放，
/// 若匿名下载被拒绝，请改用“Steam 订阅缓存”通道（在 Steam 中订阅后本程序会自动复制）。
/// </summary>
public sealed class SteamCmdProvider : IWorkshopDownloadProvider
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly Func<AppConfig> _config;
    private readonly Func<SteamPaths> _paths;

    public SteamCmdProvider(Func<AppConfig> config, Func<SteamPaths> paths)
    {
        _config = config;
        _paths = paths;
    }

    public string Name => "steamcmd";

    public string Description => "调用 steamcmd 的 workshop_download_item 命令下载";

    public bool CanHandle(DownloadTask task, WorkshopItemInfo info) => ResolveSteamCmd() != null;

    public async Task<DownloadResult> DownloadAsync(
        DownloadTask task,
        WorkshopItemInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var steamcmd = ResolveSteamCmd();
        if (steamcmd == null)
            return DownloadResult.Fail("未找到 steamcmd.exe（可在“设置”中手动指定路径）", permanent: true);

        var id = string.IsNullOrWhiteSpace(task.WorkshopId) ? info.PublishedFileId : task.WorkshopId!;
        if (string.IsNullOrWhiteSpace(id))
            return DownloadResult.Fail("缺少创意工坊 ID", permanent: true);

        var workDirectory = Path.GetDirectoryName(steamcmd) ?? AppPaths.DownloadTempDir;

        var startInfo = new ProcessStartInfo(steamcmd)
        {
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in new[]
                 {
                     "+login", "anonymous",
                     "+workshop_download_item", SteamLibraryLocator.L4D2AppId.ToString(), id,
                     "+quit",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (output) output.AppendLine(e.Data);
                progress?.Report(new DownloadProgress(0, 0, 0, e.Data.Trim()));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (output) output.AppendLine(e.Data);
                progress?.Report(new DownloadProgress(0, 0, 0, e.Data.Trim()));
            };

            if (!process.Start())
                return DownloadResult.Fail("无法启动 steamcmd");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                process.WaitForExit(); // 确保异步输出读取完毕
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return DownloadResult.Fail("steamcmd 超时（15 分钟）");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var log = output.ToString();
            var contentDirectory = Path.Combine(workDirectory, "steamapps", "workshop", "content",
                SteamLibraryLocator.L4D2AppId.ToString(), id);

            var sources = Directory.Exists(contentDirectory)
                ? Directory.EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories).Where(ModScanner.IsModFile).ToList()
                : new List<string>();

            if (sources.Count == 0)
            {
                var hint = log.Contains("Login Failure", StringComparison.OrdinalIgnoreCase) ||
                           log.Contains("FAILED login", StringComparison.OrdinalIgnoreCase)
                    ? "steamcmd 匿名登录失败。Left 4 Dead 2 的工坊内容通常需要拥有游戏的账号：请在 Steam 中订阅该 Mod，" +
                      "本程序会通过“Steam 订阅缓存”通道自动复制；也可在设置中指定 steamcmd 路径后使用带账号登录的 steamcmd。"
                    : "steamcmd 未下载到任何文件。";

                Log.Warn($"steamcmd 下载失败：{hint}");
                return DownloadResult.Fail(hint);
            }

            var destination = string.IsNullOrWhiteSpace(task.DestinationDirectory)
                ? AppPaths.DownloadTempDir
                : task.DestinationDirectory;

            string? first = null;
            long total = sources.Sum(s => new FileInfo(s).Length);
            long copied = 0;

            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = DownloadFileHelper.UniquePath(destination, Path.GetFileName(source));
                File.Copy(source, target, overwrite: false);
                copied += new FileInfo(source).Length;
                progress?.Report(new DownloadProgress(copied, total, 0, $"复制 {Path.GetFileName(source)}"));
                first ??= target;
            }

            Log.Info($"steamcmd 通道完成：{info.Title} -> {first}");
            return DownloadResult.Ok(first!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"steamcmd 通道异常: {ex.Message}");
            return DownloadResult.Fail(ex.Message);
        }
    }

    public string? ResolveSteamCmd()
    {
        try
        {
            var configured = _config().SteamCmdPath;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var detected = SteamLibraryLocator.FindSteamCmd(_config().SteamRootPath ?? _paths().SteamRoot);
            if (detected != null) return detected;

            if (!string.IsNullOrWhiteSpace(_paths().SteamCmdPath) && File.Exists(_paths().SteamCmdPath))
                return _paths().SteamCmdPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"查找 steamcmd 失败: {ex.Message}");
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 忽略
        }
    }
}
