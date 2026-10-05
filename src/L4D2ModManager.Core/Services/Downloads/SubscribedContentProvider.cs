using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Steam;

namespace L4D2ModManager.Core.Services.Downloads;

/// <summary>
/// 通道二：Steam 订阅缓存。
/// 若用户在 Steam 中已订阅该 Mod（或曾经订阅过），内容会出现在
/// steamapps\workshop\content\550\&lt;id&gt;\ 中，这里直接复制到目标 Mod 目录。
/// 这是最可靠的“下载”方式：不需要第三方服务，也不会被 Steam 接口限制。
/// </summary>
public sealed class SubscribedContentProvider : IWorkshopDownloadProvider
{
    private readonly Func<AppConfig> _config;
    private readonly Func<SteamPaths> _paths;

    public SubscribedContentProvider(Func<AppConfig> config, Func<SteamPaths> paths)
    {
        _config = config;
        _paths = paths;
    }

    public string Name => "Steam 订阅缓存";

    public string Description => "从 steamapps\\workshop\\content\\550 复制已订阅的 Mod 文件";

    public bool CanHandle(DownloadTask task, WorkshopItemInfo info) =>
        FindContentDirectory(task.WorkshopId ?? info.PublishedFileId) != null;

    public Task<DownloadResult> DownloadAsync(
        DownloadTask task,
        WorkshopItemInfo info,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var id = task.WorkshopId ?? info.PublishedFileId;
        var contentDirectory = FindContentDirectory(id);
        if (contentDirectory == null)
            return Task.FromResult(DownloadResult.Fail("未找到订阅缓存目录"));

        var destination = string.IsNullOrWhiteSpace(task.DestinationDirectory)
            ? AppPaths.DownloadTempDir
            : task.DestinationDirectory;

        try
        {
            var sources = Directory
                .EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories)
                .Where(ModScanner.IsModFile)
                .ToList();

            if (sources.Count == 0)
                return Task.FromResult(DownloadResult.Fail("订阅目录中没有 .vpk 文件"));

            string? first = null;
            long total = sources.Sum(s => new FileInfo(s).Length);
            long copied = 0;

            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                task.PauseGate.Wait(cancellationToken);

                var target = DownloadFileHelper.UniquePath(destination, Path.GetFileName(source));

                // 已经复制过同名文件则跳过
                var existing = Directory.EnumerateFiles(destination, Path.GetFileName(source), SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (existing != null && new FileInfo(existing).Length == new FileInfo(source).Length)
                {
                    first ??= existing;
                    copied += new FileInfo(source).Length;
                    progress?.Report(new DownloadProgress(copied, total, 0, $"已存在：{Path.GetFileName(source)}"));
                    continue;
                }

                File.Copy(source, target, overwrite: false);
                copied += new FileInfo(source).Length;
                progress?.Report(new DownloadProgress(copied, total, 0, $"复制 {Path.GetFileName(source)}"));
                first ??= target;
            }

            Log.Info($"订阅缓存通道完成：{info.Title} -> {first}");
            return Task.FromResult(DownloadResult.Ok(first!));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"订阅缓存通道失败: {ex.Message}");
            return Task.FromResult(DownloadResult.Fail(ex.Message));
        }
    }

    private string? FindContentDirectory(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        try
        {
            foreach (var root in _paths().WorkshopDirectories)
            {
                var directory = Path.Combine(root, id);
                if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.vpk", SearchOption.AllDirectories).Any())
                    return directory;
            }

            foreach (var modDirectory in _config().ModDirectories)
            {
                var directory = Path.Combine(modDirectory, id);
                if (Directory.Exists(directory)) return directory;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"查找订阅缓存失败: {ex.Message}");
        }

        return null;
    }
}
