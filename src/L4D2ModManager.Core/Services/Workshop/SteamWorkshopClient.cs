using System.Text.Json;
using System.Text.Json.Serialization;
using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Workshop;

/// <summary>工坊查询结果包装（区分“接口错误”和“条目不存在的”）。</summary>
public sealed class WorkshopQueryResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<WorkshopItemInfo> Items { get; set; } = new();

    public WorkshopItemInfo? First => Items.FirstOrDefault();

    public static WorkshopQueryResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Steam 创意工坊 Web API 客户端。
///   · ISteamRemoteStorage/GetPublishedFileDetails —— 无需 API Key，可获取标题、作者、预览图、UGC 直链
///   · IPublishedFileService/QueryFiles        —— 需要 API Key，可选（页面内搜索由内置浏览器完成）
///   · ISteamUser/GetPlayerSummaries           —— 需要 API Key，可选（把作者 SteamID 换成昵称）
/// </summary>
public sealed class SteamWorkshopClient : IDisposable
{
    public const string GetDetailsEndpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    public const string QueryFilesEndpoint = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/";
    public const string PlayerSummariesEndpoint = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/";

    /// <summary>Left 4 Dead 2 的消费端 AppId。</summary>
    public const int L4D2AppId = 550;

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyProvider;
    private bool _disposed;

    public SteamWorkshopClient(Func<string?>? apiKeyProvider = null, HttpClient? http = null)
    {
        _apiKeyProvider = apiKeyProvider ?? (() => null);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    public HttpClient Http => _http;

    /// <summary>批量获取条目详情（每次最多 100 个）。</summary>
    public async Task<WorkshopQueryResult> GetDetailsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var list = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

        if (list.Count == 0) return WorkshopQueryResult.Fail("没有有效的创意工坊 ID");

        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("itemcount", list.Count.ToString()),
            };
            for (int i = 0; i < list.Count; i++)
                form.Add(new KeyValuePair<string, string>($"publishedfileids[{i}]", list[i]));

            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(GetDetailsEndpoint, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return WorkshopQueryResult.Fail($"创意工坊接口返回 HTTP {(int)response.StatusCode}；请检查网络（国内网络可能需要加速器）");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseDetails(json, list);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"获取创意工坊信息失败: {ex.Message}");
            return WorkshopQueryResult.Fail($"网络请求失败：{ex.Message}");
        }
    }

    /// <summary>获取单个条目详情。</summary>
    public async Task<WorkshopItemInfo?> GetDetailAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await GetDetailsAsync(new[] { id }, cancellationToken).ConfigureAwait(false);
        return result.First;
    }

    /// <summary>关键字搜索（需要 API Key）。</summary>
    public async Task<WorkshopQueryResult> QueryAsync(string query, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key))
            return WorkshopQueryResult.Fail("未配置 Steam Web API Key，无法使用接口搜索；请使用内置创意工坊页面搜索。");

        try
        {
            var url = $"{QueryFilesEndpoint}?key={Uri.EscapeDataString(key)}&query_type=0&page={page}&numperpage={Math.Clamp(pageSize, 1, 100)}" +
                      $"&creator_appid={L4D2AppId}&appid={L4D2AppId}&search_text={Uri.EscapeDataString(query)}&return_details=true";

            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return WorkshopQueryResult.Fail($"搜索失败：HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var result = new WorkshopQueryResult { Success = true };

            if (document.RootElement.TryGetProperty("response", out var payload) &&
                payload.TryGetProperty("publishedfiledetails", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in items.EnumerateArray())
                    result.Items.Add(ParseItem(element));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WorkshopQueryResult.Fail($"搜索失败：{ex.Message}");
        }
    }

    /// <summary>把 SteamID 解析成昵称（需要 API Key），失败时返回空字典。</summary>
    public async Task<Dictionary<string, string>> ResolveCreatorNamesAsync(IEnumerable<string> steamIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var key = _apiKeyProvider();
        var ids = steamIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(100).ToList();
        if (string.IsNullOrWhiteSpace(key) || ids.Count == 0) return result;

        try
        {
            var url = $"{PlayerSummariesEndpoint}?key={Uri.EscapeDataString(key)}&steamids={string.Join(',', ids)}";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return result;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("response", out var payload)) return result;
            if (!payload.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return result;

            foreach (var player in players.EnumerateArray())
            {
                var steamId = GetString(player, "steamid");
                var name = GetString(player, "personaname");
                if (!string.IsNullOrWhiteSpace(steamId) && !string.IsNullOrWhiteSpace(name))
                    result[steamId!] = name!;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析作者昵称失败: {ex.Message}");
        }

        return result;
    }

    private static WorkshopQueryResult ParseDetails(string json, IReadOnlyList<string> requestedIds)
    {
        var result = new WorkshopQueryResult { Success = true };

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("response", out var payload) ||
                !payload.TryGetProperty("publishedfiledetails", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return WorkshopQueryResult.Fail("创意工坊返回内容无法解析");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in items.EnumerateArray())
            {
                var item = ParseItem(element);
                if (string.IsNullOrWhiteSpace(item.PublishedFileId)) continue;
                seen.Add(item.PublishedFileId);
                result.Items.Add(item);
            }

            // 接口没有返回的 ID（条目被删除 / 隐藏）
            foreach (var id in requestedIds.Where(id => !seen.Contains(id)))
            {
                result.Items.Add(new WorkshopItemInfo
                {
                    PublishedFileId = id,
                    Title = $"未找到条目 {id}",
                    Available = false,
                    Error = "创意工坊中不存在该条目（可能已被作者删除或设为私密）",
                });
            }
        }
        catch (Exception ex)
        {
            return WorkshopQueryResult.Fail($"解析创意工坊数据失败：{ex.Message}");
        }

        return result;
    }

    private static WorkshopItemInfo ParseItem(JsonElement element)
    {
        var id = GetString(element, "publishedfileid") ?? string.Empty;
        int resultCode = (int)GetLong(element, "result");

        var info = new WorkshopItemInfo
        {
            PublishedFileId = id,
            Title = GetString(element, "title") ?? string.Empty,
            Description = GetString(element, "description") ?? string.Empty,
            Author = GetString(element, "creator") ?? string.Empty,
            PreviewUrl = NormalizeUrl(GetString(element, "preview_url")),
            FileUrl = NormalizeUrl(GetString(element, "file_url")),
            FileName = GetString(element, "filename"),
            FileSize = GetLong(element, "file_size"),
            Subscriptions = (int)GetLong(element, "subscriptions"),
            Favorited = (int)GetLong(element, "favorited"),
            Views = (int)GetLong(element, "views"),
            Banned = GetBool(element, "banned"),
            TimeCreatedUtc = FromUnix(GetLong(element, "time_created")),
            TimeUpdatedUtc = FromUnix(GetLong(element, "time_updated")),
        };

        if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var value = tag.ValueKind == JsonValueKind.Object ? GetString(tag, "tag") : tag.GetString();
                if (!string.IsNullOrWhiteSpace(value)) info.Tags.Add(value!);
            }
        }

        if (resultCode != 1)
        {
            info.Available = false;
            info.Error = resultCode switch
            {
                2 => "条目被禁止访问",
                9 => "条目不存在或已删除",
                _ => $"创意工坊返回状态码 {resultCode}",
            };
        }

        if (string.IsNullOrWhiteSpace(info.Title) && info.Available)
            info.Title = $"创意工坊 Mod #{id}";

        return info;
    }

    /// <summary>从任意输入中解析创意工坊 ID：纯数字 / 链接 / steam:// 协议。</summary>
    public static string? ParseWorkshopId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();

        if (text.All(char.IsDigit) && text.Length >= 5 && text.Length <= 20)
            return text;

        var patterns = new[]
        {
            @"[?&]id=(\d{5,20})",
            @"CommunityFilePage/(\d{5,20})",
            @"filedetails/\?id=(\d{5,20})",
            @"sharedfiles/(\d{5,20})",
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
        }

        // 兜底：任意位置的一长串数字
        var fallback = System.Text.RegularExpressions.Regex.Match(text, @"(\d{8,20})");
        return fallback.Success ? fallback.Groups[1].Value : null;
    }

    /// <summary>判断文本是否像创意工坊链接/ID。</summary>
    public static bool IsWorkshopReference(string? input) => ParseWorkshopId(input) != null;

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var value = url.Trim();
        if (value.StartsWith("//")) return "https:" + value;
        return value;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static long GetLong(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return long.TryParse(text, out var value) ? value : 0;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt64(out var value) && value != 0,
            JsonValueKind.String => property.GetString() is "1" or "true",
            _ => false,
        };
    }

    private static DateTime? FromUnix(long seconds)
    {
        if (seconds <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
