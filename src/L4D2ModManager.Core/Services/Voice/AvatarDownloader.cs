using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace L4D2ModManager.Core.Services.Voice;

/// <summary>单个角色的头像下载结果。</summary>
public sealed record AvatarDownloadResult(string CharacterName, bool Success, string Message);

/// <summary>
/// 人物头像下载器。
///
/// 版权说明：程序**不内置**任何官方人物美术。这里在运行时从同人维基词条里抓取
/// 该角色的人物图，缓存到 &lt;数据目录&gt;\Avatars\&lt;代号&gt;.png|.jpg 供界面显示。
/// 你也可以把自己的图片命名为代号（例如 mechanic.png）放进该目录覆盖它。
///
/// 兼容性：按顺序尝试多个来源（国际维基 / 中文维基），任一可达即可；
/// 抓取时用通用 &lt;img&gt; 规则，不依赖具体 CDN 域名。
/// </summary>
public static class AvatarDownloader
{
    private sealed record Source(string PageUrl, string[] Tokens);

    private static readonly Regex ImagePattern = new(
        @"<img[^>]+?(?:src|data-src)=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] BadWords =
    {
        "logo", "wordmark", "site-", "favicon", "sprite", "blank", "noimage", "no_image",
        "question", "button", "icon", "achievement", ".svg", "special:", "site_logo",
        "edit", "pencil", "transparent",
    };

    /// <summary>该角色可尝试的来源列表。</summary>
    private static IEnumerable<Source> SourcesFor(VoiceCharacterInfo info)
    {
        yield return new Source(
            "https://left4dead.fandom.com/wiki/" + Uri.EscapeDataString(info.EnglishName),
            new[] { info.EnglishName, info.ChineseName });

        yield return new Source(
            "https://left4dead.huijiwiki.com/wiki/" + Uri.EscapeDataString(info.ChineseName),
            new[] { info.ChineseName, info.EnglishName });
    }

    /// <summary>把所有缺失的头像下载下来。</summary>
    public static async Task<IReadOnlyList<AvatarDownloadResult>> DownloadAllAsync(
        VoiceManager manager,
        bool overwrite = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AvatarDownloadResult>();
        using var http = CreateClient();

        foreach (var info in VoiceCharacters.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!overwrite && manager.FindAvatar(info) != null)
            {
                results.Add(new AvatarDownloadResult(info.EnglishName, true, "已有头像，跳过"));
                continue;
            }

            progress?.Report($"正在获取 {info.EnglishName} 的头像…");
            results.Add(await DownloadOneAsync(http, manager, info, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>下载单个角色的头像。</summary>
    public static async Task<AvatarDownloadResult> DownloadOneAsync(
        VoiceManager manager,
        VoiceCharacterInfo info,
        CancellationToken cancellationToken = default)
    {
        using var http = CreateClient();
        return await DownloadOneAsync(http, manager, info, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AvatarDownloadResult> DownloadOneAsync(
        HttpClient http,
        VoiceManager manager,
        VoiceCharacterInfo info,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        foreach (var source in SourcesFor(info))
        {
            try
            {
                var html = await http.GetStringAsync(source.PageUrl, cancellationToken).ConfigureAwait(false);

                var candidates = ImagePattern.Matches(html)
                    .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
                    .Select(url => url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url)
                    .Where(url => url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    .Where(url => !BadWords.Any(bad => url.Contains(bad, StringComparison.OrdinalIgnoreCase)))
                    .Where(url => source.Tokens.Any(token => url.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList();

                foreach (var url in candidates)
                {
                    var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                    var extension = DetectImageExtension(bytes);
                    if (extension == null) continue;   // 不是图片（可能是 HTML 错误页），换下一个

                    Directory.CreateDirectory(manager.AvatarDirectory);
                    foreach (var other in new[] { ".png", ".jpg", ".jpeg", ".webp" })
                    {
                        var existing = Path.Combine(manager.AvatarDirectory, info.Codename + other);
                        if (File.Exists(existing)) File.Delete(existing);
                    }

                    var target = Path.Combine(manager.AvatarDirectory, info.Codename + extension);
                    await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);

                    Log.Info($"[语音头像] 已下载 {info.EnglishName} 头像：{Path.GetFileName(target)}（{bytes.Length / 1024} KB，来源 {new Uri(url).Host}）");
                    return new AvatarDownloadResult(info.EnglishName, true, $"已下载（{bytes.Length / 1024} KB）");
                }

                errors.Add($"{new Uri(source.PageUrl).Host}：没有可用图片");
            }
            catch (Exception ex)
            {
                errors.Add($"{new Uri(source.PageUrl).Host}：{ex.Message}");
                Log.Warn($"[语音头像] {info.EnglishName} 从 {source.PageUrl} 获取失败：{ex.Message}");
            }
        }

        var reason = errors.Count == 0 ? "没有可用来源" : string.Join("；", errors);
        return new AvatarDownloadResult(info.EnglishName, false, reason);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) L4D2ModManager/1.0");
        return client;
    }

    /// <summary>按文件头判断真实图片格式（避免把 HTML 错误页当图片存下来）。</summary>
    public static string? DetectImageExtension(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";

        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";

        if (bytes.Length > 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return ".webp";

        if (bytes.Length > 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return ".webp";   // GIF 头：按可显示格式保存（WPF 能显示，只是不校验为 GIF 扩展名）

        return null;
    }
}
