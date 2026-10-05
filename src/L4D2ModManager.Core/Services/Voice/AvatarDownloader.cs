using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace L4D2ModManager.Core.Services.Voice;

/// <summary>单个角色的头像下载结果。</summary>
public sealed record AvatarDownloadResult(string CharacterName, bool Success, string Message);

/// <summary>
/// 人物头像下载器。
///
/// 版权说明：程序不内置任何官方人物美术。运行时按角色维基词条抓取该角色首图，
/// 缓存到 &lt;数据目录&gt;\Avatars\&lt;代号&gt;.png|.jpg（下载一次，之后离线使用）。
/// 也可以把自己的图片命名为代号（如 mechanic.png）放进该目录覆盖。
///
/// 取图顺序：
///   1. 维基 MediaWiki API 的 pageimages → 直接拿到词条首图（比抓 HTML 稳，不易被 WAF 403）；
///   2. 中文维基（huijiwiki）同样用 API（英文词条 → 中文词条）；
///   3. Bing 图片搜索兜底，且**只接受文件名含角色名的结果**（避免抓到无关照片 / 图标包封面）。
/// </summary>
public static class AvatarDownloader
{
    private enum SourceKind
    {
        MediaWikiApi,
        BingImages,
    }

    private sealed record Source(SourceKind Kind, string Url, string[] Tokens, bool RequireTokenInUrl, int MinBytes);

    private static readonly Regex ApiSourcePattern = new(
        @"""source""\s*:\s*""(https?://[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BingImagePattern = new(
        @"murl(?:&quot;|"")?\s*:\s*(?:&quot;|"")(https?://[^""&\\]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] BadWords =
    {
        "logo", "wordmark", "site-", "favicon", "sprite", "blank", "noimage", "no_image",
        "question", "button", "pencil", "transparent", "achievement", "banner", "placeholder",
    };

    private static IEnumerable<Source> SourcesFor(VoiceCharacterInfo info)
    {
        yield return Api("left4dead.fandom.com", info.EnglishName);

        yield return Api("left4dead.huijiwiki.com", info.EnglishName);
        yield return Api("left4dead.huijiwiki.com", info.ChineseName);

        yield return new Source(
            SourceKind.BingImages,
            "https://cn.bing.com/images/search?q=" +
            Uri.EscapeDataString("\"Left 4 Dead 2\" " + info.EnglishName + " survivor portrait png"),
            new[] { info.EnglishName },
            RequireTokenInUrl: true,
            MinBytes: 8192);

        yield return new Source(
            SourceKind.BingImages,
            "https://cn.bing.com/images/search?q=" +
            Uri.EscapeDataString("求生之路2 " + info.ChineseName + " 生还者 头像"),
            new[] { info.ChineseName, info.EnglishName },
            RequireTokenInUrl: true,
            MinBytes: 8192);
    }

    private static Source Api(string host, string title) => new(
        SourceKind.MediaWikiApi,
        "https://" + host + "/api.php?action=query&format=json&formatversion=2&redirects=1" +
        "&prop=pageimages&piprop=original%7Cthumbnail&pithumbsize=512&titles=" + Uri.EscapeDataString(title),
        new[] { title },
        RequireTokenInUrl: false,
        MinBytes: 2048);

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

            progress?.Report("正在获取 " + info.EnglishName + " 的头像…");
            results.Add(await DownloadOneAsync(http, manager, info, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

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
        var problems = new List<string>();

        foreach (var source in SourcesFor(info))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var body = await http.GetStringAsync(source.Url, cancellationToken).ConfigureAwait(false);

                var raw = source.Kind == SourceKind.MediaWikiApi
                    ? ApiSourcePattern.Matches(body).Select(m => m.Groups[1].Value)
                    : BingImagePattern.Matches(body).Select(m => m.Groups[1].Value);

                var ordered = raw
                    .Select(WebUtility.HtmlDecode)
                    .Select(url => url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url)
                    .Where(url => url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    .Where(url => !BadWords.Any(bad => url.Contains(bad, StringComparison.OrdinalIgnoreCase)))
                    .Where(url => !source.RequireTokenInUrl ||
                                  source.Tokens.Any(token => url.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();

                if (ordered.Count == 0)
                {
                    problems.Add(Host(source.Url) + "：没有匹配的图片");
                    continue;
                }

                foreach (var url in ordered)
                {
                    var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                    var extension = DetectImageExtension(bytes);
                    if (extension == null || bytes.Length < source.MinBytes) continue;

                    Save(manager, info, bytes, extension);
                    Log.Info("[语音头像] 已下载 " + info.EnglishName + " 头像：" + info.Codename + extension +
                             "（" + bytes.Length / 1024 + " KB，来源 " + Host(url) + "）");

                    return new AvatarDownloadResult(info.EnglishName, true, "已下载（" + bytes.Length / 1024 + " KB）");
                }

                problems.Add(Host(source.Url) + "：图片都不合格（格式或体积）");
            }
            catch (Exception ex)
            {
                problems.Add(Host(source.Url) + "：" + Shorten(ex.Message));
                Log.Warn("[语音头像] " + info.EnglishName + " 从 " + source.Url + " 获取失败：" + ex.Message);
            }
        }

        return new AvatarDownloadResult(info.EnglishName, false,
            problems.Count == 0 ? "没有可用来源" : string.Join("；", problems));
    }

    private static void Save(VoiceManager manager, VoiceCharacterInfo info, byte[] bytes, string extension)
    {
        Directory.CreateDirectory(manager.AvatarDirectory);

        foreach (var other in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var existing = Path.Combine(manager.AvatarDirectory, info.Codename + other);
            if (File.Exists(existing)) File.Delete(existing);
        }

        File.WriteAllBytes(Path.Combine(manager.AvatarDirectory, info.Codename + extension), bytes);
    }

    private static string Host(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text.Substring(0, 80) + "…";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/html,application/xhtml+xml,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.Referrer = new Uri("https://cn.bing.com/");
        return client;
    }

    public static string? DetectImageExtension(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length > 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
        if (bytes.Length > 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".webp";
        return null;
    }
}