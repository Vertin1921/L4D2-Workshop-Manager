using System.Windows;
using System.Windows.Media.Imaging;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.App.Services;

/// <summary>
/// 缩略图加载器：优先 VPK 内置图片，其次创意工坊预览图（会缓存到本地）。
/// 解码在 UI 线程完成并 Freeze，避免跨线程问题。
/// </summary>
public sealed class ThumbnailLoader
{
    private readonly ModLibraryService _library;
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly Dictionary<string, BitmapImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public ThumbnailLoader(ModLibraryService library) => _library = library;

    /// <summary>获取缩略图（可能为 null）。</summary>
    public async Task<BitmapImage?> LoadAsync(ModItem item, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(item.Key) ? item.FilePath : item.Key;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (_failed.Contains(key)) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;

            var path = ResolvePath(item);
            if (path == null)
            {
                var allowRemote = _library.Config.FetchRemoteThumbnails;
                path = await _library.Thumbnails.EnsureAsync(item, allowRemote, cancellationToken).ConfigureAwait(true);
            }

            if (path == null || !File.Exists(path))
            {
                _failed.Add(key);
                _cache[key] = null;
                return null;
            }

            var image = await CreateImageAsync(path).ConfigureAwait(true);
            if (image == null) _failed.Add(key);
            _cache[key] = image;
            return image;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清空缓存（重新扫描或删除 Mod 后调用）。</summary>
    public void Clear()
    {
        _cache.Clear();
        _failed.Clear();
    }

    public void Invalidate(ModItem item)
    {
        var key = string.IsNullOrWhiteSpace(item.Key) ? item.FilePath : item.Key;
        _cache.Remove(key);
        _failed.Remove(key);
    }

    private static string? ResolvePath(ModItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
            return item.ThumbnailPath;

        var existing = AppPaths.FindExistingThumbnail(item.Key);
        if (existing != null)
        {
            item.ThumbnailPath = existing;
            return existing;
        }

        return null;
    }

    private static Task<BitmapImage?> CreateImageAsync(string path)
    {
        // 解码放到线程池执行：BitmapImage 以 OnLoad + Freeze 的方式创建可以安全跨线程，
        // 因此滚动或批量加载缩略图时不会占用 UI 线程（此前掉帧的主要原因之一）。
        return Task.Run(() => CreateImage(path));
    }

    private static BitmapImage? CreateImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 240;   // 卡片显示宽度约 116px，2 倍图足够清晰且解码更快
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取缩略图失败: {path} -> {ex.Message}");
            return null;
        }
    }
}
