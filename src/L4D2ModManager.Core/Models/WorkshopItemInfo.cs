using System.Text.Json.Serialization;

namespace L4D2ModManager.Core.Models;

/// <summary>Steam 创意工坊条目信息（来自 ISteamRemoteStorage/GetPublishedFileDetails）。</summary>
public sealed class WorkshopItemInfo
{
    public string PublishedFileId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>作者 SteamID64。</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>作者昵称（需要 Web API Key 才能解析，可空）。</summary>
    public string? CreatorName { get; set; }

    public string? PreviewUrl { get; set; }

    /// <summary>UGC 直链（部分条目提供，可直接 HTTP 下载）。</summary>
    public string? FileUrl { get; set; }

    public string? FileName { get; set; }
    public long FileSize { get; set; }

    public DateTime? TimeCreatedUtc { get; set; }
    public DateTime? TimeUpdatedUtc { get; set; }

    public List<string> Tags { get; set; } = new();

    public int Subscriptions { get; set; }
    public int Favorited { get; set; }
    public int Views { get; set; }

    public bool Banned { get; set; }

    /// <summary>是否成功获取到信息。</summary>
    public bool Available { get; set; } = true;

    public string? Error { get; set; }

    [JsonIgnore] public string PageUrl => BuildPageUrl(PublishedFileId);

    /// <summary>由工坊 ID 拼出物品页面地址。</summary>
    public static string BuildPageUrl(string publishedFileId) =>
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}";

    [JsonIgnore] public string TagText => Tags.Count == 0 ? "无标签" : string.Join(" / ", Tags);

    [JsonIgnore] public string SizeText => ModItem.FormatSize(FileSize);

    [JsonIgnore]
    public string ShortDescription
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Description) ? "（无描述）" : Description;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 300 ? text : text[..300] + "…";
        }
    }

    [JsonIgnore] public string AuthorText => string.IsNullOrWhiteSpace(CreatorName) ? Author : $"{CreatorName} ({Author})";

    /// <summary>是否有直链可下载。</summary>
    [JsonIgnore] public bool HasDirectUrl => !string.IsNullOrWhiteSpace(FileUrl);
}
