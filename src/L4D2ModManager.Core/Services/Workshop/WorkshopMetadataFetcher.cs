using System.Text.Json;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services.Mods;

namespace L4D2ModManager.Core.Services.Workshop;

/// <summary>抓取结果汇总。</summary>
public sealed record WorkshopMetadataSummary(int Images, int Tags, int Failed, int Total)
{
    public string Text => $"缩略图 {Images} 个，标签 {Tags} 个，失败 {Failed} 个（共 {Total} 个）";
}

/// <summary>
/// 通过"能在本机打开的创意工坊页面"批量抓取缩略图与标签。
///
/// 为什么不用官方 API：api.steampowered.com 在部分网络下不可达，而工坊网页能打开。
/// 因此这里把工作交给内嵌浏览器：在页面内做同源 fetch 取物品页 HTML，
/// 宿主解析 og:image（下载并缓存到本地缩略图目录）与 workshop/taglist 标签
/// （标签写回 ModItem.Tags 并用 CategoryClassifier 重新判定类型）。
/// 页面脚本执行器由 View 注入，Mod 管理与创意工坊两个页面共用。
/// </summary>
public sealed class WorkshopMetadataFetcher
{

    private sealed class Meta
    {
        public string Id { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
    }

    private readonly ModLibraryService _library;

    public WorkshopMetadataFetcher(ModLibraryService library) => _library = library;

    /// <summary>需要抓取的工坊 ID（按 Mod 去重）。</summary>
    public IReadOnlyList<string> CollectTargets() =>
        _library.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m.WorkshopId))
            .GroupBy(m => m.WorkshopId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .ToList();

    /// <summary>执行抓取（每批 5 个）。</summary>
    public async Task<WorkshopMetadataSummary> FetchAsync(
        Func<string, Task<string?>> runScript,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = CollectTargets();
        int images = 0, tags = 0, failed = 0;

        for (int index = 0; index < targets.Count; index += 5)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = targets.Skip(index).Take(5).ToList();
            progress?.Report($"正在抓取工坊信息：{index}/{targets.Count}…");

            var json = await runScript(BuildScript(batch)).ConfigureAwait(false);
            var metas = Parse(json);

            if (metas.Count == 0)
            {
                failed += batch.Count;
                continue;
            }

            foreach (var meta in metas)
            {
                var item = _library.Mods.FirstOrDefault(m =>
                    string.Equals(m.WorkshopId, meta.Id, StringComparison.OrdinalIgnoreCase));
                if (item == null) continue;

                if (!string.IsNullOrWhiteSpace(meta.Image))
                {
                    var path = await _library.Thumbnails.DownloadPreviewAsync(item, meta.Image, cancellationToken)
                        .ConfigureAwait(false);
                    if (path != null)
                    {
                        item.ThumbnailPath = path;
                        images++;
                    }
                }

                if (meta.Tags.Count > 0)
                {
                    item.Tags = string.Join(", ", meta.Tags);
                    item.Category = CategoryClassifier.Classify(
                        item.DisplayName, item.FileIndex, item.Tags, item.Description);
                    tags++;
                }
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        _library.SaveDatabase();
        Log.Info($"[工坊抓取] {new WorkshopMetadataSummary(images, tags, failed, targets.Count).Text}");

        return new WorkshopMetadataSummary(images, tags, failed, targets.Count);
    }

    /// <summary>在页面里批量取 og:image 与标签，返回 JSON 字符串。</summary>
    internal static string BuildScript(IReadOnlyList<string> ids)
    {
        var list = string.Join(",", ids.Select(id => "\"" + id + "\""));

        return
            "(async () => { const ids = [" + list + "]; const out = [];" +
            " for (const id of ids) {" +
            "  try {" +
            "   const r = await fetch('https://steamcommunity.com/sharedfiles/filedetails/?id=' + id, { credentials: 'include' });" +
            "   const html = await r.text();" +
            "   let og = html.match(/<meta[^>]+property=[\"']og:image[\"'][^>]+content=[\"']([^\"']+)[\"']/i);" +
            "   if (!og) { og = html.match(/<meta[^>]+content=[\"']([^\"']+)[\"'][^>]+property=[\"']og:image[\"']/i); }" +
            "   const tags = [];" +
            "   const re = /workshop\\/taglist\\/\\?tag=([^\"'&]+)/gi; let t;" +
            "   while ((t = re.exec(html)) !== null) { const v = decodeURIComponent(t[1]); if (tags.indexOf(v) < 0) { tags.push(v); } }" +
            "   out.push({ id: id, image: og ? og[1] : '', tags: tags });" +
            "  } catch (e) { out.push({ id: id, image: '', tags: [] }); }" +
            " }" +
            " return JSON.stringify(out); })()";
    }

    /// <summary>解析页面返回的 JSON（ExecuteScriptAsync 的返回值是"字符串里套 JSON"）。</summary>
    internal static List<(string Id, string Image, List<string> Tags)> ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<(string, string, List<string>)>();

        try
        {
            var inner = json.Trim();
            if (inner.StartsWith("\"", StringComparison.Ordinal))
            {
                inner = JsonSerializer.Deserialize<string>(inner) ?? inner;
            }

            var metas = JsonSerializer.Deserialize<List<Meta>>(
                            inner, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new List<Meta>();

            return metas.Select(m => (m.Id, m.Image, m.Tags)).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"解析工坊抓取结果失败：{ex.Message}");
            return new List<(string, string, List<string>)>();
        }
    }

    private static List<Meta> Parse(string? json) =>
        ParseJson(json).Select(t => new Meta { Id = t.Id, Image = t.Image, Tags = t.Tags }).ToList();
}
