using System.Net;
using System.Net.Http;
using System.Text.Json;
using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Workshop;

/// <summary>
/// 大陆镜像站（zhrradiant）元数据客户端。
///
/// 国内访问 Steam 官方工坊接口经常直接超时（实测 45 秒都不返回），
/// 而镜像站通常 2 秒左右就能给出标题 / 描述 / 预览图 / 标签 / 作者，
/// 因此在 Steam 接口拿不到条目信息时用它兜底（界面就不再显示"尚未获取任何条目信息"）。
/// </summary>
public static class MirrorWorkshopClient
{
    /// <summary>镜像站 REST 基址（WordPress 插件 l4d2-workshop/v1）。</summary>
    public const string ApiBase = "https://zhrradiant.com/wp-json/l4d2-workshop/v1";

    private const string MirrorReferer = "https://zhrradiant.com/tools/l4d2-workshop";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>按工坊 ID 取条目信息；失败返回 null（不抛异常）。</summary>
    public static async Task<WorkshopItemInfo?> TryGetDetailAsync(string workshopId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workshopId)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/detail?id={Uri.EscapeDataString(workshopId)}");
            request.Headers.Referrer = new Uri(MirrorReferer);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"大陆镜像站 detail 返回 HTTP {(int)response.StatusCode}（{workshopId}）");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                root = data;
            }

            if (root.ValueKind != JsonValueKind.Object) return null;

            var info = new WorkshopItemInfo
            {
                PublishedFileId = ReadString(root, "id") ?? workshopId,
                Title = ReadString(root, "title") ?? string.Empty,
                Description = ReadString(root, "description") ?? string.Empty,
                Author = ReadString(root, "creator") ?? string.Empty,
                CreatorName = ReadString(root, "creator_name"),
                PreviewUrl = ReadString(root, "preview"),
                FileUrl = ReadString(root, "file_url"),
                FileName = ReadString(root, "filename") ?? ReadString(root, "file_name"),
                Available = true,
            };

            if (TryReadNumber(root, "file_size", out var size) ||
                TryReadNumber(root, "file_size_bytes", out size) ||
                TryReadNumber(root, "size", out size))
            {
                info.FileSize = size;
            }

            if (TryReadNumber(root, "subscriptions", out var subs)) info.Subscriptions = (int)subs;
            if (TryReadNumber(root, "favorited", out var fav)) info.Favorited = (int)fav;
            if (TryReadNumber(root, "views", out var views)) info.Views = (int)views;

            if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String)
                    {
                        var text = tag.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) info.Tags.Add(text);
                    }
                    else if (tag.ValueKind == JsonValueKind.Object)
                    {
                        var text = ReadString(tag, "display_name") ?? ReadString(tag, "tag");
                        if (!string.IsNullOrWhiteSpace(text)) info.Tags.Add(text!);
                    }
                }
            }

            // 预览图也是走加速线路的地址；若镜像站没给，就用图集里的第一张
            if (string.IsNullOrWhiteSpace(info.PreviewUrl) &&
                root.TryGetProperty("previews", out var previews) && previews.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in previews.EnumerateArray())
                {
                    var thumb = ReadString(item, "thumb") ?? ReadString(item, "url");
                    if (!string.IsNullOrWhiteSpace(thumb)) { info.PreviewUrl = thumb; break; }
                }
            }

            Log.Info($"大陆镜像站已返回条目信息：{workshopId} -> {info.Title}");
            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"大陆镜像站元数据查询失败（{workshopId}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按关键词搜索工坊条目（走镜像站，国内不挂梯子也能用）。
    /// 返回顺序未定义：镜像站的 sort 参数不保证生效，由调用方按字段自行排序。
    /// </summary>
    public static async Task<IReadOnlyList<WorkshopItemInfo>> SearchAsync(
        string? query,
        int num = 40,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WorkshopItemInfo>();

        try
        {
            var parts = new List<string> { $"page={page}", $"num={num}" };
            if (!string.IsNullOrWhiteSpace(query)) parts.Add("search=" + Uri.EscapeDataString(query!));
            var url = $"{ApiBase}/query?" + string.Join("&", parts);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(MirrorReferer);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"镜像站搜索返回 HTTP {(int)response.StatusCode}");
                return results;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var parsed = ParseItem(item);
                    if (parsed != null) results.Add(parsed);
                }
            }

            Log.Info($"镜像站搜索完成：\"{query}\" → {results.Count} 条");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"镜像站搜索失败：{ex.Message}");
        }

        return results;
    }

    /// <summary>把镜像站列表项解析成条目信息。</summary>
    private static WorkshopItemInfo? ParseItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = ReadString(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var info = new WorkshopItemInfo
        {
            PublishedFileId = id!,
            Title = ReadString(item, "title") ?? string.Empty,
            Description = ReadString(item, "description") ?? string.Empty,
            Author = ReadString(item, "creator") ?? string.Empty,
            CreatorName = ReadString(item, "creator_name"),
            PreviewUrl = ReadString(item, "preview"),
            FileUrl = ReadString(item, "file_url"),
            Available = true,
        };

        if (TryReadNumber(item, "subscriptions", out var subs)) info.Subscriptions = (int)subs;
        if (TryReadNumber(item, "favorited", out var fav)) info.Favorited = (int)fav;
        if (TryReadNumber(item, "views", out var views)) info.Views = (int)views;

        if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var text = tag.ValueKind == JsonValueKind.String
                    ? tag.GetString()
                    : (ReadString(tag, "display_name") ?? ReadString(tag, "tag"));
                if (!string.IsNullOrWhiteSpace(text)) info.Tags.Add(text!);
            }
        }

        return info;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static bool TryReadNumber(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var prop)) return false;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value)) return true;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return client;
    }
}