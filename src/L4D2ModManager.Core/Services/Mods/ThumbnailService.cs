using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Vpk;

namespace L4D2ModManager.Core.Services.Mods;

/// <summary>
/// 缩略图服务：
///   1) 优先从 VPK 内提取 addonimage.jpg / addonimage.png
///   2) 没有内置图片时，用创意工坊预览图（preview_url）下载缓存
/// 所有图片都缓存在 %AppData%\L4D2ModManager\thumbnails。
/// </summary>
public sealed class ThumbnailService
{
    private static readonly string[] SupportedExtensions = { "jpg", "jpeg", "png", "bmp", "gif", "webp" };

    private readonly Workshop.SteamWorkshopClient _workshopClient;
    private readonly HttpClient _http;

    public ThumbnailService(Workshop.SteamWorkshopClient workshopClient)
    {
        _workshopClient = workshopClient;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    /// <summary>从 VPK 中提取内置图片。</summary>
    public string? ExtractFromVpk(ModItem item, VpkArchive archive)
    {
        try
        {
            var entry = archive.FindAddonImage();
            if (entry == null) return null;

            var extension = entry.Extension.ToLowerInvariant();
            if (!SupportedExtensions.Contains(extension)) return null;
            if (entry.TotalLength <= 0 || entry.TotalLength > 32 * 1024 * 1024) return null;

            var bytes = archive.Read(entry);
            if (bytes == null || bytes.Length < 64) return null;

            var key = string.IsNullOrWhiteSpace(item.Key)
                ? ModItem.MakeKey(item.FilePath, item.WorkshopId)
                : item.Key;

            var target = AppPaths.ThumbnailPathFor(key, extension);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);

            Log.Info($"提取内置缩略图：{Path.GetFileName(item.FilePath)} -> {Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn($"提取缩略图失败: {item.FilePath} -> {ex.Message}");
            return null;
        }
    }

    /// <summary>下载创意工坊预览图并缓存。</summary>
    public async Task<string?> DownloadPreviewAsync(ModItem item, string previewUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(previewUrl)) return null;

            var extension = GuessExtension(previewUrl);
            var key = string.IsNullOrWhiteSpace(item.Key)
                ? ModItem.MakeKey(item.FilePath, item.WorkshopId)
                : item.Key;

            var target = AppPaths.ThumbnailPathFor(key, extension);
            if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

            var bytes = await _http.GetByteArrayAsync(previewUrl, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 64) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            Log.Info($"缓存工坊预览图：{item.DisplayName} -> {Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn($"下载预览图失败: {previewUrl} -> {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 确保 Mod 有缩略图：内置图片已提取过则直接返回，否则尝试用工坊预览图。
    /// </summary>
    public async Task<string?> EnsureAsync(ModItem item, bool allowRemote, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
            return item.ThumbnailPath;

        var existing = AppPaths.FindExistingThumbnail(item.Key);
        if (existing != null)
        {
            item.ThumbnailPath = existing;
            return existing;
        }

        // 兜底 ①：直接从 VPK 里提取内置图片（完全不需要网络）
        if (!string.IsNullOrWhiteSpace(item.FilePath) &&
            Path.GetExtension(item.FilePath).Equals(".vpk", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(item.FilePath))
        {
            try
            {
                var archive = VpkReader.TryOpen(item.FilePath, out _);
                if (archive != null)
                {
                    using (archive)
                    {
                        var extracted = ExtractFromVpk(item, archive);
                        if (extracted != null) return extracted;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"从 VPK 提取缩略图失败（{item.FilePath}）：{ex.Message}");
            }
        }

        if (!allowRemote) return null;

        // 需要工坊信息才能拿到 preview_url
        var id = item.WorkshopId ?? ModScanner.GuessWorkshopId(item.FilePath);
        if (string.IsNullOrWhiteSpace(id)) return null;

        // ① 官方 API（部分网络下不可达）
        try
        {
            var detail = await _workshopClient.GetDetailAsync(id, cancellationToken).ConfigureAwait(false);
            if (detail?.PreviewUrl != null)
            {
                var fromApi = await DownloadPreviewAsync(item, detail.PreviewUrl, cancellationToken).ConfigureAwait(false);
                if (fromApi != null)
                {
                    item.ThumbnailPath = fromApi;
                    return fromApi;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"工坊 API 获取预览图失败（{id}）：{ex.Message}");
        }

        // ② 退化为抓工坊物品页的 og:image：网页能打开就能拿到预览图
        var fromPage = await TryPageImageAsync(item, id, cancellationToken).ConfigureAwait(false);
        if (fromPage != null) item.ThumbnailPath = fromPage;
        return fromPage;
    }

    /// <summary>从工坊物品页的 &lt;meta property="og:image"&gt; 取预览图（不依赖官方 API）。</summary>
    private async Task<string?> TryPageImageAsync(ModItem item, string workshopId, CancellationToken cancellationToken)
    {
        try
        {
            // 工坊物品页有两种地址形式，都试一遍
            var urls = new[]
            {
                "https://steamcommunity.com/sharedfiles/filedetails/?id=" + Uri.EscapeDataString(workshopId),
                "https://steamcommunity.com/workshop/filedetails/?id=" + Uri.EscapeDataString(workshopId),
            };

            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Match.Empty;

            foreach (var url in urls)
            {
                string html;
                try
                {
                    html = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"抓取工坊页面失败（{url}）：{ex.Message}");
                    continue;
                }

                match = System.Text.RegularExpressions.Regex.Match(
                    html,
                    "<meta[^>]+property=[\"']og:image[\"'][^>]+content=[\"']([^\"']+)[\"']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success)
                {
                    match = System.Text.RegularExpressions.Regex.Match(
                        html,
                        "<meta[^>]+content=[\"']([^\"']+)[\"'][^>]+property=[\"']og:image[\"']",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }

                if (match.Success) break;
            }

            if (!match.Success)
            {
                Log.Warn($"工坊页面里没有找到 og:image（{workshopId}）");
                return null;
            }

            var imageUrl = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            Log.Info($"从工坊页面取到预览图：{workshopId} -> {imageUrl}");
            return await DownloadPreviewAsync(item, imageUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"抓取工坊页面预览图失败（{workshopId}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>删除某个 Mod 的缩略图缓存。</summary>
    public void DeleteFor(ModItem item)
    {
        try
        {
            foreach (var extension in SupportedExtensions)
            {
                var path = AppPaths.ThumbnailPathFor(item.Key, extension);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除缩略图失败: {item.Key} -> {ex.Message}");
        }
    }

    private static string GuessExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return SupportedExtensions.Contains(extension) ? extension : "jpg";
        }
        catch
        {
            return "jpg";
        }
    }
}
